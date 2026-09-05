using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class M10HostObservationTests
{
    private static readonly IdentityFingerprintKey Key = new(Enumerable.Repeat((byte)0xA5, IdentityFingerprintKey.RequiredLength).ToArray());

    [Fact]
    public async Task FixedMetricQueriesStartTogetherWithinTheOwnedOperation()
    {
        var reader = new CohortReader();
        var source = new WindowsHostMetricSource(reader, Key);
        HostIdentityFingerprint fingerprint = HostIdentityFingerprint.FromOpaqueIdentity("cohort-host", Key);
        HostMetricsV1 metrics = await source.ReadAsync(fingerprint, DateTimeOffset.UtcNow, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WindowsHostQuery.AllowList.Count, reader.Started);
        Assert.Equal(fingerprint, metrics.HostFingerprint);
        Assert.Single(metrics.Volumes);
    }

    private sealed class CohortReader : IWindowsHostDataReader
    {
        private readonly TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;
        internal int Started => started;
        public async ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == WindowsHostQuery.AllowList.Count) allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(token);
            return await new GoodReader().ReadAsync(query, token);
        }
    }

    [Fact]
    public void CollectorServerAndPostgreSqlUseTheSameConfiguredIdentityContract()
    {
        string configured = Convert.ToHexString(KeyBytes());
        var collectorProvider = new ConfigurationIdentityFingerprintKeyProvider(() => configured);
        var serverProvider = new ConfigurationIdentityFingerprintKeyProvider(() => configured);
        var postgresProvider = new ConfigurationIdentityFingerprintKeyProvider(() => configured);

        HostIdentityFingerprint collectorFingerprint = HostIdentityFingerprint.FromOpaqueIdentity(
            "MACHINE\\alpha|C:\\secret",
            collectorProvider.GetRequiredKey());
        HostIdentityFingerprint serverFingerprint = HostIdentityFingerprint.FromOpaqueIdentity(
            "MACHINE\\alpha|C:\\secret",
            serverProvider.GetRequiredKey());
        HostIdentityFingerprint postgresFingerprint = HostIdentityFingerprint.FromOpaqueIdentity(
            "MACHINE\\alpha|C:\\secret",
            postgresProvider.GetRequiredKey());

        Assert.Equal(collectorFingerprint, serverFingerprint);
        Assert.Equal(serverFingerprint, postgresFingerprint);
        Assert.Equal(
            collectorFingerprint.ToStableHostId(collectorProvider.GetRequiredKey()),
            postgresFingerprint.ToStableHostId(postgresProvider.GetRequiredKey()));

        var differentKey = new IdentityFingerprintKey(Enumerable.Repeat((byte)0x5A, IdentityFingerprintKey.RequiredLength).ToArray());
        Assert.NotEqual(collectorFingerprint, HostIdentityFingerprint.FromOpaqueIdentity("MACHINE\\alpha|C:\\secret", differentKey));
        Assert.NotEqual(
            collectorFingerprint.ToStableHostId(collectorProvider.GetRequiredKey()),
            collectorFingerprint.ToStableHostId(differentKey));

        // A fresh provider instance models process restart: the same config
        // must produce the same opaque identity, while absent config fails
        // closed before any collector/server/PG composition is usable.
        var restartedProvider = new ConfigurationIdentityFingerprintKeyProvider(() => configured);
        Assert.Equal(
            collectorFingerprint,
            HostIdentityFingerprint.FromOpaqueIdentity("MACHINE\\alpha|C:\\secret", restartedProvider.GetRequiredKey()));
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationIdentityFingerprintKeyProvider(() => null).GetRequiredKey());
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationIdentityFingerprintKeyProvider(() => "not-a-32-byte-key").GetRequiredKey());

        static byte[] KeyBytes() => Enumerable.Repeat((byte)0xA5, IdentityFingerprintKey.RequiredLength).ToArray();
    }

    [Fact]
    public void HostAndVolumeIdentityFingerprintsAreStableAndOpaque()
    {
        HostIdentityFingerprint first = HostIdentityFingerprint.FromOpaqueIdentity("MACHINE\\alpha|C:\\secret", Key);
        HostIdentityFingerprint second = HostIdentityFingerprint.FromOpaqueIdentity("MACHINE\\alpha|C:\\secret", Key);
        Assert.Equal(first, second);
        Assert.DoesNotContain("MACHINE", first.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", first.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindingSetCannotCrossTargetBoundaries()
    {
        var firstTarget = new MonitoredInstanceId(Guid.NewGuid());
        var secondTarget = new MonitoredInstanceId(Guid.NewGuid());
        var first = new HostTargetBinding(firstTarget, HostIdentityFingerprint.FromOpaqueIdentity("one", Key), new HostObservationRevision(1));
        var second = new HostTargetBinding(secondTarget, HostIdentityFingerprint.FromOpaqueIdentity("two", Key), new HostObservationRevision(1));
        var set = new HostTargetBindingSet([first, second]);
        Assert.Equal(first, set.Require(firstTarget));
        Assert.NotEqual(set.Require(firstTarget).HostFingerprint, set.Require(secondTarget).HostFingerprint);
        Assert.Throws<InvalidOperationException>(() => new HostTargetBindingSet([first]).Require(secondTarget));
    }

    [Fact]
    public async Task DeniedUnreachableTimeoutAndUnsupportedStatesProduceNoPayload()
    {
        MonitoredInstanceId target = new(Guid.NewGuid());
        ObservationTargetRevision revision = new(1);
        HostIdentityFingerprint identity = HostIdentityFingerprint.FromOpaqueIdentity("host", Key);
        foreach (HostBindingState state in Enum.GetValues<HostBindingState>().Where(static s => s != HostBindingState.Bound))
        {
            var binding = new HostTargetBinding(target, identity, new HostObservationRevision(1), state);
            var profile = new HostObservationProfile(target, revision, new HostObservationRevision(1), HostMetricCapability.Cpu | HostMetricCapability.Memory | HostMetricCapability.LogicalVolume | HostMetricCapability.DiskLatency, state);
            var request = new HostCollectionRequest(target, revision, binding, profile, TimeSpan.FromSeconds(5));
            var collector = new HostMetricsCollector(new WindowsHostMetricSource(new ThrowingReader(), Key));
            HostCollectionResult result = await collector.CollectAsync(request, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Null(result.Metrics);
        }
    }

    [Fact]
    public async Task SourceEmitsRequiredMetricsAndRejectsOversizedRows()
    {
        HostIdentityFingerprint identity = HostIdentityFingerprint.FromOpaqueIdentity("host", Key);
        var source = new WindowsHostMetricSource(new GoodReader(), Key);
        HostMetricsV1 output = await source.ReadAsync(identity, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(HostMetricsV1.SchemaVersion, output.Schema);
        Assert.Single(output.Volumes);
        Assert.True(new HostMetricsV1Validator().IsValid(output));

        HostMetricsSourceException exception = await Assert.ThrowsAsync<HostMetricsSourceException>(async () =>
            await new WindowsHostMetricSource(new OversizedReader(), Key).ReadAsync(identity, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(HostObservationReason.InvalidOutput, exception.Reason);
    }

    [Fact]
    public async Task CancellationDoesNotLeaveReaderOperationRunning()
    {
        var reader = new CancellingReader();
        MonitoredInstanceId target = new(Guid.NewGuid());
        ObservationTargetRevision revision = new(1);
        HostIdentityFingerprint identity = HostIdentityFingerprint.FromOpaqueIdentity("host", Key);
        var binding = new HostTargetBinding(target, identity, new HostObservationRevision(1));
        var profile = new HostObservationProfile(target, revision, new HostObservationRevision(1), HostMetricCapability.Cpu | HostMetricCapability.Memory | HostMetricCapability.LogicalVolume | HostMetricCapability.DiskLatency);
        var request = new HostCollectionRequest(target, revision, binding, profile, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        HostCollectionResult result = await new HostMetricsCollector(new WindowsHostMetricSource(reader, Key)).CollectAsync(request, cancellation.Token);
        Assert.False(result.Succeeded);
        Assert.True(reader.CancellationObserved);
    }

    [Fact]
    public async Task ExplicitQuerySessionIsHardStoppedAndDisposedBeforeOperationCompletes()
    {
        var factory = new CompletingOnTerminateFactory();
        var source = new WindowsHostMetricSource(factory, Key);
        await using (IWindowsHostMetricsOperation operation = source.StartOperation(
            HostIdentityFingerprint.FromOpaqueIdentity("host", Key), DateTimeOffset.UtcNow, CancellationToken.None))
        {
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            operation.Cancel();
            // This checks completion and disposal ordering, not thread-pool scheduling latency.
            Assert.True(await operation.TerminateAsync(TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
        }

        Assert.True(factory.Session.Disposed);
        Assert.True(factory.Session.TerminateCalled);
    }

    [Fact]
    public async Task FailedTerminationWaitsForExitQuarantinesTargetAndDoesNotRetry()
    {
        var factory = new FailingTerminationFactory(TimeSpan.FromMilliseconds(75));
        var source = new WindowsHostMetricSource(factory, Key);
        var collector = new HostMetricsCollector(source, new HostCollectionOptions(terminationGrace: TimeSpan.FromMilliseconds(10)));
        MonitoredInstanceId target = new(Guid.NewGuid());
        ObservationTargetRevision revision = new(1);
        var binding = new HostTargetBinding(target, HostIdentityFingerprint.FromOpaqueIdentity("host", Key), new HostObservationRevision(1));
        var profile = new HostObservationProfile(target, revision, new HostObservationRevision(1), HostMetricCapability.Cpu | HostMetricCapability.Memory | HostMetricCapability.LogicalVolume | HostMetricCapability.DiskLatency);
        var request = new HostCollectionRequest(target, revision, binding, profile, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        Task<HostCollectionResult> pending = collector.CollectAsync(request, cancellation.Token).AsTask();
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        HostCollectionResult result = await pending;

        Assert.Equal(HostObservationReason.TerminationUnproven, result.Reason);
        Assert.True(result.CircuitOpen);
        Assert.True(factory.Session.Disposed);
        Assert.True(factory.Session.TerminateCalled);
        HostCollectionResult quarantined = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(HostObservationReason.CircuitOpen, quarantined.Reason);
        Assert.Equal(1, factory.StartCount);
    }

    [Fact]
    public void SerializedOutputContainsNoHostOrVolumeText()
    {
        string hostileHost = "corp\\host.example|password=do-not-store";
        string hostileVolume = "C:\\Program Files\\secret-label";
        HostIdentityFingerprint identity = HostIdentityFingerprint.FromOpaqueIdentity(hostileHost, Key);
        var output = new HostMetricsV1(identity, DateTimeOffset.UtcNow, 10, 100, 200, [new HostVolumeMetrics(HostIdentityFingerprint.FromOpaqueIdentity(hostileVolume, Key).Value, 1, 2, 0, 1, 2)]);
        string json = JsonSerializer.Serialize(output);
        Assert.DoesNotContain(hostileHost, json, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileVolume, json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingReader : IWindowsHostDataReader
    {
        public ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException("must not be called");
    }

    private sealed class GoodReader : IWindowsHostDataReader
    {
        public ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<WindowsHostDataRow>>(query.Kind switch
        {
            WindowsHostQueryKind.CpuUtilization => [new(null, 10)],
            WindowsHostQueryKind.AvailableMemoryBytes => [new(null, 100)],
            WindowsHostQueryKind.CommittedMemoryBytes => [new(null, 200)],
            WindowsHostQueryKind.LogicalVolumeSpace => [new("C:", 0, 1, 2)],
            WindowsHostQueryKind.DiskQueueLength => [new("C:", 1)],
            WindowsHostQueryKind.DiskReadLatencyMilliseconds => [new("C:", 2)],
            WindowsHostQueryKind.DiskWriteLatencyMilliseconds => [new("C:", 3)],
            _ => throw new InvalidOperationException(),
        });
    }
    private sealed class OversizedReader : IWindowsHostDataReader
    {
        public ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<WindowsHostDataRow>>(Enumerable.Range(0, 257).Select(static _ => new WindowsHostDataRow(null, 1)).ToArray());
    }
    private sealed class CancellingReader : IWindowsHostDataReader
    {
        public bool CancellationObserved { get; private set; }
        public async ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            return [];
        }
    }

    private sealed class CompletingOnTerminateFactory : IWindowsHostQuerySessionFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CompletingOnTerminateSession Session { get; private set; } = null!;
        public IWindowsHostQuerySession Start(WindowsHostQuery query, CancellationToken cancellationToken)
        {
            Session = new CompletingOnTerminateSession(Started);
            return Session;
        }
    }

    private sealed class CompletingOnTerminateSession : IWindowsHostQuerySession
    {
        private readonly TaskCompletionSource<IReadOnlyList<WindowsHostDataRow>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CompletingOnTerminateSession(TaskCompletionSource started) => started.TrySetResult();
        public bool Disposed { get; private set; }
        public bool TerminateCalled { get; private set; }
        public Task<IReadOnlyList<WindowsHostDataRow>> Completion => completion.Task;
        public void Cancel() { }
        public ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            TerminateCalled = true;
            completion.TrySetResult([]);
            return ValueTask.FromResult(true);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FailingTerminationFactory(TimeSpan completionDelay) : IWindowsHostQuerySessionFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FailingTerminationSession Session { get; private set; } = null!;
        public int StartCount { get; private set; }
        public IWindowsHostQuerySession Start(WindowsHostQuery query, CancellationToken cancellationToken)
        {
            StartCount++;
            Session = new FailingTerminationSession(Started, completionDelay);
            return Session;
        }
    }

    private sealed class FailingTerminationSession : IWindowsHostQuerySession
    {
        private readonly TaskCompletionSource<IReadOnlyList<WindowsHostDataRow>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TimeSpan completionDelay;
        public FailingTerminationSession(TaskCompletionSource started, TimeSpan completionDelay)
        {
            this.completionDelay = completionDelay;
            started.TrySetResult();
            _ = CompleteLaterAsync();
        }
        public bool Disposed { get; private set; }
        public bool TerminateCalled { get; private set; }
        public Task<IReadOnlyList<WindowsHostDataRow>> Completion => completion.Task;
        public void Cancel() { }
        public ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            TerminateCalled = true;
            return ValueTask.FromResult(false);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private async Task CompleteLaterAsync()
        {
            await Task.Delay(completionDelay).ConfigureAwait(false);
            completion.TrySetResult([]);
        }
    }
}
