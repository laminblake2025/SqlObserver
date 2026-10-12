using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class PlanCacheRuntimeOutputTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly ObservationTargetRevision Revision = new(2);
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static string Digest(char value) => new(value, 64);

    [Fact]
    public void SelectedStatementsRemainUsableWhenCacheOrDatabaseCoverageIsPartial()
    {
        PlanCacheRuntimeStatementObservation[] rows = [Statement('a')];
        var capped = Batch(rows, candidateLimit: 1, candidateRows: 2, emittedRows: 2);
        rows[0] = Statement('b');
        Assert.Equal(Digest('a'), Assert.Single(capped.Items).Watermark.Key.CacheEntryFingerprint);
        Assert.False(capped.SourceExhaustive); Assert.False(capped.QualifiedExhaustive);
        var unqualified = Batch([Statement('a')], candidateRows: 2, emittedRows: 2, unqualifiedRows: 1);
        Assert.True(unqualified.SourceExhaustive); Assert.False(unqualified.QualifiedExhaustive);
        var unknown = Batch([], candidateRows: 3, emittedRows: 0, unresolvedRows: 1, invisibleRows: 1, excludedRows: 1);
        Assert.Empty(unknown.Items); Assert.False(unknown.SourceExhaustive);
        Assert.True(Batch([], candidateRows: 1, emittedRows: 0, excludedRows: 1).QualifiedExhaustive);
    }

    [Fact]
    public void RepositoryResolutionIsAppliedAfterRawBracketValidationWithoutRoundingCounters()
    {
        var source = At.AddTicks(9);
        var row = Statement('a', source);
        var batch = Batch([row], source: source);
        Assert.Equal(At, batch.ReadStartedUtc);
        Assert.Equal(At.AddMilliseconds(20), batch.ReadCompletedUtc);
        PlanCacheRuntimeStatementObservation normalized = Assert.Single(batch.Items);
        Assert.Equal(0, normalized.Before.ReadStartedUtc.Ticks % 10);
        Assert.Equal(0, normalized.After.ReadCompletedUtc.Ticks % 10);
        Assert.Equal(batch.ReadCompletedUtc, normalized.Watermark.ObservedAtUtc);
        Assert.Equal(new PlanCacheRuntimeCounters(1001, 2002, 3, 4, 5, long.MaxValue), normalized.Watermark.Counters);
        var overlapping = new PlanCacheRuntimeStatementObservation(row.Watermark,
            new(Target, Revision, 6, Digest('c'), Digest('d'), source.AddMilliseconds(-20), source.AddTicks(1)), row.After);
        Assert.Throws<ArgumentException>(() => Batch([overlapping], source: source));
    }

    [Theory]
    [InlineData("limit_zero")]
    [InlineData("limit_overflow")]
    [InlineData("candidate_overflow")]
    [InlineData("negative_unresolved")]
    [InlineData("negative_invisible")]
    [InlineData("negative_excluded")]
    [InlineData("negative_emitted")]
    [InlineData("negative_unqualified")]
    [InlineData("missing_row")]
    [InlineData("count_mismatch")]
    [InlineData("duplicate_entry")]
    [InlineData("null_row")]
    [InlineData("foreign_target")]
    [InlineData("foreign_revision")]
    [InlineData("foreign_epoch")]
    [InlineData("wrong_observation_time")]
    [InlineData("database_bracket_mismatch")]
    public void MalformedAccountingAndMixedSourceEvidenceFailClosed(string invalid)
    {
        var row = Statement('a');
        Assert.Throws<ArgumentException>(() =>
        {
            _ = invalid switch
            {
                "limit_zero" => Batch([row], candidateLimit: 0),
                "limit_overflow" => Batch([row], candidateLimit: 2001),
                "candidate_overflow" => Batch([row], candidateLimit: 1, candidateRows: 3, excludedRows: 2),
                "negative_unresolved" => Batch([row], unresolvedRows: -1),
                "negative_invisible" => Batch([row], invisibleRows: -1),
                "negative_excluded" => Batch([row], excludedRows: -1),
                "negative_emitted" => Batch([row], emittedRows: -1),
                "negative_unqualified" => Batch([row], unqualifiedRows: -1),
                "missing_row" => Batch([], candidateRows: 1, emittedRows: 1),
                "count_mismatch" => Batch([row], candidateRows: 2),
                "duplicate_entry" => Batch([row, row]),
                "null_row" => Batch([null!]),
                "foreign_target" => Batch([Statement('a', target: new(Guid.NewGuid()))]),
                "foreign_revision" => Batch([Statement('a', revision: new(1))]),
                "foreign_epoch" => Batch([Statement('a', epoch: Digest('e'))]),
                "wrong_observation_time" => Batch([Statement('a', At.AddTicks(1))]),
                "database_bracket_mismatch" => Batch([row, ChangedBracket()]),
                _ => throw new InvalidOperationException(),
            };
        });
    }

    [Theory]
    [InlineData("system_database")]
    [InlineData("local_time")]
    [InlineData("backwards")]
    [InlineData("long_window")]
    [InlineData("unknown_incarnation")]
    [InlineData("wrong_epoch")]
    [InlineData("wrong_incarnation")]
    [InlineData("after_overlap")]
    [InlineData("long_bracket")]
    public void IdentityEvidenceMustBeKnownBoundedAndMatchItsStatement(string invalid)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            var row = Statement('a');
            var before = new PlanCacheDatabaseIdentityEvidence(Target, Revision,
                invalid == "system_database" ? 4 : 6,
                invalid == "wrong_epoch" ? Digest('e') : Digest('c'),
                invalid == "unknown_incarnation" ? "" : invalid == "wrong_incarnation" ? Digest('e') : Digest('d'),
                invalid == "local_time" ? At.ToOffset(TimeSpan.FromHours(1)) :
                    invalid == "long_window" ? At.AddSeconds(-6) : At.AddMilliseconds(-20),
                invalid == "backwards" ? At.AddSeconds(-1) : At.AddMilliseconds(-10));
            var after = new PlanCacheDatabaseIdentityEvidence(Target, Revision, 6, Digest('c'), Digest('d'),
                invalid == "after_overlap" ? At : invalid == "long_bracket" ? At.AddSeconds(31) : At.AddMilliseconds(30),
                invalid == "long_bracket" ? At.AddSeconds(32) : At.AddMilliseconds(40));
            _ = new PlanCacheRuntimeStatementObservation(row.Watermark, before, after);
        });
    }

    private static PlanCacheRuntimeOutputBatch Batch(PlanCacheRuntimeStatementObservation[] rows,
        int candidateLimit = 2000, int? candidateRows = null, int unresolvedRows = 0, int invisibleRows = 0,
        int excludedRows = 0, int? emittedRows = null, int unqualifiedRows = 0, DateTimeOffset? source = null) =>
        new(Target, Revision, Digest('c'), source ?? At, (source ?? At).AddMilliseconds(20),
            candidateLimit, candidateRows ?? rows.Length, unresolvedRows, invisibleRows, excludedRows,
            emittedRows ?? rows.Length, unqualifiedRows, rows);

    private static PlanCacheRuntimeStatementObservation ChangedBracket()
    {
        var row = Statement('b');
        return new(row.Watermark, new(Target, Revision, 6, Digest('c'), Digest('d'),
            At.AddMilliseconds(-19), At.AddMilliseconds(-10)), row.After);
    }

    private static PlanCacheRuntimeStatementObservation Statement(char entry, DateTimeOffset? source = null,
        MonitoredInstanceId? target = null, ObservationTargetRevision? revision = null, string? epoch = null)
    {
        target ??= Target; revision ??= Revision; epoch ??= Digest('c');
        DateTimeOffset start = source ?? At;
        var query = new QueryOpaqueIdentity(6, Digest('a'));
        var watermark = new PlanCacheRuntimeWatermark(new(target, revision, query, new(query, Digest('b')),
            Digest('d'), epoch, Digest(entry)), new(1001, 2002, 3, 4, 5, long.MaxValue), start.AddMilliseconds(20));
        return new(watermark,
            new(target, revision, 6, epoch, Digest('d'), start.AddMilliseconds(-20), start.AddMilliseconds(-10)),
            new(target, revision, 6, epoch, Digest('d'), start.AddMilliseconds(30), start.AddMilliseconds(40)));
    }
}
