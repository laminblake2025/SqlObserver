using System.Diagnostics;
using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;

namespace SqlObserver.PerformanceTests;

public sealed class M6DeadlockBoundaryPerformanceTests
{
    [Fact]
    public void MaximumDeadlockBatchAndWindowRemainBounded()
    {
        var target = new MonitoredInstanceId(Guid.Parse("12121212-1212-1212-1212-121212121212"));
        var revision = new ObservationTargetRevision(1);
        DateTimeOffset occurred = new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
        var stopwatch = Stopwatch.StartNew();
        var values = Enumerable.Range(1, DeadlockObservationBatch.MaximumItems).Select(index => new DeadlockObservation(target, revision, new DeadlockFingerprint(index.ToString("x64", CultureInfo.InvariantCulture)), occurred.AddTicks(index), [], [])).ToArray();
        var batch = new DeadlockObservationBatch(values);
        stopwatch.Stop();
        Assert.Equal(DeadlockObservationBatch.MaximumItems, batch.Items.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"bounded deadlock batch construction took {stopwatch.Elapsed}");
        Assert.Throws<ArgumentException>(() => new DeadlockObservationBatch(values.Concat([values[^1]]).ToArray()));
        Assert.Throws<ArgumentException>(() => new SqlObserver.Application.Ports.ListDeadlocksRepositoryRequest(target, occurred, occurred.AddDays(31).AddTicks(1), 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))));
    }
}
