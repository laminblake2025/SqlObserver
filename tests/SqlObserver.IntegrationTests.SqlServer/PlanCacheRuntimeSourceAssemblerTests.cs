using System.Data;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheRuntimeSourceAssemblerTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly ObservationTargetRevision Revision = new(3);
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static string Digest(char c) => new(c, 64);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StableBracketsQualifySelectedStatementsIndependentlyOfWholeCacheCoverage(bool exhaustive)
    {
        var source = Source(exhaustive, Row(5, 'a'), Row(5, 'b'));
        var first = Snapshot(5, false); var last = Snapshot(5, true);
        var result = PlanCacheRuntimeSourceAssembler.Bind(source, [first], [last]);
        Assert.Equal(exhaustive, result.Exhaustive); Assert.Equal(0, result.UnqualifiedStatementRows);
        Assert.Equal(2, result.Statements.Count);
        Assert.All(result.Statements, statement =>
        {
            Assert.Equal(first, statement.Before); Assert.Equal(last, statement.After);
            Assert.Equal(Target, statement.Watermark.Key.TargetId);
            Assert.Equal(Revision, statement.Watermark.Key.TargetRevision);
            Assert.Equal(Digest('d'), statement.Watermark.Key.DatabaseIncarnation);
            Assert.Equal(At.AddMilliseconds(20), statement.Watermark.ObservedAtUtc);
            Assert.Equal(new PlanCacheRuntimeCounters(1001, 2002, 3, 4, 5, 6), statement.Watermark.Counters);
        });
    }

    [Theory]
    [InlineData("missing_before")]
    [InlineData("missing_after")]
    [InlineData("unknown_before")]
    [InlineData("unknown_after")]
    [InlineData("changed_incarnation")]
    [InlineData("changed_epoch_before")]
    [InlineData("changed_epoch_after")]
    [InlineData("ineligible_before")]
    [InlineData("ineligible_after")]
    [InlineData("overlap_before")]
    [InlineData("overlap_after")]
    [InlineData("expired_inventory")]
    public void UnqualifiedDatabaseCannotAdvanceWhileAnotherDatabaseRemainsUseful(string gap)
    {
        var first = Snapshot(5, false); var last = Snapshot(5, true);
        switch (gap)
        {
            case "unknown_before": first = first with { DatabaseIncarnation = null }; break;
            case "unknown_after": last = last with { DatabaseIncarnation = null }; break;
            case "changed_incarnation": last = last with { DatabaseIncarnation = Digest('e') }; break;
            case "changed_epoch_before": first = first with { SourceEpoch = Digest('f') }; break;
            case "changed_epoch_after": last = last with { SourceEpoch = Digest('f') }; break;
            case "ineligible_before": first = first with { CatalogEligible = false, DatabaseIncarnation = null }; break;
            case "ineligible_after": last = last with { CatalogEligible = false, DatabaseIncarnation = null }; break;
            case "overlap_before": first = first with { ReadCompletedAtUtc = At.AddMilliseconds(1) }; break;
            case "overlap_after": last = last with { ReadStartedAtUtc = At.AddMilliseconds(19) }; break;
            case "expired_inventory": first = first with { ReadStartedAtUtc = At.AddSeconds(-31), ReadCompletedAtUtc = At.AddSeconds(-30) }; break;
        }
        PlanCacheDatabaseIdentitySnapshot[] before = gap == "missing_before" ? [Snapshot(6, false)] : [first, Snapshot(6, false)];
        PlanCacheDatabaseIdentitySnapshot[] after = gap == "missing_after" ? [Snapshot(6, true)] : [last, Snapshot(6, true)];
        var result = PlanCacheRuntimeSourceAssembler.Bind(Source(true, Row(5, 'a'), Row(6, 'b')), before, after);
        Assert.False(result.Exhaustive); Assert.Equal(1, result.UnqualifiedStatementRows);
        Assert.Equal(6, Assert.Single(result.Statements).Watermark.Key.Query.DatabaseId);
    }

    [Theory]
    [InlineData("foreign_target")]
    [InlineData("foreign_revision")]
    [InlineData("duplicate")]
    [InlineData("system")]
    [InlineData("local_time")]
    [InlineData("backwards")]
    [InlineData("long_snapshot")]
    public void InvalidIdentityScopeFailsClosedEvenForAnEmptyCache(string invalid)
    {
        var first = Snapshot(5, false);
        first = invalid switch
        {
            "foreign_target" => first with { TargetId = new(Guid.NewGuid()) },
            "foreign_revision" => first with { TargetRevision = new(2) },
            "system" => first with { DatabaseId = 4 },
            "local_time" => first with { ReadStartedAtUtc = first.ReadStartedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            "backwards" => first with { ReadCompletedAtUtc = first.ReadStartedAtUtc.AddSeconds(-1) },
            "long_snapshot" => first with { ReadStartedAtUtc = At.AddSeconds(-6) },
            _ => first,
        };
        PlanCacheDatabaseIdentitySnapshot[] before = invalid == "duplicate" ? [first, first] : [first];
        Assert.Throws<InvalidDataException>(() => PlanCacheRuntimeSourceAssembler.Bind(Source(true), before, []));
    }

    [Fact]
    public void QualificationAgeHasAnExactBoundaryAndNeverFillsAbsentStatements()
    {
        var first = Snapshot(5, false) with { ReadStartedAtUtc = At.AddSeconds(-29.96), ReadCompletedAtUtc = At.AddSeconds(-28) };
        var last = Snapshot(5, true);
        Assert.Single(PlanCacheRuntimeSourceAssembler.Bind(Source(true, Row(5, 'a')), [first], [last]).Statements);
        Assert.Empty(PlanCacheRuntimeSourceAssembler.Bind(Source(true), [first], [last]).Statements);
        Assert.Throws<InvalidDataException>(() => PlanCacheRuntimeSourceAssembler.Bind(Source(true),
            Enumerable.Range(5, 257).Select(id => Snapshot(id, false)).ToArray(), []));
    }

    [Theory]
    [InlineData("known")]
    [InlineData("unknown")]
    [InlineData("ineligible")]
    public async Task IdentityDecoderKeepsUnknownEvidenceSeparate(string state)
    {
        using var reader = IdentityFrame(state == "known" ? Digest('d') : null, state != "ineligible").CreateDataReader();
        var snapshot = await PlanCacheDatabaseIdentitySource.DecodeAsync(reader, Target, Revision, CancellationToken.None);
        Assert.Equal(state == "known" ? Digest('d') : null, snapshot.DatabaseIncarnation);
        Assert.Equal(state != "ineligible", snapshot.CatalogEligible); Assert.Equal(5, snapshot.DatabaseId);
        _ = PlanCacheDatabaseIdentitySource.LoadEmbedded();
    }

    [Theory]
    [InlineData("format")]
    [InlineData("database")]
    [InlineData("backwards")]
    [InlineData("long_window")]
    [InlineData("ineligible_identity")]
    [InlineData("duplicate")]
    [InlineData("extra_result")]
    public async Task IdentityDecoderRejectsMalformedOrAmbiguousFrames(string invalid)
    {
        var table = IdentityFrame(Digest('d'), true);
        switch (invalid)
        {
            case "format": table.Rows[0][0] = 2; break;
            case "database": table.Rows[0][3] = 4; break;
            case "backwards": table.Rows[0][2] = At.AddSeconds(-1); break;
            case "long_window": table.Rows[0][2] = At.AddSeconds(6); break;
            case "ineligible_identity": table.Rows[0][6] = false; break;
            case "duplicate": table.Rows.Add(table.Rows[0].ItemArray); break;
        }
        using var reader = new DataTableReader(invalid == "extra_result" ? [table, table.Copy()] : [table]);
        await Assert.ThrowsAsync<InvalidDataException>(() => PlanCacheDatabaseIdentitySource.DecodeAsync(reader,
            Target, Revision, CancellationToken.None));
    }

    private static PlanCacheDatabaseIdentitySnapshot Snapshot(int id, bool after) => new(Target, Revision, id,
        after ? At.AddMilliseconds(30) : At.AddMilliseconds(-20),
        after ? At.AddMilliseconds(40) : At.AddMilliseconds(-10), Digest('c'), Digest('d'), true);

    private static PlanCacheRuntimeSourceRow Row(int id, char entry)
    {
        var query = new QueryOpaqueIdentity(id, Digest('a'));
        return new(query, new(query, Digest('b')), Digest(entry), new(1001, 2002, 3, 4, 5, 6));
    }

    private static PlanCacheRuntimeSourcePage Source(bool exhaustive, params PlanCacheRuntimeSourceRow[] rows) =>
        new(Target, Revision, At, At.AddMilliseconds(20), Digest('c'), rows.Length, 0, 0, 0, rows, exhaustive);

    private static DataTable IdentityFrame(string? incarnation, bool eligible)
    {
        var table = new DataTable();
        foreach (var type in new[] { typeof(int), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(int),
            typeof(string), typeof(string), typeof(bool) }) table.Columns.Add($"f{table.Columns.Count}", type);
        table.Rows.Add(1, At, At.AddMilliseconds(20), 5, Digest('c'), (object?)incarnation ?? DBNull.Value, eligible);
        return table;
    }
}
