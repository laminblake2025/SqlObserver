using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Common bounded execution path for M9 fixed-query collectors.</summary>
public abstract class SqlServerOperationalHealthCollector : ISqlServerCollector
{
    private readonly SqlServerOperationalHealthAssetCatalog assets;
    private readonly SqlServerIntegratedConnectionFactory connectionFactory;
    protected SqlServerOperationalHealthCollector(SqlServerOperationalHealthAssetCatalog assets)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        this.connectionFactory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
    }
    public abstract CollectorManifest Manifest { get; }
    protected abstract string QueryName { get; }
    protected abstract ValueTask<(IOperationalHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss)> ReadAsync(CollectorExecutionRequest request, IOperationalHealthRowReader reader, CancellationToken cancellationToken);
    internal ValueTask<(IOperationalHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss)> ReadRowsForTestAsync(CollectorExecutionRequest request, IOperationalHealthRowReader reader, CancellationToken cancellationToken) => ReadAsync(request, reader, cancellationToken);
    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CapabilityProfile.ServerIdentity is null || request.CapabilityProfile.TargetId != request.TargetId || request.CapabilityProfile.TargetRevision != request.TargetRevision)
            return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        if (!Manifest.SupportedVersions.Contains(request.CapabilityProfile.ServerIdentity.Version.Major) || !Manifest.SupportedEngineEditions.Contains(request.CapabilityProfile.ServerIdentity.EngineEdition))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
        if (QueryName is "sql-agent.failures" or "availability-groups.health")
        {
            string feature = QueryName == "sql-agent.failures" ? "feature.sql-agent-history" : "feature.availability-groups";
            if (request.CapabilityProfile.Capabilities.FirstOrDefault(x => x.CapabilityId.Value == feature)?.Availability != CapabilityAvailability.Available)
                return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        }
        if (QueryName == "tempdb.health")
        {
            string permission = request.CapabilityProfile.ServerIdentity.Version.Major == 15 ? "server.view-state" : "server.view-performance-state";
            if (request.CapabilityProfile.Permissions.FirstOrDefault(x => x.PermissionId.Value == permission)?.Outcome != PermissionEvidenceOutcome.Granted)
                return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        }
        if (QueryName == "backups.status")
        {
            string permission = request.CapabilityProfile.ServerIdentity.Version.Major == 15 ? "server.view-state" : "server.view-performance-state";
            if (request.CapabilityProfile.Permissions.FirstOrDefault(x => x.PermissionId.Value == permission)?.Outcome != PermissionEvidenceOutcome.Granted ||
                request.CapabilityProfile.Permissions.FirstOrDefault(x => x.PermissionId.Value == "msdb.backupset.select")?.Outcome != PermissionEvidenceOutcome.Granted)
                return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        }
        TimeSpan budget = request.Timeout.Value < Manifest.Limits.CommandTimeout ? request.Timeout.Value : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            await using SqlConnection connection = await connectionFactory.OpenConnectionAsync(request.ConnectionPolicy, deadline.Token).ConfigureAwait(false);
            await using var command = new SqlCommand(assets.Get($"{QueryName}.sqlserver{request.CapabilityProfile.ServerIdentity.Version.Major}-windows.v1.sql"), connection)
            {
                CommandTimeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds))
            };
            command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = QueryName == "availability-groups.health" ? Manifest.Limits.MaxRows + 1 : Manifest.Limits.MaxRows;
            command.Parameters.Add("scan_rows", SqlDbType.Int).Value = QueryName == "sql-agent.failures" ? 4096 : Manifest.Limits.MaxRows;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, deadline.Token).ConfigureAwait(false);
            var read = await ReadAsync(request, new SqlDataReaderOperationalHealthRowReader(reader), deadline.Token).ConfigureAwait(false);
            var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(read.Snapshot, read.Items, read.Bytes));
            var accounting = new CollectorRunAccounting(read.Rows, payload.ItemCount, Math.Max(read.Bytes, payload.EstimatedSizeBytes), payload.EstimatedSizeBytes);
            bool degraded = read.Snapshot is AvailabilityGroupsSnapshot { State: OperationalObservationState.Degraded };
            CollectorLossEvidence effectiveLoss = degraded && !read.Loss.HasLoss ? new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false) : read.Loss;
            return new CollectorExecutionResult(request.TargetId, request.TargetRevision, Manifest.Id, Manifest.ManifestVersion.Value, Manifest.OutputSchemaVersion.Value, effectiveLoss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded, read.Loss.HasLoss ? CollectorRunReason.SourceRowLimit : degraded ? CollectorRunReason.VisibilityIncomplete : CollectorRunReason.Completed, payload, accounting, effectiveLoss);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (exception.Number is 229 or 297) { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException) { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
    }
    protected static CollectorLossEvidence Loss(int rows, int maximum) => rows > maximum ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false) : CollectorLossEvidence.None;
    private CollectorExecutionResult Failure(CollectorExecutionRequest request, CollectorRunOutcome outcome, CollectorRunReason reason) =>
        new(request.TargetId, request.TargetRevision, Manifest.Id, Manifest.ManifestVersion.Value, Manifest.OutputSchemaVersion.Value, outcome, reason, CollectorPayload.Empty, new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);
}

