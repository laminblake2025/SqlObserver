using System.Collections.ObjectModel;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public sealed record DeadlockReadDigest
{
    public DeadlockReadDigest(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A canonical SHA-256 digest is required.", nameof(value));
        Value = value;
    }
    public string Value { get; }
    public override string ToString() => "Deadlock read digest";
}

public enum DeadlockReadSourceState { Ready = 1, Missing = 2, ConfigurationInvalid = 3, ResetRequired = 4, Failed = 5 }

public static class DeadlockReadTime
{
    public static DateTimeOffset ToRepositoryPrecision(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Deadlock read timestamps must be UTC.", nameof(value));
        return new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>Private canonical state, containing numeric tokens and safe identities only.</summary>
public sealed class DeadlockReadCheckpoint
{
    public DeadlockReadCheckpoint(MonitoredInstanceId targetId, ObservationTargetRevision revision, long scheduleRevision,
        CollectorRunId runId, DateTimeOffset completedAtUtc, DeadlockReadDigest completionDigest, DeadlockReadSourceState state,
        XeReadIdentity? source, XeFilePosition? bookmark, DeadlockReadWindow? window, DeadlockReadDigest digest)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(revision); ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(completionDigest); ArgumentNullException.ThrowIfNull(digest);
        if (scheduleRevision <= 0 || !Enum.IsDefined(state) || completedAtUtc.Offset != TimeSpan.Zero
            || (source is null) != (window is null) || source is null && (bookmark is not null || state == DeadlockReadSourceState.Ready)
            || source is not null && (source.TargetId != targetId || source.TargetRevision != revision || window!.Source != source || window.ReadAtUtc != completedAtUtc)
            || bookmark is not null && bookmark.BufferOffset > 101L * 1_048_576
            || state == DeadlockReadSourceState.ResetRequired && bookmark is not null
            || state != DeadlockReadSourceState.Ready && window is not null && window.Reason != DeadlockReadWindowReason.SourceIncomplete)
            throw new ArgumentException("Deadlock checkpoint bindings are inconsistent.");
        TargetId = targetId; TargetRevision = revision; ScheduleRevision = scheduleRevision; RunId = runId;
        CompletedAtUtc = completedAtUtc; CompletionDigest = completionDigest; State = state; Source = source;
        Bookmark = bookmark; Window = window; Digest = digest;
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public long ScheduleRevision { get; }
    public CollectorRunId RunId { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public DeadlockReadDigest CompletionDigest { get; }
    public DeadlockReadSourceState State { get; }
    public XeReadIdentity? Source { get; }
    public XeFilePosition? Bookmark { get; }
    public DeadlockReadWindow? Window { get; }
    public DeadlockReadDigest Digest { get; }
    public override string ToString() => "Deadlock read checkpoint";
}

/// <summary>Bounded transport witnesses for atomic publication, never a caller-supplied count or window.</summary>
public sealed class DeadlockReadSourceProof
{
    public DeadlockReadSourceProof(MonitoredInstanceId targetId, ObservationTargetRevision revision, long scheduleRevision,
        CollectorRunId runId, DeadlockReadDigest? expectedCheckpointDigest, DeadlockReadSourceState state, XeReadIdentity? source,
        DateTimeOffset? readAtUtc, DateTimeOffset? retainedFromUtc, IReadOnlyList<XeFilePosition> positions, bool reachedEnd, bool sourceLoss)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(revision); ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(positions);
        if (scheduleRevision <= 0 || !Enum.IsDefined(state) || positions.Count > XeFileReadProgressCalculator.MaximumRows
            || positions.Any(p => p is null || p.BufferOffset > 101L * 1_048_576)
            || source is not null && (source.TargetId != targetId || source.TargetRevision != revision))
            throw new ArgumentException("Deadlock source proof bindings or bounds are invalid.");
        readAtUtc = readAtUtc is null ? null : DeadlockReadTime.ToRepositoryPrecision(readAtUtc.Value);
        retainedFromUtc = retainedFromUtc is null ? null : DeadlockReadTime.ToRepositoryPrecision(retainedFromUtc.Value);
        if (readAtUtc < DateTimeOffset.MinValue.AddMinutes(5)
            || retainedFromUtc is not null && (readAtUtc is null || retainedFromUtc > readAtUtc)
            || state == DeadlockReadSourceState.Ready && (source is null || readAtUtc is null || reachedEnd && positions.Count == XeFileReadProgressCalculator.MaximumRows)
            || state != DeadlockReadSourceState.Ready && (source is not null || retainedFromUtc is not null || positions.Count != 0 || reachedEnd || !sourceLoss))
            throw new ArgumentException("Deadlock source proof contradicts its source state.");
        TargetId = targetId; TargetRevision = revision; ScheduleRevision = scheduleRevision; RunId = runId;
        ExpectedCheckpointDigest = expectedCheckpointDigest; State = state; Source = source;
        ReadAtUtc = readAtUtc; RetainedFromUtc = retainedFromUtc; Positions = new ReadOnlyCollection<XeFilePosition>(positions.ToArray());
        ReachedEnd = reachedEnd; SourceLoss = sourceLoss;
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public long ScheduleRevision { get; }
    public CollectorRunId RunId { get; }
    public DeadlockReadDigest? ExpectedCheckpointDigest { get; }
    public DeadlockReadSourceState State { get; }
    public XeReadIdentity? Source { get; }
    public DateTimeOffset? ReadAtUtc { get; }
    public DateTimeOffset? RetainedFromUtc { get; }
    public IReadOnlyList<XeFilePosition> Positions { get; }
    public bool ReachedEnd { get; }
    public bool SourceLoss { get; }
    public override string ToString() => "Deadlock source proof";
}
