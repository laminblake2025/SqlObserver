using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>One fenced repository read, bound to the active collector run.</summary>
public sealed class DeadlockReadExecutionContext
{
    public DeadlockReadExecutionContext(MonitoredInstanceId targetId, ObservationTargetRevision revision, long scheduleRevision,
        CollectorRunId runId, DeadlockReadDigest? expectedCheckpointDigest, XeReadIdentity? resumeSource, XeReadBookmark? bookmark)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(runId);
        if (scheduleRevision <= 0 || resumeSource is not null && (resumeSource.TargetId != targetId || resumeSource.TargetRevision != revision)
            || bookmark is not null && bookmark.Identity != resumeSource || resumeSource is not null && expectedCheckpointDigest is null)
            throw new ArgumentException("Deadlock read execution context has inconsistent bindings.");
        TargetId = targetId; TargetRevision = revision; ScheduleRevision = scheduleRevision; RunId = runId;
        ExpectedCheckpointDigest = expectedCheckpointDigest; ResumeSource = resumeSource; Bookmark = bookmark;
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public long ScheduleRevision { get; }
    public CollectorRunId RunId { get; }
    public DeadlockReadDigest? ExpectedCheckpointDigest { get; }
    public XeReadIdentity? ResumeSource { get; }
    public XeReadBookmark? Bookmark { get; }
    public DeadlockReadSourceProof UnavailableProof() => new(TargetId, TargetRevision, ScheduleRevision, RunId,
        ExpectedCheckpointDigest, DeadlockReadSourceState.Failed, null, null, null, [], false, true);
    public void Validate(DeadlockReadSourceProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        // The only admissible epoch transition is an empty, incomplete loss
        // probe. Atomic publication must independently recognize the reset.
        if (proof.TargetId != TargetId || proof.TargetRevision != TargetRevision || proof.ScheduleRevision != ScheduleRevision
            || proof.RunId != RunId || proof.ExpectedCheckpointDigest != ExpectedCheckpointDigest
            || proof.State == DeadlockReadSourceState.Ready && ResumeSource is not null && proof.Source != ResumeSource
                && !(proof.SourceLoss && proof.Positions.Count == 0 && !proof.ReachedEnd && proof.RetainedFromUtc == proof.ReadAtUtc))
            throw new ArgumentException("Deadlock source proof belongs to a different execution context.", nameof(proof));
    }
    public override string ToString() => "Deadlock read execution context";
}
