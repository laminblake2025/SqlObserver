using Microsoft.Extensions.Configuration;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Collector;

/// <summary>Optional local configuration adapter for explicit lab bindings.</summary>
internal sealed class ConfigurationReplicationDistributionBindingResolver(IConfiguration configuration) : IReplicationDistributionBindingResolver
{
    private readonly IConfiguration configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public ValueTask<ReplicationDistributionBinding?> ResolveAsync(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        cancellationToken.ThrowIfCancellationRequested();
        // Configuration is keyed by the exact target/revision. There is no
        // global default and no endpoint-derived fallback.
        IConfigurationSection section = configuration.GetSection($"SqlObserver:ReplicationBindings:{targetId.Value:D}:{targetRevision.Value}");
        string? database = section["DistributionDatabase"];
        if (string.IsNullOrWhiteSpace(database)) return ValueTask.FromResult<ReplicationDistributionBinding?>(null);
        int? port = int.TryParse(section["DistributionPort"], out int parsed) ? parsed : null;
        return ValueTask.FromResult<ReplicationDistributionBinding?>(new ReplicationDistributionBinding(database, port));
    }
}
