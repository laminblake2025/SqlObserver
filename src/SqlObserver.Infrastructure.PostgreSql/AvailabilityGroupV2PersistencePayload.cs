using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

internal static class AvailabilityGroupV2PersistencePayload
{
    public static object Build(CommitCollectorRunRequest request, string outcome, string reason)
    {
        AvailabilityGroupsV2Snapshot? snapshot = request.Payload.OperationalHealth?.Snapshot as AvailabilityGroupsV2Snapshot;
        CollectorRunSummary summary = request.Summary;
        return new
        {
            schemaVersion = 2,
            targetId = request.Work.TargetId.Value,
            targetRevision = request.Work.TargetRevision.Value,
            runId = summary.RunId.Value,
            outcome,
            reason,
            observedAtUtc = (snapshot?.ObservedAtUtc ?? request.Work.ScheduledAtUtc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            state = (int)(snapshot?.State ?? (summary.Outcome switch
            {
                CollectorRunOutcome.Unsupported => OperationalObservationState.Unsupported,
                CollectorRunOutcome.PermissionDenied => OperationalObservationState.PermissionDenied,
                _ => OperationalObservationState.Degraded,
            })),
            sourceRowsRead = summary.Accounting.SourceRowsRead,
            truncated = snapshot?.Truncated ?? false,
            replicas = snapshot?.Replicas.Select(static item => new
            {
                item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint,
                item.Identity.ObserverReplicaFingerprint, item.Identity.ReplicaIsLocal,
                visibilityScope = (int)item.Identity.VisibilityScope,
                item.StateObserved, item.Role, item.OperationalState, item.ConnectedState,
            }).ToArray() ?? [],
            databases = snapshot?.Databases.Select(static item => new
            {
                item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint,
                item.Identity.ObserverReplicaFingerprint, item.Identity.ReplicaIsLocal,
                visibilityScope = (int)item.Identity.VisibilityScope,
                item.StateObserved, item.DatabaseFingerprint, item.SynchronizationState,
                item.SynchronizationHealth, item.DatabaseState, item.IsSuspended,
                item.LogSendQueueKb, item.LogSendRateKbPerSecond, item.RedoQueueKb,
                item.RedoRateKbPerSecond,
                sourceLastCommitLocal = item.SourceLastCommitLocal?.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            }).ToArray() ?? [],
        };
    }
}
