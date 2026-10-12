using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Explicitly enrolled module read path. Does not grant permissions, register an adapter,
/// or change the direct reader's permission requirements. Unverified captures never escape.</summary>
public sealed class SqlServerAgentActivityModuleCaptureReader
{
    public static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(10);
    private const string Refused = "Agent module authorization or identity is unavailable; private values withheld.";
    private readonly ISqlAgentModuleEnrollmentResolver resolver;
    private readonly ISqlAgentModuleSource source;
    private readonly TimeProvider clock;
    private readonly Func<byte[]> tokenProvider;

    public SqlServerAgentActivityModuleCaptureReader(ISqlAgentModuleEnrollmentResolver resolver, TimeProvider? timeProvider = null)
        : this(resolver, new SqlAgentModuleSource(), ActualToken, timeProvider) { }

    internal SqlServerAgentActivityModuleCaptureReader(ISqlAgentModuleEnrollmentResolver resolver,
        ISqlAgentModuleSource source, Func<byte[]> tokenProvider, TimeProvider? timeProvider = null)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        clock = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<SqlAgentModuleCaptureResult> ReadAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan budget = request.Timeout.Value < MaximumBudget ? request.Timeout.Value : MaximumBudget;
        long started = clock.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        byte[]? sid = null;
        try
        {
            RequireProfile(request);
            SqlAgentModuleEnrollment enrolled = await ResolveAsync(null).ConfigureAwait(false);
            sid = tokenProvider().ToArray();
            string actualSid = CanonicalSid(sid);
            if (!string.Equals(actualSid, enrolled.ExpectedWindowsSid, StringComparison.Ordinal)) throw new UnauthorizedAccessException(Refused);
            Remaining();
            var pins = new SqlAgentModuleMetadataPins(enrolled.Binding.ObjectId, enrolled.Binding.CertificateId,
                enrolled.Binding.DefinitionSha256, enrolled.Binding.PublicCertificateSha256, enrolled.Binding.ThumbprintHex);
            SqlAgentActivityCapture capture;
            await using (ISqlAgentModuleSession session = await source.OpenAsync(request, Remaining(), deadline.Token).ConfigureAwait(false))
            {
                Guid clientConnectionId = session.ClientConnectionId;
                int serverProcessId = session.ServerProcessId;
                RequireOwnedSession();
                var identityBefore = await session.AttestAsync(sid, Remaining(), deadline.Token).ConfigureAwait(false);
                RequireOwnedSession();
                RequireIdentity(identityBefore, enrolled.Binding, serverProcessId);
                var before = await session.MetadataAsync(pins, Remaining(), deadline.Token).ConfigureAwait(false);
                RequireOwnedSession();
                RequireMetadataIdentity(before, identityBefore, clientConnectionId);
                if (!before.CaptureEligible) throw new UnauthorizedAccessException(Refused);
                // Fresh authoritative authorization immediately before constructing/sending fixed EXEC.
                _ = await ResolveAsync(enrolled).ConfigureAwait(false);
                RequireOwnedSession();
                capture = await session.CaptureAsync(request, Remaining(), deadline.Token).ConfigureAwait(false);
                RequireOwnedSession();
                if (capture.TargetId != request.TargetId || capture.TargetRevision != request.TargetRevision || capture.RunId != request.RunId)
                    throw new InvalidDataException(Refused);
                var after = await session.MetadataAsync(pins, Remaining(), deadline.Token).ConfigureAwait(false);
                RequireOwnedSession();
                SqlAgentModuleMetadataProbe.RequireStableSigned(before, after);
                var identityAfter = await session.AttestAsync(sid, Remaining(), deadline.Token).ConfigureAwait(false);
                RequireOwnedSession();
                RequireIdentity(identityAfter, enrolled.Binding, serverProcessId);
                RequireMetadataIdentity(after, identityAfter, clientConnectionId);
                if (identityAfter.ObservedAtUtc < identityBefore.ObservedAtUtc ||
                    (identityBefore with { ObservedAtUtc = identityAfter.ObservedAtUtc }) != identityAfter)
                    throw new UnauthorizedAccessException(Refused);
                // Buffer remains private until post-capture authorization, identity and metadata agree.
                _ = await ResolveAsync(enrolled).ConfigureAwait(false);
                RequireOwnedSession();

                void RequireOwnedSession()
                {
                    session.RequireConnection();
                    if (clientConnectionId == Guid.Empty || serverProcessId is <= 0 or > 32767 ||
                        session.ClientConnectionId != clientConnectionId || session.ServerProcessId != serverProcessId)
                        throw new UnauthorizedAccessException(Refused);
                }
            }
            Remaining();
            RequireProfile(request);
            if (!enrolled.IsActiveAt(clock.GetUtcNow())) throw new UnauthorizedAccessException(Refused);
            return new SqlAgentModuleCaptureResult(capture, enrolled);

            async ValueTask<SqlAgentModuleEnrollment> ResolveAsync(SqlAgentModuleEnrollment? previous)
            {
                Remaining(); RequireProfile(request);
                var snapshot = await resolver.ResolveAsync(request.TargetId, request.TargetRevision, deadline.Token).ConfigureAwait(false);
                Remaining();
                SqlAgentModuleEnrollment current = snapshot?.Enrollment ?? throw new UnauthorizedAccessException(Refused);
                if (snapshot!.Revision != current.Revision || current.TargetId != request.TargetId || current.TargetRevision != request.TargetRevision ||
                    !current.IsActiveAt(clock.GetUtcNow()) || previous is not null && !previous.SameAuthorization(current))
                    throw new UnauthorizedAccessException(Refused);
                var endpoint = request.ConnectionPolicy.Endpoint;
                if (endpoint.InstanceName is not null || endpoint.TcpPort != current.Binding.TcpPort ||
                    !string.Equals(endpoint.HostName.Value, current.Binding.EndpointHost, StringComparison.OrdinalIgnoreCase) ||
                    request.ConnectionPolicy.CertificateHostName is null ||
                    !string.Equals(request.ConnectionPolicy.CertificateHostName.Value, current.Binding.CertificateHost, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException(Refused);
                return current;
            }
            TimeSpan Remaining()
            {
                deadline.Token.ThrowIfCancellationRequested();
                TimeSpan remaining = budget - clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("The original Agent module deadline expired.");
                return remaining;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The original Agent module deadline expired."); }
        catch (SqlException error) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(error.Number))
        { throw new UnauthorizedAccessException("Agent module SQL permissions are unavailable."); }
        catch (SqlException error) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(error.Number))
        { throw new TimeoutException("The original Agent module command deadline expired."); }
        catch (SqlException error)
        { throw new InvalidDataException("Agent module SQL command failed; number=" + error.Number.ToString(CultureInfo.InvariantCulture) + "."); }
        finally { if (sid is not null) CryptographicOperations.ZeroMemory(sid); }
    }

    private void RequireProfile(CollectorExecutionRequest request)
    {
        CapabilityProfile profile = request.CapabilityProfile;
        DateTimeOffset now = clock.GetUtcNow();
        if (profile.TargetId != request.TargetId || profile.TargetRevision != request.TargetRevision || !profile.IsUsable ||
            profile.CheckedAtUtc > now || profile.ValidUntilUtc <= now) throw new UnauthorizedAccessException(Refused);
        if (profile.ServerIdentity is not { Version.Major: 17, Platform: SqlServerPlatform.Windows,
                EngineEdition: SqlServerEngineEdition.Standard or SqlServerEngineEdition.Enterprise })
            throw new PlatformNotSupportedException("Agent module collection requires Windows SQL Server 2025 Standard or Enterprise.");
        if (!profile.TransportEncrypted || profile.IsSysAdmin || profile.AuthenticationScheme is not (SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm))
            throw new UnauthorizedAccessException(Refused);
    }
    private static void RequireIdentity(SqlAgentModuleIdentity identity, SqlAgentModuleBinding binding, int serverProcessId)
    {
        if (identity.ServerConnectionId == Guid.Empty || identity.SessionId != serverProcessId ||
            identity.ServerName != binding.ServerName || identity.MachineName != binding.MachineName || (identity.InstanceName ?? string.Empty) != binding.InstanceName)
            throw new UnauthorizedAccessException(Refused);
    }
    private static void RequireMetadataIdentity(SqlAgentModuleMetadataReceipt metadata, SqlAgentModuleIdentity identity, Guid connectionId)
    {
        if (metadata.ClientConnectionId != connectionId || metadata.SessionId != identity.SessionId)
            throw new UnauthorizedAccessException(Refused);
    }
    private static byte[] ActualToken()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent module collection requires a Windows token.");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User ?? throw new UnauthorizedAccessException(Refused);
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0); return bytes;
    }
    internal static string CanonicalSid(ReadOnlySpan<byte> sid)
    {
        if (sid.Length < 12 || sid.Length > 68 || sid[0] != 1 || sid[1] is < 1 or > 15 || sid.Length != 8 + sid[1] * 4)
            throw new UnauthorizedAccessException(Refused);
        ulong authority = 0;
        for (int index = 2; index < 8; index++) authority = (authority << 8) | sid[index];
        var pieces = new string[2 + sid[1]];
        pieces[0] = "S-1"; pieces[1] = authority.ToString(CultureInfo.InvariantCulture);
        for (int index = 0; index < sid[1]; index++) pieces[index + 2] = BinaryPrimitives.ReadUInt32LittleEndian(sid.Slice(8 + index * 4, 4)).ToString(CultureInfo.InvariantCulture);
        return string.Join('-', pieces);
    }
}

internal interface ISqlAgentModuleSource
{
    ValueTask<ISqlAgentModuleSession> OpenAsync(CollectorExecutionRequest request, TimeSpan budget, CancellationToken token);
}
internal interface ISqlAgentModuleSession : IAsyncDisposable
{
    Guid ClientConnectionId { get; }
    int ServerProcessId { get; }
    void RequireConnection();
    ValueTask<SqlAgentModuleIdentity> AttestAsync(ReadOnlyMemory<byte> expectedSid, TimeSpan budget, CancellationToken token);
    ValueTask<SqlAgentModuleMetadataReceipt> MetadataAsync(SqlAgentModuleMetadataPins pins, TimeSpan budget, CancellationToken token);
    ValueTask<SqlAgentActivityCapture> CaptureAsync(CollectorExecutionRequest request, TimeSpan budget, CancellationToken token);
}
internal sealed record SqlAgentModuleIdentity(Guid ServerConnectionId, int SessionId, string ServerName,
    string MachineName, string? InstanceName, DateTime ObservedAtUtc);
