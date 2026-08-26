using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

public sealed class SqlServerSqlAgentFailuresCollector : SqlServerOperationalHealthCollector
{
    public SqlServerSqlAgentFailuresCollector(SqlServerOperationalHealthAssetCatalog assets) : base(assets) { }
    public override CollectorManifest Manifest { get; } = M9Manifest.Create("sql-agent.failures", "SQL Server Agent failures", CollectorOutputKind.SqlAgentFailures, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise], 60, 30, 512, 524_288, ["server.view-state", "msdb.sysjobhistory.select"]);
    protected override string QueryName => "sql-agent.failures";
    protected override async ValueTask<(IOperationalHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss)> ReadAsync(CollectorExecutionRequest request, IOperationalHealthRowReader reader, CancellationToken cancellationToken)
    {
        var items = new List<SqlAgentFailureObservation>();
        int rows = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            if (items.Count >= OperationalHealthBounds.AgentMaximumRows) continue;
            int duration = reader.GetInt32(7), dh = duration / 10000, dm = duration / 100 % 100, ds = duration % 100;
            if (dm > 59 || ds > 59) continue;
            int status = reader.GetInt32(3);
            AgentFailureKind kind = status == 2 ? AgentFailureKind.Retry : status == 3 ? AgentFailureKind.Cancelled : AgentFailureKind.Failed;
            Guid jobId = reader.GetGuid(0); long historyId = reader.GetInt64(1); int stepId = reader.GetInt32(2);
            items.Add(new SqlAgentFailureObservation(request.TargetId, request.TargetRevision, jobId, historyId, stepId, status, kind,
                reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6), checked(dh * 3600 + dm * 60 + ds), now,
                SqlAgentFailureIdentity.Compute(1, request.TargetId, request.TargetRevision, jobId, historyId, stepId, status)));
        }
        var snapshot = new SqlAgentFailureSnapshot(request.TargetId, request.TargetRevision, request.RunId, now,
            items.Count == 0 ? OperationalObservationState.NoData : OperationalObservationState.Complete, items, rows,
            rows > OperationalHealthBounds.AgentMaximumRows, null, null);
        return (snapshot, rows, items.Count, items.Count * 192, Loss(rows, OperationalHealthBounds.AgentMaximumRows));
    }
}
