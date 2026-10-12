using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class DeadlockReadWindowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly XeReadIdentity Source = new(new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")), new(1), new string('a', 64));

    [Fact]
    public void QuietIncrementalReadRetainsRecentDeadlocksRatherThanPublishingZero()
    {
        DeadlockWindowEvent item = Event(At.AddMinutes(-1));
        DeadlockReadWindow first = Cold([item]);
        DeadlockReadWindow quiet = Next(first, At.AddSeconds(30), []);
        Assert.Equal(1, first.DeadlockCount);
        Assert.Equal(1, quiet.DeadlockCount);
        Assert.Equal(item, Assert.Single(quiet.Events));
    }

    [Fact]
    public void BoundaryEventExpiresOnlyAfterItLeavesTheFiveMinuteWindow()
    {
        DeadlockWindowEvent item = Event(At);
        DeadlockReadWindow initial = Cold([item]);
        DeadlockReadWindow boundary = Next(initial, At.AddMinutes(5), []);
        Assert.Equal(1, boundary.DeadlockCount);
        DeadlockReadWindow expired = Next(boundary, At.AddMinutes(5).AddTicks(1), []);
        Assert.Equal(0, expired.DeadlockCount);
        Assert.Empty(expired.Events);
    }

    [Fact]
    public void RetryingAnEventDoesNotInflateTheRollingCount()
    {
        DeadlockWindowEvent item = Event(At.AddSeconds(-1));
        DeadlockReadWindow first = Cold([item, item]);
        DeadlockReadWindow replay = Next(first, At.AddSeconds(30), [item]);
        Assert.Equal(1, replay.DeadlockCount);
        Assert.Single(replay.Events);
    }

    [Fact]
    public void CatchupPreservesHistoryButCannotAssertTheCountUntilTheSourceEnds()
    {
        DeadlockWindowEvent earlier = Event(At.AddMinutes(-1)), later = Event(At.AddSeconds(10));
        DeadlockReadWindow initial = Cold([earlier]);
        DeadlockReadWindow partial = Next(initial, At.AddSeconds(30), [later], reachedEnd: false);
        Assert.Equal(DeadlockReadWindowReason.CatchingUp, partial.Reason);
        Assert.Null(partial.DeadlockCount);
        Assert.Equal(2, partial.Events.Count);
        DeadlockReadWindow complete = Next(partial, At.AddSeconds(60), []);
        Assert.Equal(2, complete.DeadlockCount);
    }

    [Fact]
    public void NewSessionWithoutFiveMinutesOfCoverageRemainsUnknownUntilItsWindowIsCovered()
    {
        DeadlockReadWindow shortCoverage = DeadlockReadWindowCalculator.Merge(null, Source, At, At.AddMinutes(-1), [], true);
        Assert.Equal(DeadlockReadWindowReason.CoverageGap, shortCoverage.Reason);
        Assert.Null(shortCoverage.DeadlockCount);
        DeadlockReadWindow covered = Next(shortCoverage, At.AddMinutes(4), []);
        Assert.Equal(0, covered.DeadlockCount);
    }

    [Fact]
    public void PermissionOrParseLossCannotResolveAnAlertThroughAQuietRead()
    {
        DeadlockReadWindow first = Cold([Event(At.AddMinutes(-1))]);
        DeadlockReadWindow gap = Next(first, At.AddSeconds(30), [], sourceLoss: true);
        Assert.Equal(DeadlockReadWindowReason.SourceIncomplete, gap.Reason);
        Assert.Null(gap.DeadlockCount);
        DeadlockReadWindow quiet = Next(gap, At.AddMinutes(1), []);
        Assert.Equal(DeadlockReadWindowReason.CoverageGap, quiet.Reason);
        Assert.Null(quiet.DeadlockCount);
        DeadlockReadWindow boundary = Next(quiet, gap.ReadAtUtc.AddMinutes(5), []);
        Assert.Null(boundary.DeadlockCount);
        DeadlockReadWindow healed = Next(boundary, boundary.ReadAtUtc.AddTicks(1), []);
        Assert.Equal(0, healed.DeadlockCount);
    }

    [Fact]
    public void TruncatedParserOutputCreatesAVisibleCoverageGap()
    {
        DeadlockReadWindow gap = Cold([new(Guid.NewGuid(), At.AddSeconds(-10), true)]);
        Assert.Equal(DeadlockReadWindowReason.SourceIncomplete, gap.Reason);
        Assert.Null(gap.DeadlockCount);
    }

    [Fact]
    public void RollingOverflowStaysBoundedAndUnknownUntilDiscardedEventsAgeOut()
    {
        DeadlockWindowEvent[] full = Enumerable.Range(0, 256).Select(_ => Event(At)).ToArray();
        DeadlockReadWindow first = Cold(full);
        Assert.Equal(256, first.DeadlockCount);
        DeadlockReadWindow overflow = Next(first, At.AddSeconds(1), [Event(At.AddSeconds(1))]);
        Assert.Equal(DeadlockReadWindowReason.WindowOverflow, overflow.Reason);
        Assert.Null(overflow.DeadlockCount);
        Assert.Equal(256, overflow.Events.Count);
        Assert.Null(Next(overflow, At.AddSeconds(30), []).DeadlockCount);
        DeadlockReadWindow healed = Next(overflow, overflow.ReadAtUtc.AddMinutes(5).AddTicks(1), []);
        Assert.Equal(0, healed.DeadlockCount);
        Assert.Empty(healed.Events);
    }

    [Fact]
    public void OldBootstrapHistoryDoesNotCountAsRecentDeadlocks()
    {
        DeadlockReadWindow window = Cold([Event(At.AddDays(-1))]);
        Assert.Equal(0, window.DeadlockCount);
        Assert.Empty(window.Events);
    }

    [Fact]
    public void SourceRestartAndTargetRevisionCannotInheritAnotherWindow()
    {
        DeadlockReadWindow first = Cold([Event(At)]);
        XeReadIdentity[] changes = [new(Source.TargetId, new(2), Source.SourceEpoch),
            new(Source.TargetId, Source.TargetRevision, new string('b', 64))];
        foreach (XeReadIdentity changed in changes)
        {
            Assert.Throws<ArgumentException>(() => DeadlockReadWindowCalculator.Merge(first, changed, At.AddSeconds(1), null, [], true));
            DeadlockReadWindow reset = DeadlockReadWindowCalculator.Merge(first, changed, At.AddSeconds(1), At, [], true);
            Assert.Empty(reset.Events);
            Assert.Null(reset.DeadlockCount);
        }
        Assert.Throws<ArgumentException>(() => DeadlockReadWindowCalculator.Merge(first,
            new(new(Guid.NewGuid()), new(1), Source.SourceEpoch), At.AddSeconds(1), At, [], true));
    }

    [Fact]
    public void ConflictingOrFutureEvidenceCannotInventAKnownCount()
    {
        DeadlockWindowEvent item = Event(At);
        DeadlockReadWindow first = Cold([item]);
        Assert.Throws<ArgumentException>(() => Next(first, At.AddSeconds(1), [new(item.EventId, At.AddSeconds(-1))]));
        Assert.Throws<ArgumentException>(() => Cold([Event(At.AddSeconds(1))]));
        Assert.Throws<ArgumentException>(() => Next(first, At, []));
        Assert.Throws<ArgumentException>(() => new DeadlockWindowEvent(Guid.Empty, At));
        Assert.Throws<ArgumentException>(() => new DeadlockWindowEvent(Guid.NewGuid(), At.ToOffset(TimeSpan.FromHours(1))));
    }

    private static DeadlockWindowEvent Event(DateTimeOffset occurred) => new(Guid.NewGuid(), occurred);
    private static DeadlockReadWindow Cold(IReadOnlyList<DeadlockWindowEvent> events) =>
        DeadlockReadWindowCalculator.Merge(null, Source, At, At.AddDays(-1), events, true);
    private static DeadlockReadWindow Next(DeadlockReadWindow previous, DateTimeOffset at,
        IReadOnlyList<DeadlockWindowEvent> events, bool reachedEnd = true, bool sourceLoss = false) =>
        DeadlockReadWindowCalculator.Merge(previous, Source, at, null, events, reachedEnd, sourceLoss);
}
