using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Numeric file tokens and a buffer offset; no provider path or event text.</summary>
public sealed record XeFilePosition
{
    public XeFilePosition(long fileGeneration, int rolloverIndex, long bufferOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(rolloverIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        FileGeneration = fileGeneration;
        RolloverIndex = rolloverIndex;
        BufferOffset = bufferOffset;
    }

    public long FileGeneration { get; }
    public int RolloverIndex { get; }
    public long BufferOffset { get; }
    public override string ToString() => "XE file position";
}

/// <summary>Exact target revision and digest of the trusted XE source incarnation.</summary>
public sealed record XeReadIdentity
{
    public XeReadIdentity(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, string sourceEpoch)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        if (sourceEpoch is not { Length: 64 } || sourceEpoch.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The XE source epoch must be a canonical SHA-256 digest.", nameof(sourceEpoch));
        SourceEpoch = sourceEpoch;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public string SourceEpoch { get; }
    public override string ToString() => "XE read identity";
}

/// <summary>A resume point after a fully enumerated buffer, to be persisted with its canonical run.</summary>
public sealed record XeReadBookmark
{
    public XeReadBookmark(XeReadIdentity identity, XeFilePosition position)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Position = position ?? throw new ArgumentNullException(nameof(position));
    }

    public XeReadIdentity Identity { get; }
    public XeFilePosition Position { get; }
    public override string ToString() => "XE read bookmark";
}

public enum XeFileReadProgressKind { CaughtUp = 1, CatchingUp = 2, ResetRequired = 3 }

/// <summary>Transport progress alone never proves a complete five-minute alert window.</summary>
public sealed record XeFileReadProgress(
    XeFileReadProgressKind Kind, XeReadBookmark? SafeBookmark, int RowsRead, int CompleteBuffers);

/// <summary>
/// Finds a safe resume point from all event types in file-generation/buffer order.
/// SQL Server's initial offset skips the entire buffer, including unread events.
/// The caller must commit accepted evidence and visibility gaps atomically before
/// persisting this progress. End-of-stream must come from the provider, not a row cap.
/// </summary>
public static class XeFileReadProgressCalculator
{
    // The later source reader has an 8,192-row budget plus a truncation probe.
    public const int MaximumRows = 8_193;

    public static XeFileReadProgress Compare(XeReadBookmark? previous, XeReadIdentity source,
        IReadOnlyList<XeFilePosition> rows, bool reachedEnd)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > MaximumRows)
            throw new ArgumentOutOfRangeException(nameof(rows), "XE read progress exceeds its row budget.");
        if (previous is not null && previous.Identity.TargetId != source.TargetId)
            throw new ArgumentException("An XE bookmark belongs to a different target.", nameof(previous));
        if (previous is not null && previous.Identity != source)
            return new(XeFileReadProgressKind.ResetRequired, null, rows.Count, 0);

        XeFilePosition? last = previous?.Position;
        XeFilePosition? frontier = null;
        int buffers = 0;
        foreach (XeFilePosition row in rows)
        {
            if (row is null) throw new ArgumentException("XE read progress cannot contain null positions.", nameof(rows));
            if (last is not null)
            {
                bool ambiguousFile = row.FileGeneration == last.FileGeneration && row.RolloverIndex != last.RolloverIndex;
                bool regressed = row.FileGeneration < last.FileGeneration ||
                    row.FileGeneration == last.FileGeneration && row.BufferOffset < last.BufferOffset;
                bool repeatsBookmark = buffers == 0 && row == last;
                if (ambiguousFile || regressed || repeatsBookmark)
                    return new(XeFileReadProgressKind.ResetRequired, null, rows.Count, 0);
                if (row != last && buffers > 0) frontier = last;
            }
            if (row != last) buffers++;
            last = row;
        }
        if (reachedEnd && buffers > 0) frontier = last;
        int completeBuffers = reachedEnd ? buffers : Math.Max(0, buffers - 1);
        return new(reachedEnd ? XeFileReadProgressKind.CaughtUp : XeFileReadProgressKind.CatchingUp,
            frontier is null ? previous : new(source, frontier), rows.Count, completeBuffers);
    }
}
