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
            Guid jobId = reader.GetGuid(0); long historyId = reader.GetInt64(1); int stepId = reader.GetInt32(2);
            int status = reader.GetInt32(3);
            int? messageId = reader.IsDBNull(4) ? null : reader.GetInt32(4), severity = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            int retries = reader.GetInt32(6);
            int duration = reader.GetInt32(7), dh = duration / 10000, dm = duration / 100 % 100, ds = duration % 100;
            if (dm > 59 || ds > 59) continue;
            int? sourceDate = reader.IsDBNull(8) ? null : reader.GetInt32(8);
            int? sourceTime = reader.IsDBNull(9) ? null : reader.GetInt32(9);
            AgentFailureKind kind = status == 2 ? AgentFailureKind.Retry : status == 3 ? AgentFailureKind.Cancelled : AgentFailureKind.Failed;
            items.Add(new SqlAgentFailureObservation(request.TargetId, request.TargetRevision, jobId, historyId, stepId, status, kind,
                messageId, severity, retries, checked(dh * 3600 + dm * 60 + ds), now,
                SqlAgentFailureIdentity.Compute(1, request.TargetId, request.TargetRevision, jobId, historyId, stepId, status))
            { SourceLocalStart = ReadSourceLocalStart(sourceDate, sourceTime) });
        }
        var snapshot = new SqlAgentFailureSnapshot(request.TargetId, request.TargetRevision, request.RunId, now,
            items.Count == 0 ? OperationalObservationState.NoData : OperationalObservationState.Complete, items, rows,
            rows > OperationalHealthBounds.AgentMaximumRows, null, null);
        return (snapshot, rows, items.Count, items.Count * 192 + items.Count(static item => item.SourceLocalStart.HasValue) * 8, Loss(rows, OperationalHealthBounds.AgentMaximumRows));
    }

    private static DateTime? ReadSourceLocalStart(int? sourceDate, int? sourceTime)
    {
        if (sourceDate is null or <= 0 || sourceTime is null or < 0 or > 235959) return null;
        int date = sourceDate.Value, time = sourceTime.Value;
        try
        {
            return new DateTime(date / 10000, date / 100 % 100, date % 100,
                time / 10000, time / 100 % 100, time % 100, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
