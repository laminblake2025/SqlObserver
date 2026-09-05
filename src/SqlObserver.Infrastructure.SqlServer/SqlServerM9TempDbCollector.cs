using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

public sealed class SqlServerTempDbHealthCollector : SqlServerOperationalHealthCollector
{
    public SqlServerTempDbHealthCollector(SqlServerOperationalHealthAssetCatalog assets) : base(assets) { }
    public override CollectorManifest Manifest { get; } = M9Manifest.Create("tempdb.health", "SQL Server TempDB health", CollectorOutputKind.TempDbHealth, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express], 30, 10, 128, 262_144, ["server.view-state"]);
    protected override string QueryName => "tempdb.health";
    protected override async ValueTask<(IOperationalHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss)> ReadAsync(CollectorExecutionRequest request, IOperationalHealthRowReader reader, CancellationToken cancellationToken)
    {
        var files = new List<TempDbFileObservation>(); int rows = 0; long total = 0, used = 0; long? logTotal = null, logUsed = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++; if (files.Count >= OperationalHealthBounds.TempDbMaximumFiles) continue;
            int fileId = reader.GetInt32(0);
            long size = reader.GetInt64(1), use = reader.IsDBNull(2) ? 0 : reader.GetInt64(2), free = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            long? rowLogTotal = reader.IsDBNull(4) ? null : reader.GetInt64(4), rowLogUsed = reader.IsDBNull(5) ? null : reader.GetInt64(5);
            if ((logTotal is not null && logTotal != rowLogTotal) || (logUsed is not null && logUsed != rowLogUsed)) continue;
            logTotal ??= rowLogTotal; logUsed ??= rowLogUsed;
            if (size < 0 || use < 0 || free < 0 || use > size || free > size || free != size - use || rowLogUsed.HasValue && rowLogUsed.Value > 0 && rowLogTotal is null || rowLogTotal.HasValue && rowLogUsed.HasValue && rowLogUsed.Value > rowLogTotal.Value) continue;
            checked { total += size; used += use; }
            files.Add(new TempDbFileObservation(request.TargetId, request.TargetRevision, fileId, size, use, free, TempDbComponentState.Healthy));
        }
        var snapshot = new TempDbSnapshot(request.TargetId, request.TargetRevision, request.RunId, DateTimeOffset.UtcNow,
            files.Count == 0 ? OperationalObservationState.NoData : OperationalObservationState.Complete, total, used, logTotal, logUsed, files,
            rows > OperationalHealthBounds.TempDbMaximumFiles);
        return (snapshot, rows, files.Count, files.Count * 96, Loss(rows, OperationalHealthBounds.TempDbMaximumFiles));
    }
}
