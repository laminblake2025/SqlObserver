namespace SqlObserver.Domain.Collection;

/// <summary>One complete Query Store wait-category snapshot for a runtime group.</summary>
public sealed class QueryStoreWaitWatermark
{
    public QueryStoreWaitWatermark(QueryStoreRuntimeWatermarkKey key,
        IReadOnlyDictionary<int, long> categoryMilliseconds, DateTimeOffset observedAtUtc)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        ArgumentNullException.ThrowIfNull(categoryMilliseconds);
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Query Store wait observation time must be UTC.", nameof(observedAtUtc));
        Dictionary<int, long> totals = new(categoryMilliseconds.Count);
        foreach ((int category, long milliseconds) in categoryMilliseconds)
        {
            if (category is < 0 or > 31 || milliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(categoryMilliseconds),
                    "Query Store wait categories and counters must be bounded and nonnegative.");
            totals.Add(category, milliseconds);
        }
        CategoryMilliseconds = new System.Collections.ObjectModel.ReadOnlyDictionary<int, long>(totals);
        ObservedAtUtc = observedAtUtc;
    }

    public QueryStoreRuntimeWatermarkKey Key { get; }
    public IReadOnlyDictionary<int, long> CategoryMilliseconds { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public enum QueryStoreWaitTransitionKind
{
    Incomplete = 1,
    BaselineUnavailable = 2,
    Comparable = 3,
    Reset = 4,
    Stale = 5,
    RuntimeIncomparable = 6
}

public sealed record QueryStoreWaitTransition(QueryStoreWaitTransitionKind Kind,
    IReadOnlyDictionary<int, long>? DeltaMilliseconds, QueryStoreWaitWatermark? NextWatermark);

/// <summary>Compares complete category sets only when the matching runtime group is comparable.</summary>
public static class QueryStoreWaitWatermarkCalculator
{
    public static QueryStoreWaitTransition Compare(QueryStoreWaitWatermark? previous,
        QueryStoreWaitWatermark current, bool sourceComplete,
        QueryStoreRuntimeTransitionKind runtimeTransition)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is not null && previous.Key != current.Key)
            throw new ArgumentException("Wait watermark identities do not match.", nameof(previous));
        if (!Enum.IsDefined(runtimeTransition))
            throw new ArgumentOutOfRangeException(nameof(runtimeTransition));
        if (!sourceComplete)
            return new(QueryStoreWaitTransitionKind.Incomplete, null, null);
        if (runtimeTransition is QueryStoreRuntimeTransitionKind.Incomplete or QueryStoreRuntimeTransitionKind.Stale)
            return new(QueryStoreWaitTransitionKind.RuntimeIncomparable, null, null);
        if (previous is not null && current.ObservedAtUtc <= previous.ObservedAtUtc)
            return new(QueryStoreWaitTransitionKind.Stale, null, null);
        if (previous is null || runtimeTransition is QueryStoreRuntimeTransitionKind.BaselineUnavailable
            or QueryStoreRuntimeTransitionKind.Reset or QueryStoreRuntimeTransitionKind.EpochAmbiguous)
            return new(QueryStoreWaitTransitionKind.BaselineUnavailable, null, current);
        if (runtimeTransition != QueryStoreRuntimeTransitionKind.Comparable)
            throw new ArgumentOutOfRangeException(nameof(runtimeTransition));

        Dictionary<int, long> delta = new();
        foreach ((int category, long priorMilliseconds) in previous.CategoryMilliseconds)
        {
            long currentMilliseconds = current.CategoryMilliseconds.GetValueOrDefault(category);
            if (currentMilliseconds < priorMilliseconds)
                return new(QueryStoreWaitTransitionKind.Reset, null, current);
            delta.Add(category, currentMilliseconds - priorMilliseconds);
        }
        foreach ((int category, long currentMilliseconds) in current.CategoryMilliseconds)
        {
            delta.TryAdd(category, currentMilliseconds);
        }
        return new(QueryStoreWaitTransitionKind.Comparable,
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, long>(delta), current);
    }
}