/// <summary>Minimal row-reader seam used by the M9 parsers and deterministic production-path tests.</summary>
public interface IOperationalHealthRowReader
{
    int FieldCount { get; }
    bool IsDBNull(int ordinal);
    byte[] GetBinary(int ordinal);
    bool GetBoolean(int ordinal);
    DateTime GetDateTime(int ordinal);
    Guid GetGuid(int ordinal);
    short GetInt16(int ordinal);
    int GetInt32(int ordinal);
    long GetInt64(int ordinal);
    string GetString(int ordinal);
    ValueTask<bool> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class SqlDataReaderOperationalHealthRowReader(SqlDataReader reader) : IOperationalHealthRowReader
{
    public int FieldCount => reader.FieldCount;
    public bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
    public byte[] GetBinary(int ordinal) => reader.GetSqlBinary(ordinal).Value;
    public bool GetBoolean(int ordinal) => reader.GetBoolean(ordinal);
    public DateTime GetDateTime(int ordinal) => reader.GetDateTime(ordinal);
    public Guid GetGuid(int ordinal) => reader.GetGuid(ordinal);
    public short GetInt16(int ordinal) => reader.GetInt16(ordinal);
    public int GetInt32(int ordinal) => reader.GetInt32(ordinal);
    public long GetInt64(int ordinal) => reader.GetInt64(ordinal);
    public string GetString(int ordinal) => reader.GetString(ordinal);
    public ValueTask<bool> ReadAsync(CancellationToken cancellationToken) => new(reader.ReadAsync(cancellationToken));
}

public static class M9Manifest
{
    private static readonly CapabilityId[] Capabilities = [new("connection.tds"), new("authentication.windows-integrated"), new("transport.tls-validated"), new("privilege.non-sysadmin"), new("platform.windows")];
    public static CollectorManifest Create(string id, string display, CollectorOutputKind kind, IReadOnlyList<SqlServerEngineEdition> editions, int cadence, int minimum, int rows, int bytes, IReadOnlyList<string> permissions)
    {
        var supported = new SqlServerMajorVersionRange(15, 17);
        CapabilityId[] capabilities = id switch
        {
            "availability-groups.health" => [.. Capabilities, new CapabilityId("feature.availability-groups")],
            "sql-agent.failures" => [.. Capabilities, new CapabilityId("feature.sql-agent-history")],
            _ => Capabilities,
        };
        // Feature flags are capability evidence, never grants.  Keep them out of
        // the permission contract so a feature-disabled profile is not mistaken
        // for a missing SQL permission.
        var declaredPermissions = permissions.Where(static permission => !permission.StartsWith("feature.", StringComparison.Ordinal)).ToArray();
        IEnumerable<CollectorPermissionRequirement> permissionRequirements = declaredPermissions.Select(permission => new CollectorPermissionRequirement(
            new SqlServerPermissionId(permission),
            permission.StartsWith("msdb.", StringComparison.Ordinal) ? PermissionEvidenceScope.Database : PermissionEvidenceScope.Server,
            supported)).ToArray();
        if (id == "backups.status")
        {
            permissionRequirements =
            [
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
                new CollectorPermissionRequirement(new SqlServerPermissionId("msdb.backupset.select"), PermissionEvidenceScope.Database, supported),
            ];
        }
        else if (id == "sql-agent.failures")
        {
            permissionRequirements =
            [
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
                new CollectorPermissionRequirement(new SqlServerPermissionId("msdb.sysjobhistory.select"), PermissionEvidenceScope.Database, supported),
            ];
        }
        else if (id == "tempdb.health")
        {
            permissionRequirements =
            [
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
            ];
        }
        else if (id == "availability-groups.health")
        {
            permissionRequirements =
            [
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
                new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
            ];
        }
        CollectorPermissionRequirement[] perms = permissionRequirements.ToArray();
        return new CollectorManifest(new CollectorId(id), new CollectorDisplayName(display), new CollectorManifestVersion(1), capabilities, perms, supported,
            [SqlServerPlatform.Windows], new CollectorIntervalPolicy(TimeSpan.FromSeconds(cadence), TimeSpan.FromSeconds(minimum)),
            new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(id == "backups.status" ? 10 : 5), rows, bytes, CollectorEstimatedCost.Moderate),
            new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported), new CollectorOutputSchemaVersion(1), CollectorOperationalMode.Passive,
            editions, [new CollectorId("capability.connection"), new CollectorId("engine.core")],
            new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100), 3, TimeSpan.FromMinutes(5)), kind);
    }
    public static CollectorOutputContract OutputContract(int maximum) => new(new CollectorOutputSchemaVersion(1), [], 0, 0, 0, maxOperationalHealthObservations: maximum);
}
