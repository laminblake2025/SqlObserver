using Microsoft.Extensions.Configuration;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.Collector;

/// <summary>Loads only explicit persisted host binding/profile entries.</summary>
internal sealed class ConfigurationHostTargetStore(IConfiguration configuration) : IHostTargetStore
{
    private readonly Dictionary<(Guid TargetId, long Revision), HostTarget> _targets = Load(configuration);

    public ValueTask<HostTarget?> ReadAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_targets.TryGetValue((targetId.Value, revision.Value), out HostTarget? target) ? target : null);
    }

    private static Dictionary<(Guid, long), HostTarget> Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var values = new Dictionary<(Guid, long), HostTarget>();
        foreach (IConfigurationSection section in configuration.GetSection("SqlObserver:HostBindings").GetChildren())
        {
            if (!Guid.TryParse(section["TargetId"], out Guid targetId) ||
                !long.TryParse(section["TargetRevision"], out long targetRevision) || targetRevision <= 0 ||
                !long.TryParse(section["BindingRevision"], out long bindingRevision) || bindingRevision <= 0 ||
                !long.TryParse(section["ProfileRevision"], out long profileRevision) || profileRevision <= 0 ||
                !Enum.TryParse(section["BindingState"], true, out HostBindingState bindingState) ||
                !Enum.TryParse(section["ProfileState"], true, out HostBindingState profileState) ||
                !Enum.TryParse(section["Capabilities"], true, out HostMetricCapability capabilities) ||
                !Enum.IsDefined(bindingState) || !Enum.IsDefined(profileState) ||
                !HostIdentityFingerprint.TryParse(section["HostFingerprint"], out HostIdentityFingerprint? fingerprint)) continue;
            var target = new MonitoredInstanceId(targetId);
            var revision = new ObservationTargetRevision(targetRevision);
            var binding = new HostTargetBinding(target, fingerprint!, new HostObservationRevision(bindingRevision), bindingState);
            var profile = new HostObservationProfile(target, revision, new HostObservationRevision(profileRevision), capabilities, profileState);
            try { values.Add((targetId, targetRevision), new HostTarget(binding, profile, revision)); }
            catch (ArgumentException) { }
        }
        return values;
    }
}
