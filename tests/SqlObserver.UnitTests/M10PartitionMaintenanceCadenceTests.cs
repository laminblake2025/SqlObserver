using SqlObserver.Collector;

namespace SqlObserver.UnitTests;

public sealed class M10PartitionMaintenanceCadenceTests
{
    [Fact]
    public async Task StartupRunsThenNormalCyclesAreSkippedAndSixHourRepeatIgnoresUtcDayBoundary()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 9, 23, 59, 0, TimeSpan.Zero));
        var coordinator = new M10PartitionMaintenanceCoordinator(clock);
        int calls = 0;

        M10MaintenanceAttempt first = await coordinator.TryRunAsync(_ =>
        {
            calls++;
            return ValueTask.FromResult(177);
        }, CancellationToken.None);
        Assert.Equal(M10MaintenanceAttemptState.Succeeded, first.State);
        Assert.Equal(177, first.CheckedCount);
        Assert.Equal(1, calls);

        Assert.Equal(M10MaintenanceAttemptState.Skipped,
            (await coordinator.TryRunAsync(_ => ValueTask.FromResult(177), CancellationToken.None)).State);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(M10MaintenanceAttemptState.Skipped,
            (await coordinator.TryRunAsync(_ => ValueTask.FromResult(177), CancellationToken.None)).State);
        clock.Advance(TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(58)));
        M10MaintenanceAttempt repeat = await coordinator.TryRunAsync(_ =>
        {
            calls++;
            return ValueTask.FromResult(177);
        }, CancellationToken.None);

        Assert.Equal(M10MaintenanceAttemptState.Succeeded, repeat.State);
        Assert.Equal(2, calls);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 5, 59, 0, TimeSpan.Zero), clock.GetUtcNow());
    }

    [Fact]
    public async Task FailureRetriesAfterThirtySecondsWithoutBeingMarkedSuccessful()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var coordinator = new M10PartitionMaintenanceCoordinator(clock);
        int calls = 0;

        M10MaintenanceAttempt failure = await coordinator.TryRunAsync(_ =>
        {
            calls++;
            throw new InvalidOperationException("catalog unavailable");
        }, CancellationToken.None);
        Assert.Equal(M10MaintenanceAttemptState.Failed, failure.State);
        Assert.Equal(nameof(InvalidOperationException), failure.FailureType);
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(M10MaintenanceAttemptState.Skipped,
            (await coordinator.TryRunAsync(_ => ValueTask.FromResult(177), CancellationToken.None)).State);
        clock.Advance(TimeSpan.FromSeconds(1));
        M10MaintenanceAttempt retry = await coordinator.TryRunAsync(_ =>
        {
            calls++;
            return ValueTask.FromResult(177);
        }, CancellationToken.None);

        Assert.Equal(M10MaintenanceAttemptState.Succeeded, retry.State);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndDoesNotBecomeAHealthySuccess()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new M10PartitionMaintenanceCoordinator(clock);
        M10MaintenanceAttempt success = await coordinator.TryRunAsync(
            _ => ValueTask.FromResult(177),
            CancellationToken.None);
        Assert.Equal(M10MaintenanceAttemptState.Succeeded, success.State);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.TryRunAsync(
                _ => ValueTask.FromResult(177),
                cancellation.Token)
            .AsTask());

        M10MaintenanceAttempt afterCancellation = await coordinator.TryRunAsync(
            _ => ValueTask.FromResult(177),
            CancellationToken.None);
        Assert.Equal(M10MaintenanceAttemptState.Skipped, afterCancellation.State);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow.ToUniversalTime();
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
            _utcNow = _utcNow.Add(duration);
        }
    }
}
