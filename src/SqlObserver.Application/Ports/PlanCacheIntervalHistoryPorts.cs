using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record PlanCacheIntervalHistoryCursor(long Revision, string Snapshot, DateTimeOffset SnapshotUtc,
    long TotalRows, DateTimeOffset ObservedAtUtc, DateTimeOffset CommittedAtUtc,
    Guid CollectionRunId, string CacheEntryFingerprint);

public sealed class PlanCacheIntervalHistoryRequest
{
    public PlanCacheIntervalHistoryRequest(MonitoredInstanceId targetId, int databaseId, string databaseIncarnation,
        string queryFingerprint, string planFingerprint, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int limit, RepositoryCallTimeout timeout, PlanCacheIntervalHistoryCursor? cursor = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (databaseId is < 5 or > 32766 || !Digest(databaseIncarnation) || !Digest(queryFingerprint) || !Digest(planFingerprint)
            || !Utc(fromUtc) || !Utc(toUtc) || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7) || limit is < 1 or > 100)
            throw new ArgumentException("Cache history requires a database incarnation and query plan, whole-microsecond UTC bounds of at most seven days, and 1-100 results.");
        if (cursor is not null && (cursor.Revision < 1 || cursor.TotalRows < 1 || !SnapshotValid(cursor.Snapshot)
            || !Utc(cursor.SnapshotUtc) || !Utc(cursor.ObservedAtUtc) || !Utc(cursor.CommittedAtUtc)
            || cursor.ObservedAtUtc < fromUtc || cursor.ObservedAtUtc >= toUtc
            || cursor.CommittedAtUtc < fromUtc.AddMinutes(-5) || cursor.CommittedAtUtc >= toUtc.AddMinutes(5)
            || cursor.CollectionRunId == Guid.Empty || !Digest(cursor.CacheEntryFingerprint)))
            throw new ArgumentException("Cache history continuation is invalid.", nameof(cursor));
        DatabaseId = databaseId; DatabaseIncarnation = databaseIncarnation; QueryFingerprint = queryFingerprint;
        PlanFingerprint = planFingerprint; FromUtc = fromUtc; ToUtc = toUtc; Limit = limit; Cursor = cursor;
    }
    public MonitoredInstanceId TargetId { get; }
    public int DatabaseId { get; }
    public string DatabaseIncarnation { get; }
    public string QueryFingerprint { get; }
    public string PlanFingerprint { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }
    public PlanCacheIntervalHistoryCursor? Cursor { get; }
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.Ticks % 10 == 0;
    public static bool SnapshotValid(string? value) => long.TryParse(value, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out long sequence) && sequence > 0
        && sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) == value;
}

public sealed record PlanCacheIntervalHistoryItem(Guid CollectionRunId, string CacheEntryFingerprint, string SourceEpoch,
    DateTimeOffset ObservedAtUtc, DateTimeOffset CommittedAtUtc, bool CaptureComplete, string Transition,
    DateTimeOffset? IntervalFromUtc, DateTimeOffset? IntervalToUtc, string? CpuMilliseconds, string? DurationMilliseconds,
    string? Executions, string? LogicalReads, string? Writes, string? Rows);

public sealed record PlanCacheIntervalHistoryResult(Guid TargetId, int DatabaseId, string DatabaseIncarnation,
    string QueryFingerprint, string PlanFingerprint, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    string Semantics, string Coverage, long Revision, string Snapshot, DateTimeOffset SnapshotUtc, long TotalRows,
    bool HasMore, IReadOnlyList<PlanCacheIntervalHistoryItem> Items);

public interface IPlanCacheIntervalHistoryRepositoryPort
{
    ValueTask<PlanCacheIntervalHistoryResult> ReadAsync(PlanCacheIntervalHistoryRequest request, CancellationToken cancellationToken);
}
