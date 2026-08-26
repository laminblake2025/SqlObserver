using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>Read-only persisted source for explicit target-to-host bindings.</summary>
public interface IHostTargetStore
{
    ValueTask<HostTarget?> ReadAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken);
}

/// <summary>Resolves only an exact persisted binding/profile and target revision.</summary>
public sealed class PersistedHostTargetResolver : IHostTargetResolver
{
    private readonly IHostTargetStore _store;

    public PersistedHostTargetResolver(IHostTargetStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<HostTarget?> ResolveAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(revision);
        return _store.ReadAsync(targetId, revision, cancellationToken);
    }
}
