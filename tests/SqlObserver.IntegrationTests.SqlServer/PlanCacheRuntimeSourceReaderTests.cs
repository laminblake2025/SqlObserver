using System.Data;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheRuntimeSourceReaderTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly ObservationTargetRevision Revision = new(2);
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LoadsAllPinnedVariantsAndRejectsUnsupportedVersions()
    {
        var bundle = PlanCacheRuntimeSourceBundle.LoadEmbedded();
        foreach (int major in new[] { 15, 16, 17 })
        {
            string sql = bundle.GetSql(major);
            Assert.True(sql.IndexOf("INSERT @candidates", StringComparison.Ordinal) <
                sql.IndexOf("sys.dm_exec_plan_attributes", StringComparison.Ordinal));
            Assert.DoesNotContain("sys.dm_exec_sql_text", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("sys.dm_exec_query_plan", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("sys.database_recovery_status", sql, StringComparison.Ordinal);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => bundle.GetSql(14));
    }

    [Fact]
    public async Task CompleteStatementRowsRetainScopeCountersAndBoundedCaptureWindow()
    {
        using var reader = new DataTableReader([Header(3, excluded: 1, emitted: 2), Rows(Row('a'), Row('b'))]);
        var page = await Read(reader);
        Assert.True(page.Exhaustive); Assert.Equal(Target, page.TargetId); Assert.Equal(Revision, page.TargetRevision);
        Assert.Equal(3, page.CandidateRows); Assert.Equal(1, page.ExcludedRows);
        Assert.Equal(2000, page.CandidateLimit);
        Assert.Equal(At, page.ReadStartedAtUtc); Assert.Equal(At.AddMilliseconds(20), page.ReadCompletedAtUtc);
        Assert.Equal(new string('d', 64), page.SourceEpoch);
        Assert.Equal(2, page.Rows.Count); Assert.Equal(1001L, page.Rows[0].Counters.CpuMicroseconds);
        Assert.Equal(7L, page.Rows[0].Counters.Executions); Assert.Equal(3L, page.Rows[0].Counters.Rows);
        Assert.NotEqual(page.Rows[0].CacheEntryFingerprint, page.Rows[1].CacheEntryFingerprint);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task UnresolvedOrInvisibleCandidatesCannotClaimExhaustiveCoverage(int unresolved, int invisible)
    {
        using var reader = new DataTableReader([Header(2, unresolved, invisible, emitted: 1), Rows(Row('a'))]);
        var page = await Read(reader);
        Assert.False(page.Exhaustive); Assert.Single(page.Rows);
        Assert.Equal(unresolved, page.UnresolvedRows); Assert.Equal(invisible, page.InvisibleDatabaseRows);
    }

    [Fact]
    public async Task ExcludedLookaheadStillLimitsGlobalCoverageWhileSelectedRowsKeepTheirCounters()
    {
        using var reader = new DataTableReader([Header(3, excluded: 1, emitted: 2), Rows(Row('a'), Row('b'))]);
        var page = await Read(reader, limit: 2);
        Assert.False(page.Exhaustive); Assert.Equal(2, page.Rows.Count); Assert.Equal(3, page.CandidateRows);
        Assert.Equal(2, page.CandidateLimit);
        Assert.All(page.Rows, item => Assert.Equal(7L, item.Counters.Executions));
        using var excess = new DataTableReader([Header(3, emitted: 3), Rows(Row('a'), Row('b'), Row('c'))]);
        var capped = await Read(excess, limit: 2);
        Assert.False(capped.Exhaustive); Assert.Equal(2, capped.Rows.Count);
        Assert.Equal(2, capped.CandidateLimit);
    }

    [Fact]
    public async Task EmptyResultHasNoStatementDeltaOrIncarnation()
    {
        using var reader = new DataTableReader([Header(0), Rows()]);
        var page = await Read(reader);
        Assert.True(page.Exhaustive); Assert.Empty(page.Rows);
    }

    [Theory]
    [InlineData("counts")]
    [InlineData("negative")]
    [InlineData("probe")]
    [InlineData("clock")]
    [InlineData("budget")]
    [InlineData("format")]
    public async Task ForgedAccountingAndCaptureHeadersFailClosed(string fault)
    {
        DataTable header = Header(1, emitted: 1);
        switch (fault)
        {
            case "counts": header.Rows[0][4] = 2; break;
            case "negative": header.Rows[0][5] = -1; break;
            case "probe": header.Rows[0][4] = 2002; break;
            case "clock": header.Rows[0][2] = At.AddTicks(-1); break;
            case "budget": header.Rows[0][2] = At.AddSeconds(21); break;
            case "format": header.Rows[0][0] = 2; break;
        }
        using var reader = new DataTableReader([header, Rows(Row('a'))]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(reader));
    }

    [Fact]
    public async Task MissingDuplicateExtraOrUnscopedRowsFailClosed()
    {
        using (var missing = new DataTableReader([Header(1, emitted: 1), Rows()]))
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(missing));
        using (var duplicate = new DataTableReader([Header(2, emitted: 2), Rows(Row('a'), Row('a'))]))
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(duplicate));
        using (var extra = new DataTableReader([Header(1, emitted: 1), Rows(Row('a')), Rows()]))
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(extra));
        object[] system = Row('a'); system[0] = 4;
        using (var wrongScope = new DataTableReader([Header(1, emitted: 1), Rows(system)]))
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(wrongScope));
    }

    private static Task<PlanCacheRuntimeSourcePage> Read(DataTableReader reader, int limit = 2000) =>
        PlanCacheRuntimeSourceReader.ReadAsync(reader, Target, Revision, limit, CancellationToken.None);

    private static DataTable Header(int candidates, int unresolved = 0, int invisible = 0, int excluded = 0, int emitted = 0)
    {
        var table = new DataTable();
        Type[] types = [typeof(int), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(string),
            typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)];
        for (int index = 0; index < types.Length; index++) table.Columns.Add($"h{index}", types[index]);
        table.Rows.Add(1, At, At.AddMilliseconds(20), new string('d', 64), candidates, unresolved, invisible, excluded, emitted);
        return table;
    }

    private static DataTable Rows(params object[][] rows)
    {
        var table = new DataTable();
        Type[] types = [typeof(int), typeof(string), typeof(string), typeof(string),
            typeof(long), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long)];
        for (int index = 0; index < types.Length; index++) table.Columns.Add($"r{index}", types[index]);
        foreach (object[] row in rows) table.Rows.Add(row);
        return table;
    }

    private static object[] Row(char entry) => [5, new string('a', 64), new string('b', 64),
        new string(entry, 64), 1001L, 2002L, 7L, 20L, 2L, 3L];
}
