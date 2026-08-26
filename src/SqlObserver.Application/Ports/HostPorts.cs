using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class HostCollectionRequest
{
    public HostCollectionRequest(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        HostTargetBinding binding,
        HostObservationProfile profile,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(profile);
        if (binding.TargetId != targetId || profile.TargetId != targetId || profile.TargetRevision != targetRevision)
            throw new ArgumentException("Host collection requires an exact target/revision binding.", nameof(binding));
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        TargetId = targetId;
        TargetRevision = targetRevision;
        Binding = binding;
        Profile = profile;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public HostTargetBinding Binding { get; }
    public HostObservationProfile Profile { get; }
    public TimeSpan Timeout { get; }
}

/// <summary>Source-neutral host metrics port. Implementations must return only bounded, opaque output.</summary>
public interface IHostMetricsCollector
{
    ValueTask<HostCollectionResult> CollectAsync(HostCollectionRequest request, CancellationToken cancellationToken);
}
