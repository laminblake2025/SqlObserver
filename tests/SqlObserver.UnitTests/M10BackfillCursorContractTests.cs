using SqlObserver.Analytics;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class M10BackfillCursorContractTests
{
    [Fact]
    public void MidDayMultiPageTraversalKeepsTheSameLowerBoundAndTransitionsAtMidnight()
    {
        var job = new AnalyticsBackfillJob(
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            new ObservationTargetRevision(7),
            new DateTimeOffset(2026, 8, 25, 12, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero),
            "host.cpu.percent");

        var firstPage = AnalyticsBackfillWindows.ForDay(job, new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero));
        var secondPage = AnalyticsBackfillWindows.ForDay(job, new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero));
        var nextDay = AnalyticsBackfillWindows.ForDay(job, new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(firstPage, secondPage);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 12, 30, 0, TimeSpan.Zero), firstPage.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero), firstPage.EndUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero), nextDay.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero), nextDay.EndUtc);
    }

    [Fact]
    public void SameTimestampAndRunRowsHaveAStableTotalOrderWithoutSkips()
    {
        DateTimeOffset at = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        Guid run = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var rows = new[]
        {
            new Row("host", at, run, "host.cpu.percent", "01", 2),
            new Row("host", at, run, "host.memory.available_bytes", "02", 2),
            new Row("replication", at, run, "replication.pending_commands", "03", 3),
            new Row("replication", at, run, "replication.redo_queue_bytes", "04", 4),
        }.OrderBy(x => x.ObservedAt).ThenBy(x => x.SourceKind, StringComparer.Ordinal).ThenBy(x => x.SourceId)
         .ThenBy(x => x.MetricKey).ThenBy(x => x.DimensionHash).ThenBy(x => x.Ordinal).ToArray();

        var seen = new List<Row>();
        Row? cursor = null;
        do
        {
            Row[] page = rows.Where(x => cursor is null || Compare(x, cursor) > 0).Take(1).ToArray();
            if (page.Length == 0) break;
            seen.Add(page[0]);
            cursor = page[0];
        } while (seen.Count < rows.Length + 1);

        Assert.Equal(rows, seen);
        Assert.Equal(rows.Select(x => x.MetricKey), seen.Select(x => x.MetricKey));
    }

    [Fact]
    public void PageCarriesASeparateSafeCursorWhileRetainingBoundaryCursor()
    {
        DateTimeOffset at = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var page = new AnalyticsBackfillPage(
            new[] { new MetricPoint(at, "host.cpu.percent", 10) },
            NextCursor: "boundary",
            HasMore: true,
            SafeCursor: "last-complete-bucket");

        page.Validate();
        Assert.Equal("boundary", page.NextCursor);
        Assert.Equal("last-complete-bucket", page.SafeCursor);
    }

    private static int Compare(Row left, Row right)
    {
        int value = left.ObservedAt.CompareTo(right.ObservedAt);
        if (value != 0) return value;
        value = string.CompareOrdinal(left.SourceKind, right.SourceKind);
        if (value != 0) return value;
        value = left.SourceId.CompareTo(right.SourceId);
        if (value != 0) return value;
        value = string.CompareOrdinal(left.MetricKey, right.MetricKey);
        if (value != 0) return value;
        value = string.CompareOrdinal(left.DimensionHash, right.DimensionHash);
        return value != 0 ? value : left.Ordinal.CompareTo(right.Ordinal);
    }

    private sealed record Row(string SourceKind, DateTimeOffset ObservedAt, Guid SourceId, string MetricKey, string DimensionHash, int Ordinal);
}
