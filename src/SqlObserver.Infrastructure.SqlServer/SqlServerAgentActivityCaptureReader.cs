using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>
/// Staged, passive evidence only. Direct msdb table visibility and version-specific service
/// DMV permissions require separate native qualification; this reader installs no grants.
/// Local activity dates are never converted to UTC or used as an elapsed-time clock.
/// </summary>
public sealed class SqlServerAgentActivityCaptureReader
{
    public static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(10);
    private readonly SqlServerAgentActivityCaptureAssetCatalog assets;
    private readonly ISqlAgentActivitySource source;
    private readonly TimeProvider timeProvider;

    public SqlServerAgentActivityCaptureReader(SqlServerAgentActivityCaptureAssetCatalog assets)
        : this(assets, new SqlAgentActivitySource()) { }

    internal SqlServerAgentActivityCaptureReader(SqlServerAgentActivityCaptureAssetCatalog assets, ISqlAgentActivitySource source, TimeProvider? timeProvider = null)
    { this.assets = assets ?? throw new ArgumentNullException(nameof(assets)); this.source = source ?? throw new ArgumentNullException(nameof(source)); this.timeProvider = timeProvider ?? TimeProvider.System; }

    public async ValueTask<SqlAgentActivityCapture> ReadAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        RequireProfile(request);
        TimeSpan budget = request.Timeout.Value < MaximumBudget ? request.Timeout.Value : MaximumBudget;
        long started = timeProvider.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            SqlAgentActivityCapture result;
            await using (ISqlAgentActivitySession session = await source.OpenAsync(request,
                assets.GetQuery(request.CapabilityProfile.ServerIdentity!.Version.Major), budget, deadline.Token).ConfigureAwait(false))
            {
                session.RequireConnection();
                result = await ParseAsync(request, session.Reader, deadline.Token).ConfigureAwait(false);
                session.RequireConnection();
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (timeProvider.GetElapsedTime(started) >= budget)
                throw new TimeoutException("The original Agent activity source deadline expired.");
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The bounded Agent activity source deadline expired.", exception); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number))
        { throw new UnauthorizedAccessException("Agent activity source permissions are unavailable.", exception); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { throw new TimeoutException("The bounded Agent activity source command expired.", exception); }
    }

    private static void RequireProfile(CollectorExecutionRequest request)
    {
        CapabilityProfile profile = request.CapabilityProfile;
        SqlServerIdentity? identity = profile.ServerIdentity;
        if (profile.TargetId != request.TargetId || profile.TargetRevision != request.TargetRevision)
            throw new InvalidDataException("Agent activity capability context does not match its target.");
        if (identity is null || identity.Version.Major is < 15 or > 17 || identity.Platform != SqlServerPlatform.Windows ||
            identity.EngineEdition is not (SqlServerEngineEdition.Standard or SqlServerEngineEdition.Enterprise))
            throw new PlatformNotSupportedException("Agent activity requires supported Windows SQL Server 2019 through 2025.");
        string statePermission = identity.Version.Major == 15 ? "server.view-state" : "server.view-security-state";
        if (!profile.TransportEncrypted || profile.IsSysAdmin ||
            profile.AuthenticationScheme is not (SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm) ||
            !Granted(statePermission, PermissionEvidenceScope.Server) ||
            !Granted("msdb.syssessions.select", PermissionEvidenceScope.Database) ||
            !Granted("msdb.sysjobactivity.select", PermissionEvidenceScope.Database))
            throw new UnauthorizedAccessException("Agent activity requires explicit integrated monitoring and full msdb activity visibility.");
        bool Granted(string id, PermissionEvidenceScope scope) => profile.Permissions.Any(item =>
            item.PermissionId.Value == id && item.Scope == scope && item.Outcome == PermissionEvidenceOutcome.Granted);
    }

    internal static async ValueTask<SqlAgentActivityCapture> ParseAsync(CollectorExecutionRequest request,
        ISqlAgentActivityResultReader reader, CancellationToken cancellationToken)
    {
        SqlAgentActivityHeader before = await HeaderAsync(reader, cancellationToken).ConfigureAwait(false);
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) throw InvalidShape();
        Shape(reader, JobNames, JobTypes);
        var jobs = new List<SqlAgentActivityJob>();
        int rows = 0;
        string? previous = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++rows > 513) throw InvalidShape();
            if (reader.IsDBNull(0)) throw InvalidShape();
            Guid id = reader.GetGuid(0);
            if (reader.IsDBNull(1)) throw InvalidShape();
            int session = reader.GetInt32(1);
            DateTime? requested = Local(reader, 2), queued = Local(reader, 3), start = Local(reader, 4), stop = Local(reader, 5);
            int? step = Integer(reader, 6);
            DateTime? stepLocal = Local(reader, 7);
            string orderedId = id.ToString("D");
            if (id == Guid.Empty || before.Session.SessionId != session ||
                previous is not null && string.CompareOrdinal(previous, orderedId) >= 0) throw InvalidShape();
            var job = new SqlAgentActivityJob(id, session, requested, queued, start, stop, step, stepLocal);
            previous = orderedId;
            if (jobs.Count < 512) jobs.Add(job);
        }
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) throw InvalidShape();
        SqlAgentActivityHeader after = await HeaderAsync(reader, cancellationToken).ConfigureAwait(false);
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) throw InvalidShape();
        cancellationToken.ThrowIfCancellationRequested();
        return new SqlAgentActivityCapture(request.TargetId, request.TargetRevision, request.RunId, before, jobs, after, rows, rows == 513);
    }

    private static async ValueTask<SqlAgentActivityHeader> HeaderAsync(ISqlAgentActivityResultReader reader, CancellationToken cancellationToken)
    {
        Shape(reader, HeaderNames, HeaderTypes);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw InvalidShape();
        if (reader.IsDBNull(0)) throw InvalidShape();
        DateTime observed = reader.GetDateTime(0);
        if (observed.Kind == DateTimeKind.Local) throw InvalidShape();
        if (reader.IsDBNull(1)) throw InvalidShape();
        int count = reader.GetInt32(1);
        int? status = Integer(reader, 2), pid = Integer(reader, 3);
        DateTimeOffset? startup = reader.IsDBNull(4) ? null : reader.GetDateTimeOffset(4);
        int? session = Integer(reader, 5);
        DateTime? local = Local(reader, 6);
        if (reader.IsDBNull(7) || reader.GetInt32(7) != 1 || await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw InvalidShape();
        return new SqlAgentActivityHeader(new DateTimeOffset(DateTime.SpecifyKind(observed, DateTimeKind.Utc)),
            new SqlAgentServiceEvidence(count, status, pid, startup), new SqlAgentSessionEvidence(session, local));
    }

    private static int? Integer(ISqlAgentActivityResultReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static DateTime? Local(ISqlAgentActivityResultReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        DateTime value = reader.GetDateTime(ordinal);
        if (value.Kind != DateTimeKind.Unspecified) throw InvalidShape();
        return value;
    }
    private static void Shape(ISqlAgentActivityResultReader reader, string[] names, Type[] types)
    {
        if (reader.FieldCount != names.Length) throw InvalidShape();
        for (int i = 0; i < names.Length; i++)
            if (reader.GetName(i) != names[i] || reader.GetFieldType(i) != types[i]) throw InvalidShape();
    }
    private static InvalidDataException InvalidShape() => new("Agent activity source shape or identity is invalid.");
    private static readonly string[] HeaderNames = ["source_observed_utc", "service_match_count", "service_status", "service_process_id", "service_startup_utc", "agent_session_id", "agent_start_local", "source_schema"];
    private static readonly Type[] HeaderTypes = [typeof(DateTime), typeof(int), typeof(int), typeof(int), typeof(DateTimeOffset), typeof(int), typeof(DateTime), typeof(int)];
    private static readonly string[] JobNames = ["job_id", "agent_session_id", "requested_local", "queued_local", "start_execution_local", "stop_execution_local", "last_executed_step_id", "last_executed_step_local"];
    private static readonly Type[] JobTypes = [typeof(Guid), typeof(int), typeof(DateTime), typeof(DateTime), typeof(DateTime), typeof(DateTime), typeof(int), typeof(DateTime)];
}

internal interface ISqlAgentActivitySource
{
    ValueTask<ISqlAgentActivitySession> OpenAsync(CollectorExecutionRequest request, string query, TimeSpan budget, CancellationToken cancellationToken);
}
internal interface ISqlAgentActivitySession : IAsyncDisposable
{
    ISqlAgentActivityResultReader Reader { get; }
    void RequireConnection();
}
internal interface ISqlAgentActivityResultReader
{
    int FieldCount { get; }
    string GetName(int ordinal);
    Type GetFieldType(int ordinal);
    bool IsDBNull(int ordinal);
    int GetInt32(int ordinal);
    Guid GetGuid(int ordinal);
    DateTime GetDateTime(int ordinal);
    DateTimeOffset GetDateTimeOffset(int ordinal);
    ValueTask<bool> ReadAsync(CancellationToken cancellationToken);
    ValueTask<bool> NextResultAsync(CancellationToken cancellationToken);
}

internal sealed class SqlAgentActivitySource : ISqlAgentActivitySource
{
    public async ValueTask<ISqlAgentActivitySession> OpenAsync(CollectorExecutionRequest request, string query, TimeSpan budget, CancellationToken cancellationToken)
    {
        var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, cancellationToken).ConfigureAwait(false);
        SqlCommand? command = null;
        try
        {
            Guid connectionId = connection.ClientConnectionId;
            command = new SqlCommand(query, connection) { CommandTimeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds)) };
            command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value = 513;
            SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            return new Session(connection, command, reader, connectionId);
        }
        catch
        {
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    private sealed class Session(SqlConnection connection, SqlCommand command, SqlDataReader reader, Guid connectionId)
        : ISqlAgentActivitySession, ISqlAgentActivityResultReader
    {
        public ISqlAgentActivityResultReader Reader => this;
        public void RequireConnection()
        {
            if (connectionId == Guid.Empty || connection.State != ConnectionState.Open || connection.ClientConnectionId != connectionId)
                throw new InvalidDataException("Agent activity connection changed during its source bracket.");
        }
        public int FieldCount => reader.FieldCount;
        public string GetName(int ordinal) => reader.GetName(ordinal);
        public Type GetFieldType(int ordinal) => reader.GetFieldType(ordinal);
        public bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
        public int GetInt32(int ordinal) => reader.GetInt32(ordinal);
        public Guid GetGuid(int ordinal) => reader.GetGuid(ordinal);
        public DateTime GetDateTime(int ordinal) => reader.GetDateTime(ordinal);
        public DateTimeOffset GetDateTimeOffset(int ordinal) => reader.GetDateTimeOffset(ordinal);
        public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken) => await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        public async ValueTask<bool> NextResultAsync(CancellationToken cancellationToken) => await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        public async ValueTask DisposeAsync()
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { await command.DisposeAsync().ConfigureAwait(false); }
                finally { await connection.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }
}
