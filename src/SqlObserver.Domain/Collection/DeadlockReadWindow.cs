using System.Collections.ObjectModel;

namespace SqlObserver.Domain.Collection;

/// <summary>Safe event identity and occurrence retained across incremental reads.</summary>
public sealed record DeadlockWindowEvent
{
    public DeadlockWindowEvent(Guid eventId, DateTimeOffset occurredAtUtc, bool parseTruncated = false)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("A deadlock event identity is required.", nameof(eventId));
        if (occurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Deadlock occurrence must be UTC.", nameof(occurredAtUtc));
        EventId = eventId;
        OccurredAtUtc = occurredAtUtc;
        ParseTruncated = parseTruncated;
    }

    public Guid EventId { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public bool ParseTruncated { get; }
}

public enum DeadlockReadWindowReason { Current = 1, CatchingUp = 2, SourceIncomplete = 3, CoverageGap = 4, WindowOverflow = 5 }

/// <summary>A bounded rolling count, distinct from the newly ingested event batch.</summary>
public sealed class DeadlockReadWindow
{
    internal DeadlockReadWindow(XeReadIdentity source, DateTimeOffset readAtUtc, DateTimeOffset coveredFromUtc,
        DateTimeOffset? gapThroughUtc, IReadOnlyList<DeadlockWindowEvent> events, DeadlockReadWindowReason reason)
    {
        Source = source;
        ReadAtUtc = readAtUtc;
        CoveredFromUtc = coveredFromUtc;
        GapThroughUtc = gapThroughUtc;
        Events = new ReadOnlyCollection<DeadlockWindowEvent>(events.ToArray());
        Reason = reason;
    }

    public XeReadIdentity Source { get; }
    public DateTimeOffset ReadAtUtc { get; }
    public DateTimeOffset CoveredFromUtc { get; }
    public DateTimeOffset? GapThroughUtc { get; }
    public IReadOnlyList<DeadlockWindowEvent> Events { get; }
    public DeadlockReadWindowReason Reason { get; }
    public int? DeadlockCount => Reason == DeadlockReadWindowReason.Current ? Events.Count : null;
}

/// <summary>
/// Preserves recent events through quiet reads and makes lost coverage unknown.
/// A transport bookmark cannot replace retained five-minute event history.
/// The future fenced writer must reproduce this merge and commit it atomically
/// with the canonical source outcome and its numeric resume position.
/// </summary>
public static class DeadlockReadWindowCalculator
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public const int MaximumEvents = 256;

    public static DeadlockReadWindow Merge(DeadlockReadWindow? previous, XeReadIdentity source,
        DateTimeOffset readAtUtc, DateTimeOffset? retainedFromUtc, IReadOnlyList<DeadlockWindowEvent> newlyRead,
        bool reachedEnd, bool sourceLoss = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(newlyRead);
        if (readAtUtc.Offset != TimeSpan.Zero || readAtUtc < DateTimeOffset.MinValue.Add(Window))
            throw new ArgumentException("Deadlock read time must be UTC with a representable window.", nameof(readAtUtc));
        if (newlyRead.Count > MaximumEvents) throw new ArgumentOutOfRangeException(nameof(newlyRead));
        if (previous is not null && previous.Source.TargetId != source.TargetId)
            throw new ArgumentException("Deadlock window belongs to a different target.", nameof(previous));
        if (previous is not null && readAtUtc <= previous.ReadAtUtc)
            throw new ArgumentException("Deadlock read time must advance.", nameof(readAtUtc));
        if (previous?.Source != source) previous = null;
        if (previous is null && (retainedFromUtc is null || retainedFromUtc.Value.Offset != TimeSpan.Zero || retainedFromUtc > readAtUtc))
            throw new ArgumentException("A cold read needs its proven UTC retention boundary.", nameof(retainedFromUtc));

        DateTimeOffset coveredFrom = previous?.CoveredFromUtc ?? retainedFromUtc!.Value;
        DateTimeOffset windowFrom = readAtUtc - Window;
        DateTimeOffset? gapThrough = previous?.GapThroughUtc;
        var events = new Dictionary<Guid, DeadlockWindowEvent>();
        foreach (DeadlockWindowEvent item in previous?.Events ?? [])
            if (item.OccurredAtUtc >= windowFrom) events.Add(item.EventId, item);
        foreach (DeadlockWindowEvent item in newlyRead)
        {
            if (item is null) throw new ArgumentException("Deadlock window cannot contain null events.", nameof(newlyRead));
            if (item.OccurredAtUtc > readAtUtc)
                throw new ArgumentException("Future deadlock evidence cannot enter the current window.", nameof(newlyRead));
            if (item.ParseTruncated) sourceLoss = true;
            if (events.TryGetValue(item.EventId, out DeadlockWindowEvent? existing) && existing != item)
                throw new ArgumentException("Deadlock event identity has conflicting evidence.", nameof(newlyRead));
            if (item.OccurredAtUtc >= windowFrom) events[item.EventId] = item;
        }
        bool overflow = events.Count > MaximumEvents;
        if (sourceLoss || overflow) gapThrough = readAtUtc;
        DeadlockWindowEvent[] retained = events.Values.OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.EventId).Take(MaximumEvents).ToArray();
        DeadlockReadWindowReason reason = overflow ? DeadlockReadWindowReason.WindowOverflow :
            sourceLoss ? DeadlockReadWindowReason.SourceIncomplete :
            !reachedEnd ? DeadlockReadWindowReason.CatchingUp :
            coveredFrom > windowFrom || gapThrough >= windowFrom ? DeadlockReadWindowReason.CoverageGap :
            DeadlockReadWindowReason.Current;
        return new(source, readAtUtc, coveredFrom, gapThrough, retained, reason);
    }
}
