using System.Collections.ObjectModel;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public enum QueryStoreGroupWaitCoverage
{
    Complete = 1,
    CaptureOff = 2,
    Unavailable = 3,
    Truncated = 4,
    Mixed = 5,
}

/// <summary>A complete runtime group and, when captured, its complete category set.</summary>
public sealed record QueryStoreGroupObservation
{
    public QueryStoreGroupObservation(QueryStoreRuntimeWatermark runtime,
        QueryStoreWaitWatermark? waits)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        if (waits is not null && waits.Key != runtime.Key)
            throw new ArgumentException("Query Store wait and runtime groups must share one identity.", nameof(waits));
        Waits = waits;
    }

    public QueryStoreRuntimeWatermark Runtime { get; }
    public QueryStoreWaitWatermark? Waits { get; }
    public int EstimatedSizeBytes => checked(320 + (Waits?.CategoryMilliseconds.Count ?? 0) * 24);
}

/// <summary>
/// One bounded target source window. Incomplete runtime coverage never carries
/// a publishable subset; wait coverage is independent of runtime coverage.
/// </summary>
public sealed class QueryStoreGroupOutputBatch
{
    public const int MaximumItems = 2_000;
    private readonly ReadOnlyCollection<QueryStoreGroupObservation> items;

    public QueryStoreGroupOutputBatch(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc, bool runtimeComplete,
        QueryStoreGroupWaitCoverage waitCoverage,
        IReadOnlyList<QueryStoreGroupObservation> observations,
        IReadOnlyDictionary<int, QueryStoreGroupWaitCoverage>? databaseWaitCoverage = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        ArgumentNullException.ThrowIfNull(observations);
        if (windowStartUtc.Offset != TimeSpan.Zero || windowEndUtc.Offset != TimeSpan.Zero ||
            windowEndUtc <= windowStartUtc || windowEndUtc - windowStartUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Query Store group source window must be bounded UTC time.");
        if (!Enum.IsDefined(waitCoverage) || observations.Count > MaximumItems ||
            !runtimeComplete && (observations.Count != 0 || waitCoverage != QueryStoreGroupWaitCoverage.Unavailable ||
                                 databaseWaitCoverage is not null) ||
            (waitCoverage == QueryStoreGroupWaitCoverage.Mixed) != (databaseWaitCoverage is not null))
            throw new ArgumentException("Query Store group source coverage is inconsistent.");

        Dictionary<int, QueryStoreGroupWaitCoverage>? coverageByDatabase = null;
        if (databaseWaitCoverage is not null)
        {
            if (databaseWaitCoverage.Count is < 2 or > 256 ||
                databaseWaitCoverage.Any(static item => item.Key is <= 4 or > 32767 ||
                    !Enum.IsDefined(item.Value) || item.Value == QueryStoreGroupWaitCoverage.Mixed) ||
                databaseWaitCoverage.Values.Distinct().Count() < 2)
                throw new ArgumentException("Mixed Query Store wait coverage requires distinct database states.",
                    nameof(databaseWaitCoverage));
            coverageByDatabase = new Dictionary<int, QueryStoreGroupWaitCoverage>(databaseWaitCoverage);
        }

        var keys = new HashSet<QueryStoreRuntimeWatermarkKey>();
        var copy = new QueryStoreGroupObservation[observations.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            QueryStoreGroupObservation item = observations[i]
                ?? throw new ArgumentException("Query Store group output cannot contain a null row.", nameof(observations));
            QueryStoreRuntimeWatermarkKey key = item.Runtime.Key;
            if (key.TargetId != targetId || key.TargetRevision != targetRevision ||
                !keys.Add(key) ||
                (coverageByDatabase is not null &&
                 !coverageByDatabase.ContainsKey(key.Query.DatabaseId)) ||
                ((coverageByDatabase?.GetValueOrDefault(key.Query.DatabaseId) ?? waitCoverage) ==
                 QueryStoreGroupWaitCoverage.Complete) != (item.Waits is not null))
                throw new ArgumentException("Query Store group output has a foreign, duplicate, or uncovered group.", nameof(observations));
            copy[i] = item;
        }
        WindowStartUtc = windowStartUtc;
        WindowEndUtc = windowEndUtc;
        RuntimeComplete = runtimeComplete;
        WaitCoverage = waitCoverage;
        DatabaseWaitCoverage = coverageByDatabase is null ? null :
            new ReadOnlyDictionary<int, QueryStoreGroupWaitCoverage>(coverageByDatabase);
        items = Array.AsReadOnly(copy);
        EstimatedSizeBytes = checked(128 + copy.Sum(static item => item.EstimatedSizeBytes) +
            (coverageByDatabase?.Count ?? 0) * 24);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public DateTimeOffset WindowStartUtc { get; }
    public DateTimeOffset WindowEndUtc { get; }
    public bool RuntimeComplete { get; }
    public QueryStoreGroupWaitCoverage WaitCoverage { get; }
    public IReadOnlyDictionary<int, QueryStoreGroupWaitCoverage>? DatabaseWaitCoverage { get; }
    public bool HasTruncatedWaitCoverage => WaitCoverage == QueryStoreGroupWaitCoverage.Truncated ||
        DatabaseWaitCoverage?.Values.Contains(QueryStoreGroupWaitCoverage.Truncated) == true;
    public IReadOnlyList<QueryStoreGroupObservation> Items => items;
    public int EstimatedSizeBytes { get; }
}
