using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

public sealed class SqlServerBackupsStatusCollector : SqlServerOperationalHealthCollector
{
    public SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog assets) : base(assets) { }
    public override CollectorManifest Manifest { get; } = M9Manifest.Create("backups.status", "SQL Server backup status", CollectorOutputKind.BackupsStatus, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express], 300, 60, 1537, 1_048_576, ["server.view-state", "msdb.backupset.select"]);
    protected override string QueryName => "backups.status";
    protected override async ValueTask<(IOperationalHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss)> ReadAsync(CollectorExecutionRequest request, IOperationalHealthRowReader reader, CancellationToken cancellationToken)
    {
        var items = new List<BackupStatusObservation>();
        int rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            if (items.Count >= OperationalHealthBounds.BackupMaximumRows) continue;
            string fingerprint = Convert.ToHexString(reader.GetBinary(0)).ToLowerInvariant();
            BackupKind kind = (BackupKind)reader.GetInt32(1);
            DateTime? local = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            long? size = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            bool? copyOnly = reader.IsDBNull(4) ? null : reader.GetBoolean(4), checksum = reader.IsDBNull(5) ? null : reader.GetBoolean(5), damaged = reader.IsDBNull(6) ? null : reader.GetBoolean(6);
            long? backupSetId = reader.IsDBNull(7) ? null : reader.GetInt64(7);
            short? offset = reader.IsDBNull(8) ? null : reader.GetInt16(8);
            DateTimeOffset? finishUtc = null;
            bool sourceTimeUnknown = false;
            if (local.HasValue)
            {
                var converted = SqlServerTimestamp.ToUtc(local.Value, offset);
                finishUtc = converted.Utc;
                sourceTimeUnknown = converted.SourceTimeUnknown;
            }
            BackupCoverage coverage = local.HasValue ? BackupCoverage.Complete
                : backupSetId.HasValue ? BackupCoverage.Unknown : BackupCoverage.NotSeenWithin35Days;
            items.Add(new BackupStatusObservation(request.TargetId, request.TargetRevision,
                fingerprint, kind, finishUtc, local, sourceTimeUnknown, size,
                copyOnly, checksum, damaged, coverage)
            { BackupSetId = backupSetId });
        }
        var snapshot = new BackupStatusSnapshot(request.TargetId, request.TargetRevision, request.RunId, DateTimeOffset.UtcNow,
            items.Count == 0 ? OperationalObservationState.NoData : OperationalObservationState.Complete, items, rows, rows > OperationalHealthBounds.BackupMaximumRows);
        return (snapshot, rows, items.Count, items.Count * 160, Loss(rows, OperationalHealthBounds.BackupMaximumRows));
    }
}
