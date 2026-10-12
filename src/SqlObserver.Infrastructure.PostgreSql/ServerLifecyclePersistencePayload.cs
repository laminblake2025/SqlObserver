using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Closed source mirrors for the staged atomic lifecycle writer.</summary>
internal static class ServerLifecyclePersistencePayload
{
    public static object Build(CommitCollectorRunRequest request, string outcome, string reason)
    {
        ServerLifecycleSnapshot? snapshot = request.Payload.OperationalHealth?.Snapshot as ServerLifecycleSnapshot;
        return new
        {
            schemaVersion = ServerLifecycleSnapshot.CurrentSourceSchemaVersion,
            identityVersion = ServerLifecycleSnapshot.IdentityVersion,
            targetId = request.Work.TargetId.Value,
            targetRevision = request.Work.TargetRevision.Value,
            runId = request.Summary.RunId.Value,
            outcome,
            reason,
            sourceObservedAtUtc = snapshot?.SourceObservedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            sourceRowsRead = request.Summary.Accounting.SourceRowsRead,
            truncated = snapshot?.Truncated ?? false,
            engineCoverage = (int)(snapshot?.EngineCoverage ?? ServerLifecycleCoverage.Unknown),
            availabilityCoverage = (int)(snapshot?.AvailabilityCoverage ?? ServerLifecycleCoverage.Unknown),
            engine = snapshot is null ? null : new
            {
                snapshot.Engine.PhysicalNodeFingerprint,
                snapshot.Engine.LogicalInstanceFingerprint,
                snapshot.Engine.ProcessId,
                sourceStartLocal = snapshot.Engine.SourceStartLocal?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                snapshot.Engine.StartMsTicks,
                snapshot.Engine.CurrentMsTicks,
                snapshot.Engine.StartupIncarnationFingerprint,
                coverage = (int)snapshot.Engine.Coverage,
                hardwareIdentityVerified = false,
                occurredAtUtc = (string?)null,
            },
            isHadrEnabled = snapshot?.IsHadrEnabled,
            members = snapshot?.Members.Select(static item => new
            {
                item.GroupFingerprint,
                item.ReplicaFingerprint,
                item.ObserverReplicaFingerprint,
                item.Role,
                item.ObserverRole,
                item.StateObserved,
                item.ExpectedGroupMembers,
            }).ToArray() ?? [],
            // Schema 1 is a single-pass DMV source. This is never caller authority.
            sourceConsistencyVerified = false,
        };
    }
}
