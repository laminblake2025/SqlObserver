using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>Resolves a revision-fenced local host binding for a SQL target.</summary>
public interface IHostTargetResolver
{
    ValueTask<HostTarget?> ResolveAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken);
}

/// <summary>Adapts source-neutral host evidence to the collector envelope.</summary>
public sealed class HostMetricsCollectorAdapter : ICollector
{
    private readonly IHostMetricsCollector _collector;
    private readonly IHostTargetResolver _targets;
    private readonly IdentityFingerprintKey _fingerprintKey;

    public HostMetricsCollectorAdapter(IHostMetricsCollector collector, IHostTargetResolver targets, IdentityFingerprintKey fingerprintKey)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _fingerprintKey = fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey));
    }

    public CollectorManifest Manifest { get; } = HostMetricsManifest.Create();
    public static CollectorOutputContract OutputContract => new(
        new CollectorOutputSchemaVersion(1),
        [
            new CollectorMetricOutputContract(new MetricId("host.cpu.percent"), []),
            new CollectorMetricOutputContract(new MetricId("host.memory.available_bytes"), []),
            new CollectorMetricOutputContract(new MetricId("host.memory.committed_bytes"), []),
            new CollectorMetricOutputContract(new MetricId("host.volume.free_bytes"), ["volume"]),
            new CollectorMetricOutputContract(new MetricId("host.volume.total_bytes"), ["volume"]),
            new CollectorMetricOutputContract(new MetricId("host.volume.queue_length"), ["volume"]),
            new CollectorMetricOutputContract(new MetricId("host.volume.read_latency_ms"), ["volume"]),
            new CollectorMetricOutputContract(new MetricId("host.volume.write_latency_ms"), ["volume"]),
        ],
        maxMetricSamples: 3 + (HostMetricsV1.MaximumVolumes * 5),
        maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 0);

    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        HostTarget? target = await _targets.ResolveAsync(request.TargetId, request.TargetRevision, cancellationToken).ConfigureAwait(false);
        if (target is null || target.TargetId != request.TargetId || target.Binding.TargetId != request.TargetId ||
            target.TargetRevision != request.TargetRevision || target.Profile.TargetId != request.TargetId ||
            target.Profile.TargetRevision != request.TargetRevision || target.Binding.Revision != target.Profile.ProfileRevision ||
            target.Binding.State != HostBindingState.Bound || target.Profile.State != HostBindingState.Bound)
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        HostCollectionRequest hostRequest = new(request.TargetId, request.TargetRevision, target.Binding, target.Profile, request.Timeout.Value > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : request.Timeout.Value);
        HostCollectionResult result = await _collector.CollectAsync(hostRequest, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Metrics is null)
            return Failure(request, MapOutcome(result.Reason), MapReason(result.Reason));

        HostMetricsV1 metrics = result.Metrics;
        var samples = new List<MetricSample>(3 + metrics.Volumes.Count * 5)
        {
            new(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.cpu.percent"), metrics.ObservedAtUtc, metrics.CpuPercent),
            new(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.memory.available_bytes"), metrics.ObservedAtUtc, metrics.AvailableMemoryBytes),
            new(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.memory.committed_bytes"), metrics.ObservedAtUtc, metrics.CommittedMemoryBytes),
        };
        foreach (HostVolumeMetrics volume in metrics.Volumes)
        {
            var dimension = new[] { new MetricDimension("volume", volume.VolumeFingerprint) };
            samples.Add(new MetricSample(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.volume.free_bytes"), metrics.ObservedAtUtc, volume.FreeBytes, dimension));
            samples.Add(new MetricSample(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.volume.total_bytes"), metrics.ObservedAtUtc, volume.TotalBytes, dimension));
            samples.Add(new MetricSample(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.volume.queue_length"), metrics.ObservedAtUtc, volume.QueueLength, dimension));
            samples.Add(new MetricSample(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.volume.read_latency_ms"), metrics.ObservedAtUtc, volume.ReadLatencyMilliseconds, dimension));
            samples.Add(new MetricSample(new MetricSampleId(Guid.NewGuid()), request.TargetId, new MetricId("host.volume.write_latency_ms"), metrics.ObservedAtUtc, volume.WriteLatencyMilliseconds, dimension));
        }
        var payload = new CollectorPayload(samples, hostMetricsContext: new HostMetricsPayloadContext(metrics.HostFingerprint, target.Binding.Revision, target.Profile.ProfileRevision, _fingerprintKey));
        var accounting = new CollectorRunAccounting(metrics.Volumes.Count * 4 + 3, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        return new CollectorExecutionResult(request.TargetId, request.TargetRevision, Manifest.Id, 1, 1, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, payload, accounting, CollectorLossEvidence.None);
    }

    private static CollectorExecutionResult Failure(CollectorExecutionRequest request, CollectorRunOutcome outcome, CollectorRunReason reason) =>
        new(request.TargetId, request.TargetRevision, new CollectorId("host.metrics"), 1, 1, outcome, reason, CollectorPayload.Empty, new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);

    private static CollectorRunOutcome MapOutcome(HostObservationReason reason) => reason switch
    {
        HostObservationReason.Canceled => CollectorRunOutcome.TimedOut,
        HostObservationReason.TimedOut or HostObservationReason.TerminationUnproven => CollectorRunOutcome.TimedOut,
        HostObservationReason.PermissionDenied => CollectorRunOutcome.PermissionDenied,
        HostObservationReason.Unsupported or HostObservationReason.NotBound => CollectorRunOutcome.Unsupported,
        HostObservationReason.Unreachable or HostObservationReason.ProviderFailure => CollectorRunOutcome.TransientFailure,
        _ => CollectorRunOutcome.OutputInvalid,
    };

    private static CollectorRunReason MapReason(HostObservationReason reason) => reason switch
    {
        HostObservationReason.PermissionDenied => CollectorRunReason.RequiredPermissionMissing,
        HostObservationReason.Unsupported => CollectorRunReason.TargetUnsupported,
        HostObservationReason.NotBound => CollectorRunReason.CapabilityMissing,
        HostObservationReason.TimedOut or HostObservationReason.TerminationUnproven => CollectorRunReason.DeadlineExceeded,
        HostObservationReason.Unreachable or HostObservationReason.ProviderFailure => CollectorRunReason.TransientTargetFailure,
        _ => CollectorRunReason.OutputValidationFailed,
    };
}

public static class HostMetricsManifest
{
    public static CollectorManifest Create() => new(
        new CollectorId("host.metrics"), new CollectorDisplayName("Windows host metrics"), new CollectorManifestVersion(1),
        [new CapabilityId("platform.windows"), new CapabilityId("feature.host-binding")], [],
        new SqlServerMajorVersionRange(15, 17), [SqlServerPlatform.Windows], new CollectorIntervalPolicy(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 256, 256 * 1024, CollectorEstimatedCost.Moderate),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported), new CollectorOutputSchemaVersion(1), CollectorOperationalMode.Passive,
        [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express], [new CollectorId("engine.core")],
        new CollectorResiliencePolicy(1, TimeSpan.Zero, 3, TimeSpan.FromMinutes(5)), CollectorOutputKind.Metrics);
}

/// <summary>Default fail-closed host target resolver until control-plane bindings are provisioned.</summary>
public sealed class UnavailableHostTargetResolver : IHostTargetResolver
{
    public ValueTask<HostTarget?> ResolveAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<HostTarget?>(null);
    }
}

/// <summary>Default fail-closed reader; production adapters must be explicitly provisioned.</summary>
public sealed class UnavailableWindowsHostDataReader : IWindowsHostDataReader
{
    public ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new HostMetricsSourceException(HostObservationReason.Unsupported);
    }
}
