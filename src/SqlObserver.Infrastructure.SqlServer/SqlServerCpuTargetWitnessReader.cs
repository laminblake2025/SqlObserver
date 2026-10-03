using System.Data;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

public sealed record SqlCpuWitnessBracket<T>(SqlCpuTargetWitness Before, T NativeEvidence,
    SqlCpuTargetWitness After, SqlCpuWitnessGap ContinuityGap)
{
    public bool TargetComparisonAdmitted { get; }
}

/// <summary>Staged source only. Brackets native sampling on one owned SQL connection;
/// never reroutes an endpoint or relaxes the integrated TLS connection policy.</summary>
public sealed class SqlServerCpuTargetWitnessReader
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(10);
    private readonly IdentityFingerprintKey key;
    private readonly ISqlCpuWitnessSource source;
    private readonly ConcurrentDictionary<Guid, Task> quarantined = new();
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    private static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(1);

    public SqlServerCpuTargetWitnessReader(IdentityFingerprintKey key)
        : this(key, new SqlCpuWitnessSource()) { }

    internal SqlServerCpuTargetWitnessReader(IdentityFingerprintKey key, ISqlCpuWitnessSource source)
    {
        this.key = key ?? throw new ArgumentNullException(nameof(key));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async ValueTask<SqlCpuWitnessBracket<T>> ReadBracketAsync<T>(CollectorExecutionRequest request,
        Func<SqlCpuTargetWitness, CancellationToken, ICpuNativeSamplingOperation<T>> startNative,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(startNative);
        cancellationToken.ThrowIfCancellationRequested();
        if (!active.TryAdd(request.TargetId.Value, 0))
            throw new InvalidOperationException("A CPU witness operation already owns this target.");
        try { return await ReadOwnedAsync(request, startNative, cancellationToken).ConfigureAwait(false); }
        finally { active.TryRemove(request.TargetId.Value, out _); }
    }

    private async ValueTask<SqlCpuWitnessBracket<T>> ReadOwnedAsync<T>(CollectorExecutionRequest request,
        Func<SqlCpuTargetWitness, CancellationToken, ICpuNativeSamplingOperation<T>> startNative,
        CancellationToken cancellationToken)
    {
        RequireProfile(request);
        if (quarantined.TryGetValue(request.TargetId.Value, out Task? outstanding))
        {
            if (!outstanding.IsCompleted) throw new CpuNativeTerminationUnprovenException();
            quarantined.TryRemove(request.TargetId.Value, out _);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout.Value < MaximumDuration ? request.Timeout.Value : MaximumDuration);
        await using ISqlCpuWitnessSession session = await source.OpenAsync(request, key, deadline.Token).ConfigureAwait(false);
        SqlCpuTargetWitness before = await session.ReadAsync(1, deadline.Token).ConfigureAwait(false);
        RequireRequest(request, before, 1);
        deadline.Token.ThrowIfCancellationRequested();
        ICpuNativeSamplingOperation<T> operation = startNative(before, deadline.Token)
            ?? throw new InvalidDataException("Native CPU operation is missing.");
        T? native = default;
        Exception? failure = null;
        try
        {
            native = await operation.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        if (!operation.Completion.IsCompleted)
        {
            try
            {
                operation.Cancel();
                _ = await operation.TerminateAsync(TerminationGrace).AsTask().WaitAsync(TerminationGrace, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { /* Completion below remains the termination proof. */ }
        }
        Task? cleanup = null;
        bool disposed = false;
        try
        {
            cleanup = operation.DisposeAsync().AsTask();
            await cleanup.WaitAsync(TerminationGrace, CancellationToken.None).ConfigureAwait(false);
            disposed = true;
        }
        catch (Exception exception)
        { cleanup ??= Task.FromException(exception); /* Retain synchronous cleanup failure for the ownership proof. */ }
        if (!operation.Completion.IsCompleted || !disposed)
        {
            quarantined[request.TargetId.Value] = disposed ? operation.Completion
                : CleanupProofAsync(operation, cleanup!);
            throw new CpuNativeTerminationUnprovenException();
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        deadline.Token.ThrowIfCancellationRequested();
        SqlCpuTargetWitness after = await session.ReadAsync(2, deadline.Token).ConfigureAwait(false);
        RequireRequest(request, after, 2);
        deadline.Token.ThrowIfCancellationRequested();
        return new(before, native!, after, SqlCpuWitnessContinuity.Evaluate(before, after));
    }

    private static async Task CleanupProofAsync<T>(ICpuNativeSamplingOperation<T> operation, Task cleanup)
    {
        try { _ = await operation.Completion.ConfigureAwait(false); } catch (Exception) { /* A faulted native operation has stopped. */ }
        try { await cleanup.ConfigureAwait(false); }
        catch (CpuNativeTerminationUnprovenException)
        {
            try
            {
                // A delayed helper has now stopped; retry the idempotent cleanup once.
                await operation.DisposeAsync().AsTask().WaitAsync(TerminationGrace, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            { await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.ConfigureAwait(false); }
        }
        catch (Exception)
        {
            // Failed cleanup cannot be replaced by a new operation on this target.
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.ConfigureAwait(false);
        }
    }

    private static void RequireRequest(CollectorExecutionRequest request, SqlCpuTargetWitness receipt, int ordinal)
    {
        if (receipt.TargetId != request.TargetId || receipt.TargetRevision != request.TargetRevision ||
            receipt.RunId != request.RunId || receipt.Ordinal != ordinal)
            throw new InvalidDataException("SQL CPU witness does not belong to the exact collector execution.");
    }

    private static void RequireProfile(CollectorExecutionRequest request)
    {
        CapabilityProfile profile = request.CapabilityProfile;
        SqlServerIdentity? identity = profile.ServerIdentity;
        if (identity is null || identity.Version.Major is < 15 or > 17 || identity.Platform != SqlServerPlatform.Windows)
            throw new PlatformNotSupportedException("SQL CPU witnesses require SQL Server 2019 through 2025 on Windows.");
        string permission = identity.Version.Major == 15 ? "server.view-state" : "server.view-performance-state";
        if (!profile.TransportEncrypted || profile.IsSysAdmin != false ||
            profile.AuthenticationScheme is not (SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm) ||
            !profile.Permissions.Any(item => item.PermissionId.Value == permission &&
                item.Scope == PermissionEvidenceScope.Server && item.Outcome == PermissionEvidenceOutcome.Granted))
            throw new UnauthorizedAccessException("SQL CPU witnesses require the bounded integrated monitoring profile.");
    }

    internal static async ValueTask<SqlCpuTargetWitness> ParseAsync(CollectorExecutionRequest request,
        IOperationalHealthRowReader reader, IdentityFingerprintKey key, Guid sessionId,
        Guid clientConnectionId, int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.FieldCount != 12 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("SQL CPU witness requires exactly one twelve-field row.");
        if (reader.IsDBNull(0)) throw new InvalidDataException("SQL CPU witness source time is missing.");
        DateTime observed = reader.GetDateTime(0);
        if (observed.Kind == DateTimeKind.Local) throw new InvalidDataException("SQL source UTC cannot carry a local kind.");
        string? Name(int index)
        {
            string? value = reader.IsDBNull(index) ? null : reader.GetString(index);
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Trim() != value || value.Any(char.IsControl)))
                throw new InvalidDataException("SQL CPU private source identity is invalid.");
            return value;
        }
        string? node = Name(1), logical = Name(2);
        int? Int(int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
        long? Long(int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
        int? processId = Int(3);
        DateTime? start = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
        long? startTicks = Long(5), currentTicks = Long(6), userTime = Long(7), kernelTime = Long(8);
        int? processors = Int(9);
        string? transport = Name(10);
        if (reader.IsDBNull(11) || reader.GetInt32(11) != 1)
            throw new InvalidDataException("SQL CPU witness schema is missing or unsupported.");
        var engine = new ServerLifecycleEngineEvidence(
            node is null ? null : ServerLifecycleIdentityFingerprint.PhysicalNode(node, key),
            logical is null ? null : ServerLifecycleIdentityFingerprint.LogicalInstance(logical, key),
            processId, start, startTicks, currentTicks);
        SqlCpuPhysicalTransport kind = transport switch
        {
            "TCP" => SqlCpuPhysicalTransport.Tcp,
            "Named pipe" => SqlCpuPhysicalTransport.NamedPipe,
            "Shared memory" => SqlCpuPhysicalTransport.SharedMemory,
            null => SqlCpuPhysicalTransport.Unknown,
            _ => throw new InvalidDataException("SQL CPU physical transport is unsupported."),
        };
        var receipt = new SqlCpuTargetWitness(request.TargetId, request.TargetRevision, request.RunId,
            sessionId, clientConnectionId, ordinal, new DateTimeOffset(DateTime.SpecifyKind(observed, DateTimeKind.Utc)),
            engine, kind, userTime, kernelTime, processors);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("SQL CPU witness exceeds its one-row bound.");
        cancellationToken.ThrowIfCancellationRequested();
        return receipt;
    }

    internal static string LoadQuery(Assembly? assembly = null)
    {
        const string resource = "SqlObserver.Infrastructure.SqlServer.CpuWitnessSourceAssets.cpu.target-witness.sqlserver15-17-windows.v1.sql";
        const string checksum = "86c64863d16f99cf7fd2ac969356c11f307969b3a07a348bcbe8218b3acfce99";
        using Stream source = (assembly ?? typeof(SqlServerCpuTargetWitnessReader).Assembly).GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("The SQL CPU witness source is missing.");
        if (source.Length is <= 0 or > 4096) throw new InvalidDataException("The SQL CPU witness source exceeds its bound.");
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        byte[] value = bytes.ToArray();
        if (value.Contains((byte)'\r') || value.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            !Convert.ToHexString(SHA256.HashData(value)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQL CPU witness source must be approved UTF-8 LF.");
        return new UTF8Encoding(false, true).GetString(value);
    }
}

internal interface ISqlCpuWitnessSource
{
    ValueTask<ISqlCpuWitnessSession> OpenAsync(CollectorExecutionRequest request, IdentityFingerprintKey key, CancellationToken cancellationToken);
}

internal interface ISqlCpuWitnessSession : IAsyncDisposable
{
    ValueTask<SqlCpuTargetWitness> ReadAsync(int ordinal, CancellationToken cancellationToken);
}

internal sealed class SqlCpuWitnessSource : ISqlCpuWitnessSource
{
    public async ValueTask<ISqlCpuWitnessSession> OpenAsync(CollectorExecutionRequest request, IdentityFingerprintKey key, CancellationToken cancellationToken)
    {
        string query = SqlServerCpuTargetWitnessReader.LoadQuery();
        var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, cancellationToken).ConfigureAwait(false);
        return new Session(connection, request, key, query);
    }

    private sealed class Session(SqlConnection connection, CollectorExecutionRequest request,
        IdentityFingerprintKey key, string query) : ISqlCpuWitnessSession
    {
        private readonly Guid sessionId = Guid.NewGuid();
        private readonly Guid clientId = connection.ClientConnectionId;
        private int previousOrdinal;

        public async ValueTask<SqlCpuTargetWitness> ReadAsync(int ordinal, CancellationToken cancellationToken)
        {
            if (ordinal is not (1 or 2) || Interlocked.CompareExchange(ref previousOrdinal, ordinal, ordinal - 1) != ordinal - 1)
                throw new InvalidOperationException("SQL CPU witness reads must be ordered and bounded.");
            RequireConnection();
            await using var command = new SqlCommand(query, connection)
            { CommandTimeout = Math.Max(1, (int)Math.Ceiling(Math.Min(request.Timeout.Value.TotalSeconds, 5))) };
            await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);
            SqlCpuTargetWitness receipt = await SqlServerCpuTargetWitnessReader.ParseAsync(request,
                new SqlDataReaderOperationalHealthRowReader(reader), key, sessionId, clientId, ordinal, cancellationToken).ConfigureAwait(false);
            RequireConnection();
            return receipt;
        }

        private void RequireConnection()
        {
            if (connection.State != ConnectionState.Open || clientId == Guid.Empty || connection.ClientConnectionId != clientId)
                throw new InvalidDataException("The SQL CPU witness connection changed during native sampling.");
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
