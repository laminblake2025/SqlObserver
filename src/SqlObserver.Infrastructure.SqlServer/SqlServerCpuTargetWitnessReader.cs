using System.Data;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Principal;
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
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);
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
        var result = await ReadCoreAsync(request, null, startNative, cancellationToken).ConfigureAwait(false);
        return result.Bracket;
    }

    /// <summary>Attests the current collector Windows token on the same owned SQL connection
    /// before and after native sampling. Callers cannot supply or replace the expected identity.</summary>
    public async ValueTask<SqlCpuWitnessBracket<T>> ReadCollectorAttestedBracketAsync<T>(CollectorExecutionRequest request,
        Func<SqlCpuTargetWitness, CancellationToken, ICpuNativeSamplingOperation<T>> startNative,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier sid = identity.User ?? throw new UnauthorizedAccessException("Collector Windows identity is unavailable.");
        byte[] expected = new byte[sid.BinaryLength]; sid.GetBinaryForm(expected, 0);
        try
        {
            var attested = await ReadAttestedBracketAsync(request, expected, startNative, cancellationToken).ConfigureAwait(false);
            return attested.Bracket;
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    internal async ValueTask<SqlCpuAttestedWitnessBracket<T>> ReadAttestedBracketAsync<T>(CollectorExecutionRequest request,
        ReadOnlyMemory<byte> expectedMonitorSid,
        Func<SqlCpuTargetWitness, CancellationToken, ICpuNativeSamplingOperation<T>> startNative,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(startNative);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] expected = SqlCpuMonitorIdentityValidation.CopyExpectedSid(expectedMonitorSid.Span);
        try
        {
            var result = await ReadCoreAsync(request, expected, startNative, cancellationToken).ConfigureAwait(false);
            return new(result.Bracket, result.BeforeIdentity!, result.AfterIdentity!);
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    private sealed record OwnedResult<T>(SqlCpuWitnessBracket<T> Bracket,
        SqlCpuMonitorIdentityEvidence? BeforeIdentity, SqlCpuMonitorIdentityEvidence? AfterIdentity);

    private async ValueTask<OwnedResult<T>> ReadCoreAsync<T>(CollectorExecutionRequest request,
        ReadOnlyMemory<byte>? expectedMonitorSid,
        Func<SqlCpuTargetWitness, CancellationToken, ICpuNativeSamplingOperation<T>> startNative,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(startNative);
        cancellationToken.ThrowIfCancellationRequested();
        if (!active.TryAdd(request.TargetId.Value, 0))
            throw new InvalidOperationException("A CPU witness operation already owns this target.");
        try { return await ReadOwnedAsync(request, expectedMonitorSid, startNative, cancellationToken).ConfigureAwait(false); }
        finally { active.TryRemove(request.TargetId.Value, out _); }
    }

    private async ValueTask<OwnedResult<T>> ReadOwnedAsync<T>(CollectorExecutionRequest request,
        ReadOnlyMemory<byte>? expectedMonitorSid,
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
        ISqlCpuMonitorIdentitySession? identitySession = null;
        SqlCpuMonitorIdentityEvidence? beforeIdentity = null, afterIdentity = null;
        if (expectedMonitorSid is { } expected)
        {
            identitySession = session as ISqlCpuMonitorIdentitySession ??
                throw new InvalidDataException("The owned SQL CPU session cannot attest monitor identity.");
            beforeIdentity = await identitySession.AttestAsync(expected, 1, deadline.Token).ConfigureAwait(false);
            RequireIdentityRequest(request, beforeIdentity, 1);
            SqlCpuMonitorIdentityValidation.RequireAllowed(beforeIdentity);
            deadline.Token.ThrowIfCancellationRequested();
        }
        SqlCpuTargetWitness before = await session.ReadAsync(1, deadline.Token).ConfigureAwait(false);
        RequireRequest(request, before, 1);
        if (beforeIdentity is not null)
        {
            SqlCpuMonitorIdentityValidation.RequireWitness(beforeIdentity, before);
            if (beforeIdentity.ObservedAtUtc > before.SourceObservedAtUtc)
                throw new InvalidDataException("SQL CPU monitor identity precedes its witness only with consistent source clocks.");
        }
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
        if (identitySession is not null)
        {
            afterIdentity = await identitySession.AttestAsync(expectedMonitorSid!.Value, 2, deadline.Token).ConfigureAwait(false);
            RequireIdentityRequest(request, afterIdentity, 2);
            SqlCpuMonitorIdentityValidation.RequirePair(beforeIdentity!, afterIdentity, before, after);
            deadline.Token.ThrowIfCancellationRequested();
        }
        return new(new(before, native!, after, SqlCpuWitnessContinuity.Evaluate(before, after)), beforeIdentity, afterIdentity);
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

    private static void RequireIdentityRequest(CollectorExecutionRequest request, SqlCpuMonitorIdentityEvidence receipt, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.TargetId != request.TargetId || receipt.TargetRevision != request.TargetRevision ||
            receipt.RunId != request.RunId || receipt.Ordinal != ordinal)
            throw new InvalidDataException("SQL CPU monitor identity does not belong to the exact collector execution.");
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

    internal static ValueTask<SqlCpuTargetWitness> ParseAsync(CollectorExecutionRequest request,
        IOperationalHealthRowReader reader, IdentityFingerprintKey key, Guid sessionId,
        Guid clientConnectionId, int ordinal, CancellationToken cancellationToken) =>
        ParseCoreAsync(request, reader, key, sessionId, clientConnectionId, ordinal, 1, 0, cancellationToken);

    internal static ValueTask<SqlCpuTargetWitness> ParseTcpAsync(CollectorExecutionRequest request,
        IOperationalHealthRowReader reader, IdentityFingerprintKey key, Guid sessionId,
        Guid clientConnectionId, int ordinal, int collectorProcessId, CancellationToken cancellationToken) =>
        ParseCoreAsync(request, reader, key, sessionId, clientConnectionId, ordinal, 2, collectorProcessId, cancellationToken);

    internal static async ValueTask<SqlCpuMonitorIdentityEvidence> ParseMonitorIdentityAsync(CollectorExecutionRequest request,
        IOperationalHealthRowReader reader, Guid sessionId, Guid clientConnectionId, int ordinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.FieldCount != 12 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("SQL CPU monitor identity requires exactly one row of its approved width.");
        SqlCpuMonitorIdentityEvidence receipt;
        try
        {
            if (reader.IsDBNull(0)) throw new InvalidDataException("SQL CPU monitor identity source time is missing.");
            DateTime observed = reader.GetDateTime(0);
            if (observed.Kind == DateTimeKind.Local) throw new InvalidDataException("SQL CPU monitor source UTC cannot carry a local kind.");
            bool Boolean(int field)
            {
                if (reader.IsDBNull(field)) throw new InvalidDataException("SQL CPU monitor identity comparison is missing.");
                return reader.GetBoolean(field);
            }
            bool currentMatched = Boolean(1), originalMatched = Boolean(2), nonSysadmin = Boolean(3),
                integrated = Boolean(4), tcp = Boolean(5), encrypted = Boolean(6),
                noControlServer = Boolean(7), noAlterAnyLogin = Boolean(8);
            if (reader.IsDBNull(9)) throw new InvalidDataException("SQL CPU monitor connection identity is missing.");
            Guid serverConnectionId = reader.GetGuid(9);
            if (reader.IsDBNull(10)) throw new InvalidDataException("SQL CPU monitor session identity is missing.");
            int serverSessionId = reader.GetInt32(10);
            if (reader.IsDBNull(11) || reader.GetInt32(11) != 1)
                throw new InvalidDataException("SQL CPU monitor identity schema is unsupported.");
            receipt = new(request.TargetId, request.TargetRevision, request.RunId, sessionId, clientConnectionId,
                ordinal, new DateTimeOffset(DateTime.SpecifyKind(observed, DateTimeKind.Utc)), serverConnectionId,
                serverSessionId, currentMatched, originalMatched, nonSysadmin, integrated, tcp, encrypted,
                noControlServer, noAlterAnyLogin);
        }
        catch (Exception failure) when (failure is InvalidCastException or ArgumentException or IndexOutOfRangeException)
        { throw new InvalidDataException("SQL CPU monitor identity row has an invalid source shape."); }
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("SQL CPU monitor identity exceeds its one-row bound.");
        cancellationToken.ThrowIfCancellationRequested();
        SqlCpuMonitorIdentityValidation.RequireAllowed(receipt);
        return receipt;
    }

    // Fixed, read-only, parameterized qualification query. This opt-in source
    // does not alter either existing pinned SQL CPU witness asset.
    internal static string LoadMonitorIdentityQuery() => """
        SET NOCOUNT ON;
        SELECT SYSUTCDATETIME(),
          CONVERT(bit, CASE WHEN SUSER_SID() = @expected_sid THEN 1 ELSE 0 END),
          CONVERT(bit, CASE WHEN SUSER_SID(ORIGINAL_LOGIN()) = @expected_sid THEN 1 ELSE 0 END),
          CONVERT(bit, CASE WHEN IS_SRVROLEMEMBER(N'sysadmin') = 0 THEN 1 ELSE 0 END),
          CONVERT(bit, CASE WHEN CONNECTIONPROPERTY(N'auth_scheme') IN (N'KERBEROS', N'NTLM') THEN 1 ELSE 0 END),
          CONVERT(bit, CASE WHEN c.net_transport = N'TCP' AND c.protocol_type = N'TSQL'
            AND c.parent_connection_id IS NULL AND CONNECTIONPROPERTY(N'physical_net_transport') = N'TCP' THEN 1 ELSE 0 END),
          CONVERT(bit, CASE WHEN c.encrypt_option = N'TRUE' THEN 1 ELSE 0 END),
          CONVERT(bit, CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, N'CONTROL SERVER') = 0 THEN 1 ELSE 0 END),
          CONVERT(bit, CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY LOGIN') = 0 THEN 1 ELSE 0 END),
          c.connection_id, CONVERT(int, c.session_id), CONVERT(int, 1)
        FROM sys.dm_exec_connections AS c WHERE c.session_id = @@SPID;
        """;

    private static async ValueTask<SqlCpuTargetWitness> ParseCoreAsync(CollectorExecutionRequest request,
        IOperationalHealthRowReader reader, IdentityFingerprintKey key, Guid sessionId,
        Guid clientConnectionId, int ordinal, int schema, int collectorProcessId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (schema == 2 && collectorProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(collectorProcessId));
        if (reader.FieldCount != (schema == 1 ? 12 : 18) || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("SQL CPU witness requires exactly one row of its approved width.");
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
        SqlCpuTcpConnectionEvidence? tcp = null;
        if (schema == 2)
        {
            string? serverAddress = Name(11); int? serverPort = Int(12);
            string? clientAddress = Name(13); int? clientPort = Int(14);
            Guid? connectionId = reader.IsDBNull(15) ? null : reader.GetGuid(15);
            int? serverSessionId = Int(16);
            bool anyTcpField = serverAddress is not null || serverPort is not null || clientAddress is not null ||
                clientPort is not null || connectionId is not null || serverSessionId is not null;
            if (anyTcpField)
            {
                if (transport != "TCP" || connectionId is null || serverSessionId is null)
                    throw new InvalidDataException("SQL CPU TCP ownership fields are incomplete or conflict with transport.");
                tcp = new(connectionId.Value, serverSessionId.Value, serverAddress, serverPort,
                    clientAddress, clientPort, collectorProcessId);
            }
        }
        int schemaField = schema == 1 ? 11 : 17;
        if (reader.IsDBNull(schemaField) || reader.GetInt32(schemaField) != schema)
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
            engine, kind, userTime, kernelTime, processors, tcpConnection: tcp, sourceSchemaVersion: schema);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("SQL CPU witness exceeds its one-row bound.");
        cancellationToken.ThrowIfCancellationRequested();
        return receipt;
    }

    internal static string LoadQuery(Assembly? assembly = null)
    {
        const string resource = "SqlObserver.Infrastructure.SqlServer.CpuWitnessSourceAssets.cpu.target-witness.sqlserver15-17-windows.v1.sql";
        const string checksum = "86c64863d16f99cf7fd2ac969356c11f307969b3a07a348bcbe8218b3acfce99";
        return LoadPinnedQuery(resource, checksum, assembly);
    }

    internal static string LoadTcpQuery(Assembly? assembly = null) => LoadPinnedQuery(
        "SqlObserver.Infrastructure.SqlServer.CpuWitnessSourceAssets.cpu.target-witness.sqlserver15-17-windows.v2.sql",
        "04ae4d76f6b750ed3112483a483636fbe29e2a42cd788478bbe7454b6a68b87c", assembly);

    private static string LoadPinnedQuery(string resource, string checksum, Assembly? assembly)
    {
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

internal interface ISqlCpuMonitorIdentitySession : ISqlCpuWitnessSession
{
    ValueTask<SqlCpuMonitorIdentityEvidence> AttestAsync(ReadOnlyMemory<byte> expectedMonitorSid, int ordinal,
        CancellationToken cancellationToken);
}

internal sealed class SqlCpuWitnessSource : ISqlCpuWitnessSource
{
    public async ValueTask<ISqlCpuWitnessSession> OpenAsync(CollectorExecutionRequest request, IdentityFingerprintKey key, CancellationToken cancellationToken)
    {
        string query = SqlServerCpuTargetWitnessReader.LoadTcpQuery();
        var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, cancellationToken).ConfigureAwait(false);
        return new Session(connection, request, key, query);
    }

    private sealed class Session(SqlConnection connection, CollectorExecutionRequest request,
        IdentityFingerprintKey key, string query) : ISqlCpuMonitorIdentitySession
    {
        private readonly Guid sessionId = Guid.NewGuid();
        private readonly Guid clientId = connection.ClientConnectionId;
        private readonly int collectorProcessId = Environment.ProcessId;
        private int previousOrdinal;
        private int previousIdentityOrdinal;

        public async ValueTask<SqlCpuMonitorIdentityEvidence> AttestAsync(ReadOnlyMemory<byte> expectedMonitorSid,
            int ordinal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordinal is not (1 or 2) || previousOrdinal != (ordinal == 1 ? 0 : 2) ||
                Interlocked.CompareExchange(ref previousIdentityOrdinal, ordinal, ordinal - 1) != ordinal - 1)
                throw new InvalidOperationException("SQL CPU monitor attestations must surround the ordered witness reads.");
            byte[] expected = SqlCpuMonitorIdentityValidation.CopyExpectedSid(expectedMonitorSid.Span);
            try
            {
                RequireConnection();
                await using var command = new SqlCommand(SqlServerCpuTargetWitnessReader.LoadMonitorIdentityQuery(), connection)
                { CommandTimeout = Math.Max(1, (int)Math.Ceiling(Math.Min(request.Timeout.Value.TotalSeconds, 5))) };
                command.Parameters.Add("@expected_sid", SqlDbType.VarBinary, expected.Length).Value = expected;
                await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                    cancellationToken).ConfigureAwait(false);
                SqlCpuMonitorIdentityEvidence receipt = await SqlServerCpuTargetWitnessReader.ParseMonitorIdentityAsync(request,
                    new SqlDataReaderOperationalHealthRowReader(reader), sessionId, clientId, ordinal,
                    cancellationToken).ConfigureAwait(false);
                RequireConnection();
                return receipt;
            }
            finally { CryptographicOperations.ZeroMemory(expected); }
        }

        public async ValueTask<SqlCpuTargetWitness> ReadAsync(int ordinal, CancellationToken cancellationToken)
        {
            if (ordinal is not (1 or 2) || Interlocked.CompareExchange(ref previousOrdinal, ordinal, ordinal - 1) != ordinal - 1)
                throw new InvalidOperationException("SQL CPU witness reads must be ordered and bounded.");
            RequireConnection();
            await using var command = new SqlCommand(query, connection)
            { CommandTimeout = Math.Max(1, (int)Math.Ceiling(Math.Min(request.Timeout.Value.TotalSeconds, 5))) };
            await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);
            SqlCpuTargetWitness receipt = await SqlServerCpuTargetWitnessReader.ParseTcpAsync(request,
                new SqlDataReaderOperationalHealthRowReader(reader), key, sessionId, clientId, ordinal,
                collectorProcessId, cancellationToken).ConfigureAwait(false);
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
