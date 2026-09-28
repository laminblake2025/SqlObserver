using System.Data.Common;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed record QueryStoreRuntimeSourceRow(QueryStoreRuntimeWatermark Watermark,
    long PlanSourceId, long QueryTextSourceId);

internal sealed record QueryStoreRuntimeSourcePage(IReadOnlyList<QueryStoreRuntimeSourceRow> Rows,
    int SourceRowsRead, bool Complete);

internal enum QueryStoreWaitCaptureMode { On = 1, Off = 2, Unavailable = 3 }

internal sealed record QueryStoreWaitSourceRow(QueryStoreRuntimeWatermarkKey Key,
    int Category, long WaitMilliseconds);

internal sealed record QueryStoreWaitSourcePage(QueryStoreWaitCaptureMode CaptureMode,
    DateTimeOffset ObservedAtUtc, IReadOnlyList<QueryStoreWaitSourceRow> Rows,
    int SourceRowsRead, bool Complete);

/// <summary>Reads the fixed-column candidate Query Store groups without treating a capped read as complete.</summary>
internal static class QueryStoreGroupedSourceReader
{
    internal const int MaximumGroups = 2_000;

    internal static async Task<QueryStoreRuntimeSourcePage> ReadRuntimeAsync(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        int expectedDatabaseId, int limit, CancellationToken cancellationToken)
    {
        ValidateRequest(reader, targetId, targetRevision, expectedDatabaseId, limit);
        if (reader.FieldCount != 20)
            throw new InvalidDataException("Query Store runtime source changed its reviewed column shape.");
        var rows = new List<QueryStoreRuntimeSourceRow>(limit);
        var keys = new HashSet<QueryStoreRuntimeWatermarkKey>();
        int sourceRowsRead = 0;
        bool complete = true;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sourceRowsRead++;
            if (sourceRowsRead > limit)
            {
                complete = false;
                break;
            }
            QueryStoreRuntimeSourceRow row = MapRuntime(reader, targetId, targetRevision, expectedDatabaseId);
            if (!keys.Add(row.Watermark.Key))
                throw new InvalidDataException("Query Store runtime source repeated a complete group identity.");
            rows.Add(row);
        }
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Query Store runtime source returned an extra result set.");
        return new(rows, sourceRowsRead, complete);
    }

    internal static async Task<QueryStoreWaitSourcePage> ReadWaitsAsync(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        int expectedDatabaseId, int limit, CancellationToken cancellationToken)
    {
        ValidateRequest(reader, targetId, targetRevision, expectedDatabaseId, limit);
        if (reader.FieldCount != 2 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Query Store wait source omitted its capture status and read time.");
        QueryStoreWaitCaptureMode mode = reader.GetString(0) switch
        {
            "ON" => QueryStoreWaitCaptureMode.On,
            "OFF" => QueryStoreWaitCaptureMode.Off,
            "UNAVAILABLE" => QueryStoreWaitCaptureMode.Unavailable,
            _ => throw new InvalidDataException("Query Store wait capture status was not allowlisted."),
        };
        DateTimeOffset observedAtUtc = reader.GetFieldValue<DateTimeOffset>(1);
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Query Store wait read time must be UTC.");
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Query Store wait source returned multiple capture statuses.");
        bool hasGroups = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        if (mode != QueryStoreWaitCaptureMode.On)
        {
            if (hasGroups)
                throw new InvalidDataException("Disabled Query Store wait capture returned a group result set.");
            return new(mode, observedAtUtc, [], 0, false);
        }
        if (!hasGroups || reader.FieldCount != 14)
            throw new InvalidDataException("Enabled Query Store wait source omitted its reviewed group result set.");

        var rows = new List<QueryStoreWaitSourceRow>(limit);
        var keys = new HashSet<(QueryStoreRuntimeWatermarkKey Key, int Category)>();
        int sourceRowsRead = 0;
        bool complete = true;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sourceRowsRead++;
            if (sourceRowsRead > limit)
            {
                complete = false;
                break;
            }
            QueryStoreWaitSourceRow row = MapWait(reader, targetId, targetRevision,
                expectedDatabaseId, observedAtUtc);
            if (!keys.Add((row.Key, row.Category)))
                throw new InvalidDataException("Query Store wait source repeated a category group identity.");
            rows.Add(row);
        }
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Query Store wait source returned an extra result set.");
        return new(mode, observedAtUtc, rows, sourceRowsRead, complete);
    }

    internal static IReadOnlyList<QueryStoreWaitWatermark>? CompleteWaitSnapshots(
        QueryStoreRuntimeSourcePage runtime, QueryStoreWaitSourcePage waits)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(waits);
        if (!runtime.Complete || !waits.Complete || waits.CaptureMode != QueryStoreWaitCaptureMode.On)
            return null;
        var categories = runtime.Rows.ToDictionary(static row => row.Watermark.Key,
            static _ => new Dictionary<int, long>());
        foreach (QueryStoreWaitSourceRow row in waits.Rows)
        {
            if (!categories.TryGetValue(row.Key, out Dictionary<int, long>? group) ||
                !group.TryAdd(row.Category, row.WaitMilliseconds))
                throw new InvalidDataException("Query Store wait group has no unique matching runtime group.");
        }
        return runtime.Rows.Select(row => new QueryStoreWaitWatermark(row.Watermark.Key,
            categories[row.Watermark.Key], waits.ObservedAtUtc)).ToArray();
    }

    private static void ValidateRequest(DbDataReader reader, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, int expectedDatabaseId, int limit)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (expectedDatabaseId is <= 0 or > 32767 || limit is <= 0 or > MaximumGroups)
            throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static QueryStoreRuntimeSourceRow MapRuntime(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, int expectedDatabaseId)
    {
        try
        {
            (QueryStoreRuntimeWatermarkKey key, long planId) = ReadKey(reader, targetId, targetRevision,
                expectedDatabaseId);
            QueryStoreRuntimeCounters counters = new(reader.GetInt64(10), reader.GetInt64(11),
                reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15));
            QueryStoreRuntimeWatermark watermark = new(key, counters,
                reader.GetFieldValue<DateTimeOffset>(16), reader.GetFieldValue<DateTimeOffset>(17),
                reader.GetFieldValue<DateTimeOffset>(18));
            long textId = reader.GetInt64(19);
            if (textId <= 0)
                throw new InvalidDataException("Query Store runtime source identifiers must be positive.");
            return new(watermark, planId, textId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException("Query Store runtime source row was invalid.", exception);
        }
    }

    private static QueryStoreWaitSourceRow MapWait(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        int expectedDatabaseId, DateTimeOffset observedAtUtc)
    {
        try
        {
            (QueryStoreRuntimeWatermarkKey key, _) = ReadKey(reader, targetId, targetRevision,
                expectedDatabaseId);
            int category = reader.GetInt32(10);
            long milliseconds = reader.GetInt64(11);
            DateTimeOffset rowObservedAtUtc = reader.GetFieldValue<DateTimeOffset>(12);
            long textId = reader.GetInt64(13);
            if (category is < 0 or > 31 || milliseconds < 0 ||
                rowObservedAtUtc != observedAtUtc || textId <= 0)
                throw new InvalidDataException("Query Store wait category, timestamp, or source identity is invalid.");
            return new(key, category, milliseconds);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException("Query Store wait source row was invalid.", exception);
        }
    }

    private static (QueryStoreRuntimeWatermarkKey Key, long PlanId) ReadKey(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, int expectedDatabaseId)
    {
        int databaseId = reader.GetInt32(0);
        if (databaseId != expectedDatabaseId)
            throw new InvalidDataException("Query Store source database ID did not match the selected database.");
        Guid incarnation = reader.GetGuid(1);
        QueryOpaqueIdentity query = new(databaseId, reader.GetString(2));
        PlanOpaqueIdentity plan = new(query, reader.GetString(3));
        long planId = reader.GetInt64(4);
        if (planId <= 0)
            throw new InvalidDataException("Query Store plan source ID must be positive.");
        QueryStoreRuntimeWatermarkKey key = new(targetId, targetRevision, incarnation, query, plan,
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt64(6),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetByte(9));
        return (key, planId);
    }
}
