using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

internal static class BackupV2PersistencePayload
{
    public static object Build(CommitCollectorRunRequest request, string outcome, string reason)
    {
        var snapshot = request.Payload.OperationalHealth?.Snapshot as BackupV2ReadSnapshot;
        CollectorRunSummary summary = request.Summary;
        return new
        {
            schemaVersion = 2, targetId = request.Work.TargetId.Value,
            targetRevision = request.Work.TargetRevision.Value, runId = summary.RunId.Value,
            outcome, reason,
            receivedAtUtc = Utc(snapshot?.ReceivedAtUtc ?? request.Work.RepositoryTimeUtc),
            sourceObservedAtUtc = Utc(snapshot?.SourceObservedAtUtc),
            sourceZoneId = snapshot?.SourceZoneId,
            historyLowerBoundLocal = Local(snapshot?.HistoryLowerBoundLocal),
            coverage = (int)(snapshot?.Coverage ?? (summary.Outcome switch
            {
                CollectorRunOutcome.PermissionDenied => BackupFreshnessCoverage.PermissionDenied,
                CollectorRunOutcome.Unsupported => BackupFreshnessCoverage.Unsupported,
                CollectorRunOutcome.CircuitOpen => BackupFreshnessCoverage.NotCollected,
                _ => BackupFreshnessCoverage.Failed,
            })),
            sourceRowsRead = summary.Accounting.SourceRowsRead,
            rejectedItems = summary.Outcome == CollectorRunOutcome.OutputInvalid ? summary.Accounting.OutputItemsProduced : 0,
            rejectedBytes = summary.Outcome == CollectorRunOutcome.OutputInvalid ? summary.Accounting.OutputBytes : 0,
            databases = snapshot?.Databases.Select((database, index) => new
            {
                ordinal = index + 1, database.DatabaseFingerprint, database.FamilyFingerprint,
                database.RecoveryForkFingerprint, database.GroupFingerprint,
                database.AgDatabaseFingerprint, database.LocalReplicaFingerprint,
                recoveryModel = (int)database.RecoveryModel, database.CatalogDatabaseState,
                scope = (int)database.Scope, database.LocalReplicaRole, database.LocalReplicaPreferred,
                database.UnclassifiedBackupSeen, coverage = (int)database.Coverage,
                samples = database.Samples.Select(sample => new
                {
                    kind = (int)sample.Freshness.Kind, sample.Freshness.CopyOnly,
                    finishUtc = Utc(sample.Freshness.FinishUtc),
                    sourceLocalStart = Local(sample.SourceLocalStart),
                    sourceLocalFinish = Local(sample.Freshness.SourceLocalFinish),
                    sample.RecordedStartOffsetMinutes, sample.SizeBytes,
                    sample.Freshness.HasChecksum, sample.Freshness.IsDamaged, sample.Freshness.MetadataComplete,
                    sample.FirstRecoveryForkFingerprint, sample.LastRecoveryForkFingerprint,
                    timeState = (int)sample.TimeResolution.State, sample.TimeResolution.QualificationRevision,
                }).ToArray(),
            }).ToArray() ?? [],
        };
    }

    private static string? Utc(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    private static string? Local(DateTime? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
