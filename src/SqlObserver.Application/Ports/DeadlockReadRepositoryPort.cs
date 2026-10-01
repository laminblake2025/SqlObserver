using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed class DeadlockReadCheckpointResult
{
    public DeadlockReadCheckpointResult(BeginCollectorRunRequest request, DateTimeOffset readAtUtc, DeadlockReadDigest sourceDigest,
        DeadlockReadCheckpoint? checkpoint, bool resumeAllowed)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(sourceDigest);
        bool expectedResume = checkpoint is not null && checkpoint.TargetRevision == request.Work.TargetRevision
            && checkpoint.ScheduleRevision == request.Work.ScheduleRevision.Value && checkpoint.Source is not null;
        if (readAtUtc.Offset != TimeSpan.Zero || checkpoint is not null && checkpoint.TargetId != request.Work.TargetId || resumeAllowed != expectedResume)
            throw new ArgumentException("Deadlock checkpoint read bindings are invalid.");
        Request = request; ReadAtUtc = readAtUtc; SourceDigest = sourceDigest; Checkpoint = checkpoint; ResumeAllowed = resumeAllowed;
    }
    public BeginCollectorRunRequest Request { get; }
    public DateTimeOffset ReadAtUtc { get; }
    public DeadlockReadDigest SourceDigest { get; }
    public DeadlockReadCheckpoint? Checkpoint { get; }
    public bool ResumeAllowed { get; }
    public XeReadBookmark? ResumeBookmark => ResumeAllowed && Checkpoint!.Bookmark is not null
        ? new(Checkpoint.Source!, Checkpoint.Bookmark) : null;
    public XeReadIdentity? ResumeSource => ResumeAllowed ? Checkpoint!.Source : null;
    public override string ToString() => "Deadlock checkpoint read";
}

public interface IDeadlockReadRepositoryPort
{
    ValueTask<DeadlockReadCheckpointResult> ReadDeadlockCheckpointAsync(BeginCollectorRunRequest request, CancellationToken cancellationToken);
    ValueTask<CollectorRunCommitResult> CommitDeadlockReadAsync(CommitCollectorRunRequest request, DeadlockReadSourceProof proof, CancellationToken cancellationToken);
}
