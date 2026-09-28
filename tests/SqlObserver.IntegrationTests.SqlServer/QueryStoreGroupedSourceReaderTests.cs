using System.Data;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class QueryStoreGroupedSourceReaderTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly ObservationTargetRevision Revision = new(3);
    private static readonly Guid Incarnation = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VersionedGroupSourceLoadsOnlyVerifiedSql()
    {
        QueryStoreGroupedSourceBundle source = QueryStoreGroupedSourceBundle.LoadEmbedded();
        Assert.Contains("sys.query_store_runtime_stats", source.RuntimeSql, StringComparison.Ordinal);
        Assert.Contains("sys.query_store_wait_stats", source.WaitSql, StringComparison.Ordinal);
        Assert.Contains("@probe_rows", source.RuntimeSql, StringComparison.Ordinal);
        Assert.Contains("@probe_rows", source.WaitSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteRuntimeAndWaitReadsJoinExactGroupAndCategory()
    {
        using var runtimeReader = new DataTableReader(RuntimeTable(RuntimeRow()));
        QueryStoreRuntimeSourcePage runtime = await QueryStoreGroupedSourceReader.ReadRuntimeAsync(
            runtimeReader, Target, Revision, 6, 2_000, CancellationToken.None);
        Assert.True(runtime.Complete);
        Assert.Equal(1, runtime.SourceRowsRead);
        Assert.Equal(7, runtime.Rows[0].Watermark.Counters.Executions);

        using var waitReader = new DataTableReader([StatusTable("ON"), WaitTable(WaitRow())]);
        QueryStoreWaitSourcePage waits = await QueryStoreGroupedSourceReader.ReadWaitsAsync(
            waitReader, Target, Revision, 6, 2_000, CancellationToken.None);
        Assert.True(waits.Complete);
        Assert.Equal(QueryStoreWaitCaptureMode.On, waits.CaptureMode);
        IReadOnlyList<QueryStoreWaitWatermark> joined = Assert.IsAssignableFrom<IReadOnlyList<QueryStoreWaitWatermark>>(
            QueryStoreGroupedSourceReader.CompleteWaitSnapshots(runtime, waits));
        Assert.Single(joined);
        Assert.Equal(runtime.Rows[0].Watermark.Key, joined[0].Key);
        Assert.Equal(4_030, joined[0].CategoryMilliseconds[3]);
    }

    [Fact]
    public async Task EnabledEmptyWaitResultIsKnownZeroButOffIsUnavailable()
    {
        using var runtimeReader = new DataTableReader(RuntimeTable(RuntimeRow()));
        QueryStoreRuntimeSourcePage runtime = await QueryStoreGroupedSourceReader.ReadRuntimeAsync(
            runtimeReader, Target, Revision, 6, 2_000, CancellationToken.None);

        using var emptyReader = new DataTableReader([StatusTable("ON"), WaitTable()]);
        QueryStoreWaitSourcePage empty = await QueryStoreGroupedSourceReader.ReadWaitsAsync(
            emptyReader, Target, Revision, 6, 2_000, CancellationToken.None);
        IReadOnlyList<QueryStoreWaitWatermark> zero = Assert.IsAssignableFrom<IReadOnlyList<QueryStoreWaitWatermark>>(
            QueryStoreGroupedSourceReader.CompleteWaitSnapshots(runtime, empty));
        Assert.Single(zero);
        Assert.Empty(zero[0].CategoryMilliseconds);
        Assert.Equal(Start.AddMinutes(4), zero[0].ObservedAtUtc);

        using var offReader = new DataTableReader([StatusTable("OFF")]);
        QueryStoreWaitSourcePage off = await QueryStoreGroupedSourceReader.ReadWaitsAsync(
            offReader, Target, Revision, 6, 2_000, CancellationToken.None);
        Assert.False(off.Complete);
        Assert.Null(QueryStoreGroupedSourceReader.CompleteWaitSnapshots(runtime, off));
    }

    [Fact]
    public async Task LookaheadAndUnmatchedWaitsCannotPublishACompleteSnapshot()
    {
        object[] second = RuntimeRow();
        second[6] = 2L;
        using var cappedReader = new DataTableReader(RuntimeTable(RuntimeRow(), second));
        QueryStoreRuntimeSourcePage capped = await QueryStoreGroupedSourceReader.ReadRuntimeAsync(
            cappedReader, Target, Revision, 6, 1, CancellationToken.None);
        Assert.False(capped.Complete);
        Assert.Equal(2, capped.SourceRowsRead);
        using var waitReader = new DataTableReader([StatusTable("ON"), WaitTable(WaitRow())]);
        QueryStoreWaitSourcePage waits = await QueryStoreGroupedSourceReader.ReadWaitsAsync(
            waitReader, Target, Revision, 6, 2_000, CancellationToken.None);
        Assert.Null(QueryStoreGroupedSourceReader.CompleteWaitSnapshots(capped, waits));

        using var completeReader = new DataTableReader(RuntimeTable(RuntimeRow()));
        QueryStoreRuntimeSourcePage complete = await QueryStoreGroupedSourceReader.ReadRuntimeAsync(
            completeReader, Target, Revision, 6, 2_000, CancellationToken.None);
        object[] unmatched = WaitRow();
        unmatched[6] = 99L;
        using var unmatchedReader = new DataTableReader([StatusTable("ON"), WaitTable(unmatched)]);
        QueryStoreWaitSourcePage wrong = await QueryStoreGroupedSourceReader.ReadWaitsAsync(
            unmatchedReader, Target, Revision, 6, 2_000, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => QueryStoreGroupedSourceReader.CompleteWaitSnapshots(complete, wrong));

        object[] secondWait = WaitRow();
        secondWait[10] = 4;
        using var cappedWaitReader = new DataTableReader([StatusTable("ON"), WaitTable(WaitRow(), secondWait)]);
        QueryStoreWaitSourcePage cappedWaits = await QueryStoreGroupedSourceReader.ReadWaitsAsync(
            cappedWaitReader, Target, Revision, 6, 1, CancellationToken.None);
        Assert.False(cappedWaits.Complete);
        Assert.Equal(2, cappedWaits.SourceRowsRead);
        Assert.Null(QueryStoreGroupedSourceReader.CompleteWaitSnapshots(complete, cappedWaits));
    }

    [Fact]
    public async Task WrongDatabaseAndCaptureStatusFailClosed()
    {
        object[] wrongDatabase = RuntimeRow();
        wrongDatabase[0] = 7;
        using var runtimeReader = new DataTableReader(RuntimeTable(wrongDatabase));
        await Assert.ThrowsAsync<InvalidDataException>(() => QueryStoreGroupedSourceReader.ReadRuntimeAsync(
            runtimeReader, Target, Revision, 6, 2_000, CancellationToken.None));

        using var waitReader = new DataTableReader([StatusTable("UNKNOWN")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => QueryStoreGroupedSourceReader.ReadWaitsAsync(
            waitReader, Target, Revision, 6, 2_000, CancellationToken.None));

        using var duplicateReader = new DataTableReader(RuntimeTable(RuntimeRow(), RuntimeRow()));
        await Assert.ThrowsAsync<InvalidDataException>(() => QueryStoreGroupedSourceReader.ReadRuntimeAsync(
            duplicateReader, Target, Revision, 6, 2_000, CancellationToken.None));
    }

    private static DataTable RuntimeTable(params object[][] rows) => Table(
        [typeof(int), typeof(Guid), typeof(string), typeof(string), typeof(long),
         typeof(DateTimeOffset), typeof(long), typeof(DateTimeOffset), typeof(DateTimeOffset),
         typeof(byte), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long),
         typeof(long), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(DateTimeOffset),
         typeof(long)], rows);

    private static DataTable WaitTable(params object[][] rows) => Table(
        [typeof(int), typeof(Guid), typeof(string), typeof(string), typeof(long),
         typeof(DateTimeOffset), typeof(long), typeof(DateTimeOffset), typeof(DateTimeOffset),
         typeof(byte), typeof(int), typeof(long), typeof(DateTimeOffset), typeof(long)], rows);

    private static DataTable StatusTable(string mode) => Table(
        [typeof(string), typeof(DateTimeOffset)], [[mode, Start.AddMinutes(4)]]);

    private static DataTable Table(Type[] types, params object[][] rows)
    {
        var table = new DataTable();
        for (int index = 0; index < types.Length; index++)
            table.Columns.Add($"column_{index}", types[index]);
        foreach (object[] row in rows) table.Rows.Add(row);
        return table;
    }

    private static object[] RuntimeRow() =>
    [
        6, Incarnation, new string('a', 64), new string('c', 64), 11L,
        Start.AddHours(-1), 1L, Start, Start.AddDays(1), (byte)0,
        100L, 200L, 7L, 30L, 4L, 7L,
        Start.AddMinutes(1), Start.AddMinutes(3), Start.AddMinutes(4), 17L,
    ];

    private static object[] WaitRow() =>
    [
        6, Incarnation, new string('a', 64), new string('c', 64), 11L,
        Start.AddHours(-1), 1L, Start, Start.AddDays(1), (byte)0,
        3, 4_030L, Start.AddMinutes(4), 17L,
    ];
}
