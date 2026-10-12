using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public enum AvailabilityGroupEvidenceKind { Replicas = 1, Databases = 2 }
public enum AvailabilityGroupEvidenceStatus
{
    NotCollected = 1, CollectionFailed = 2, EvidenceNotRetained = 3,
    WithheldByByteLimit = 4, NoData = 5, Retained = 6,
}

/// <summary>Continuation identity; transport adapters must authenticate the encoded cursor.</summary>
public sealed record AvailabilityGroupEvidenceCursor(
    MonitoredInstanceId TargetId, DateTimeOffset AtUtc, long TargetRevision, Guid RunId,
    AvailabilityGroupEvidenceKind Stream, string GroupFingerprint,
    string ReplicaFingerprint, string? DatabaseFingerprint);

public sealed class AvailabilityGroupEvidenceRequest
{
    public AvailabilityGroupEvidenceRequest(MonitoredInstanceId targetId, DateTimeOffset atUtc,
        AvailabilityGroupEvidenceKind stream, int limit, RepositoryCallTimeout timeout,
        AvailabilityGroupEvidenceCursor? cursor = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (atUtc.Offset != TimeSpan.Zero || atUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0 ||
            atUtc < DateTimeOffset.MinValue.AddDays(7))
            throw new ArgumentException("AG cutoff must be microsecond-aligned UTC with a seven-day lookback.", nameof(atUtc));
        if (!Enum.IsDefined(stream)) throw new ArgumentOutOfRangeException(nameof(stream));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (cursor is not null && (cursor.TargetId != targetId || cursor.AtUtc != atUtc ||
            cursor.AtUtc.Offset != TimeSpan.Zero || cursor.TargetRevision < 1 || cursor.RunId == Guid.Empty ||
            cursor.Stream != stream || !ValidFingerprint(cursor.GroupFingerprint) ||
            !ValidFingerprint(cursor.ReplicaFingerprint) ||
            (stream == AvailabilityGroupEvidenceKind.Databases
                ? !ValidFingerprint(cursor.DatabaseFingerprint) : cursor.DatabaseFingerprint is not null)))
            throw new ArgumentException("AG cursor does not match the target, cutoff or evidence stream.", nameof(cursor));
        AtUtc = atUtc;
        Stream = stream;
        Limit = limit;
        Cursor = cursor;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset AtUtc { get; }
    public AvailabilityGroupEvidenceKind Stream { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }
    public AvailabilityGroupEvidenceCursor? Cursor { get; }

    internal static bool ValidFingerprint(string? value) => value is { Length: 64 } &&
        value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void RequireStream(AvailabilityGroupEvidenceKind expected)
    {
        if (Stream != expected) throw new ArgumentException("AG request uses the wrong evidence stream.", nameof(expected));
    }
}

/// <summary>Source observation time stays unknown when no retained evidence carries it.</summary>
public sealed record AvailabilityGroupEvidenceHeader(
    MonitoredInstanceId TargetId, DateTimeOffset AtUtc, long TargetRevision, Guid? RunId,
    DateTimeOffset? SourceObservedAtUtc, DateTimeOffset? CompletedAtUtc,
    OperationalObservationState State, AvailabilityGroupEvidenceStatus EvidenceStatus,
    int SourceRows, int OutputItems, long RetainedItems, CollectorLossKind LossKind,
    bool Truncated, bool CollectorEnabled);

public sealed record AvailabilityGroupEvidencePage<T>(AvailabilityGroupEvidenceHeader Header,
    IReadOnlyList<T> Items, AvailabilityGroupEvidenceCursor? NextCursor);

public sealed class AvailabilityGroupEvidenceCursorStaleException(Exception? innerException = null)
    : Exception("AG snapshot changed; restart paging from the first page.", innerException);

public interface IAvailabilityGroupEvidenceRepositoryPort
{
    ValueTask<AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>> ReadReplicasAsync(
        AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>> ReadDatabasesAsync(
        AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken);
}
