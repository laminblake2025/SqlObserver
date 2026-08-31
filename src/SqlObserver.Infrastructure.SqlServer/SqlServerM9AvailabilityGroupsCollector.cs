using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

public sealed class SqlServerAvailabilityGroupsHealthCollector : SqlServerOperationalHealthCollector
{
    public SqlServerAvailabilityGroupsHealthCollector(SqlServerOperationalHealthAssetCatalog assets) : base(assets) { }
    public override CollectorManifest Manifest { get; } = M9Manifest.Create("availability-groups.health", "SQL Server availability groups health", CollectorOutputKind.AvailabilityGroupsHealth, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise], 30, 10, 2048, 2_097_152, ["server.view-state", "feature.availability-groups"]);
    protected override string QueryName => "availability-groups.health";
    protected override async ValueTask<(IOperationalHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss)> ReadAsync(CollectorExecutionRequest request, IOperationalHealthRowReader reader, CancellationToken cancellationToken)
    {
        var replicas = new List<AvailabilityReplicaObservation>(); var databases = new List<AvailabilityDatabaseObservation>(); AvailabilityVisibilityScope? localVisibility = null; int rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++; if (replicas.Count + databases.Count >= OperationalHealthBounds.AvailabilityMaximumRows) continue;
            int kind = reader.GetInt32(0);
            string group = Convert.ToHexString(reader.GetBinary(1)).ToLowerInvariant();
            if (kind == 0)
            {
                string replica = Convert.ToHexString(reader.GetBinary(2)).ToLowerInvariant();
                string role = SafeState(reader, 3), operational = SafeState(reader, 4), connected = SafeState(reader, 5);
                AvailabilityVisibilityScope scope = ReadVisibilityScope(reader, 10, role); localVisibility ??= scope;
                replicas.Add(new AvailabilityReplicaObservation(request.TargetId, request.TargetRevision, group, replica, role, operational, connected, scope, !reader.IsDBNull(6) && reader.GetBoolean(6)));
            }
            else if (!reader.IsDBNull(7))
            {
                string database = Convert.ToHexString(reader.GetBinary(7)).ToLowerInvariant();
                AvailabilityVisibilityScope scope = ReadVisibilityScope(reader, 10, SafeState(reader, 8)); localVisibility ??= scope;
                databases.Add(new AvailabilityDatabaseObservation(request.TargetId, request.TargetRevision, group, database, SafeState(reader, 8), SafeState(reader, 9), scope, !reader.IsDBNull(6) && reader.GetBoolean(6)));
            }
        }
        AvailabilityVisibilityScope visibility = localVisibility ?? (replicas.Any(static x => x.Role == "PRIMARY") ? AvailabilityVisibilityScope.PrimaryAllKnown : replicas.Any(static x => x.Role == "SECONDARY") ? AvailabilityVisibilityScope.SecondaryLocalOnly : AvailabilityVisibilityScope.ResolvingLocalOnly);
        bool stateAvailable = replicas.All(static x => x.StateAvailable) && databases.All(static x => x.StateAvailable);
        OperationalObservationState state = rows == 0 ? OperationalObservationState.NoData : stateAvailable ? OperationalObservationState.Complete : OperationalObservationState.Degraded;
        var snapshot = new AvailabilityGroupsSnapshot(request.TargetId, request.TargetRevision, request.RunId, DateTimeOffset.UtcNow,
            state, visibility, replicas, databases, rows > OperationalHealthBounds.AvailabilityMaximumRows);
        return (snapshot, rows, replicas.Count + databases.Count, (replicas.Count + databases.Count) * 160, Loss(rows, OperationalHealthBounds.AvailabilityMaximumRows));
    }
    private static string SafeState(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return "unknown";
        string value = reader.GetString(ordinal);
        return value is "PRIMARY" or "SECONDARY" or "RESOLVING" or "ONLINE" or "CONNECTED" or "DISCONNECTED" or "NOT_CONNECTED" or "FAILED" or "OFFLINE" or "SYNCHRONIZED" or "SYNCHRONIZING" or "NOT SYNCHRONIZING" or "RESTORING" or "RECOVERING" ? value : "unknown";
    }

    private static AvailabilityVisibilityScope ReadVisibilityScope(IOperationalHealthRowReader reader, int ordinal, string fallback)
    {
        if (reader.FieldCount > ordinal && !reader.IsDBNull(ordinal) && reader.GetInt16(ordinal) is >= 1 and <= 3)
            return (AvailabilityVisibilityScope)reader.GetInt16(ordinal);
        return fallback == "PRIMARY" ? AvailabilityVisibilityScope.PrimaryAllKnown : fallback == "SECONDARY" ? AvailabilityVisibilityScope.SecondaryLocalOnly : AvailabilityVisibilityScope.ResolvingLocalOnly;
    }
}
