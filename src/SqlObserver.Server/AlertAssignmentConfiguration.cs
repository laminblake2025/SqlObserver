using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public sealed class AlertAssignmentAssigneeOptions
{
    public string Sid { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool AllTargets { get; init; }
    public Guid[] TargetIds { get; init; } = [];
}

/// <summary>Approved responders are configuration assertions, not directory queries or login grants.</summary>
public sealed class ConfiguredAlertAssignmentEligibility : IAlertAssignmentEligibilityPort
{
    private sealed record Entry(AlertAssignmentCandidate Candidate, bool Enabled, TargetAuthorizationScope Scope);
    private readonly IReadOnlyDictionary<string, Entry> entries;

    public ConfiguredAlertAssignmentEligibility(IReadOnlyList<AlertAssignmentAssigneeOptions> assignees,
        IReadOnlyList<string>? disabledActorSids = null)
    {
        ArgumentNullException.ThrowIfNull(assignees);
        if (assignees.Count > AlertAssignmentContract.MaximumAssignees)
            throw new ArgumentException("At most 256 approved responders may be configured.", nameof(assignees));
        disabledActorSids ??= [];
        if (disabledActorSids.Count > WindowsGroupRoleResolver.MaximumDisabledPrincipals)
            throw new ArgumentException("The disabled-principal list exceeds its bound.", nameof(disabledActorSids));
        var disabled = disabledActorSids.Select(static sid => new ActorSecurityIdentifier(sid).Value).ToHashSet(StringComparer.Ordinal);
        var validated = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (AlertAssignmentAssigneeOptions options in assignees)
        {
            ArgumentNullException.ThrowIfNull(options);
            var sid = new ActorSecurityIdentifier(options.Sid);
            string label = AlertAssignmentContract.ValidateDisplayName(options.DisplayName);
            ArgumentNullException.ThrowIfNull(options.TargetIds);
            if (options.AllTargets && options.TargetIds.Length != 0)
                throw new ArgumentException("An all-target responder cannot enumerate target IDs.", nameof(assignees));
            TargetAuthorizationScope scope = options.AllTargets ? TargetAuthorizationScope.ForAllTargets() :
                TargetAuthorizationScope.ForTargets(options.TargetIds.Select(static id => new MonitoredInstanceId(id)).ToArray());
            if (!validated.TryAdd(sid.Value, new Entry(new AlertAssignmentCandidate(sid.Value, label),
                    options.Enabled && !disabled.Contains(sid.Value), scope)))
                throw new ArgumentException("Approved responder SIDs must be unique.", nameof(assignees));
        }
        entries = validated;
    }

    public static ConfiguredAlertAssignmentEligibility Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ConfiguredAlertAssignmentEligibility(
            configuration.GetSection("AlertAssignment:Assignees").Get<AlertAssignmentAssigneeOptions[]>() ?? [],
            configuration.GetSection("SqlObserver:Authorization:DisabledActorSids").Get<string[]>() ?? []);
    }

    public IReadOnlyList<AlertAssignmentCandidate> ListEligible(MonitoredInstanceId targetId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        return entries.Values.Where(entry => entry.Enabled && entry.Scope.Contains(targetId))
            .Select(static entry => entry.Candidate).OrderBy(static candidate => candidate.DisplayName, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Sid, StringComparer.Ordinal).ToArray();
    }
    public AlertAssignmentCandidate? ResolveApproved(MonitoredInstanceId targetId, ActorSecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(sid);
        return entries.TryGetValue(sid.Value, out Entry? entry) && entry.Enabled && entry.Scope.Contains(targetId) ? entry.Candidate : null;
    }
}
