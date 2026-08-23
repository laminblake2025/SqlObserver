using System.Collections.ObjectModel;
using System.Security.Claims;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Security;

/// <summary>A bounded administrator-owned mapping from one Windows group SID to application access.</summary>
public sealed class WindowsGroupRoleBinding
{
    public const int MaximumRoles = AuthorizationContext.MaximumRoleCount;
    public const int MaximumTargets = TargetAuthorizationScope.MaximumTargetCount;

    private readonly ReadOnlyCollection<ApplicationRole> _roles;
    private readonly ReadOnlyCollection<MonitoredInstanceId> _targetIds;

    public WindowsGroupRoleBinding(
        ActorSecurityIdentifier groupSid,
        IReadOnlyList<ApplicationRole> roles,
        bool allTargets,
        IReadOnlyList<MonitoredInstanceId>? targetIds = null)
    {
        ArgumentNullException.ThrowIfNull(groupSid);
        ArgumentNullException.ThrowIfNull(roles);
        targetIds ??= Array.Empty<MonitoredInstanceId>();

        if (roles.Count is 0 or > MaximumRoles || roles.Distinct().Count() != roles.Count)
        {
            throw new ArgumentException(
                $"A Windows group must map to between 1 and {MaximumRoles} unique roles.",
                nameof(roles));
        }

        if (roles.Any(static role => !Enum.IsDefined(role)))
        {
            throw new ArgumentOutOfRangeException(nameof(roles));
        }

        if (targetIds.Count > MaximumTargets)
        {
            throw new ArgumentException("A Windows group target scope is invalid.", nameof(targetIds));
        }

        if (targetIds.Any(static id => id is null))
        {
            throw new ArgumentException("A Windows group target scope cannot contain null.", nameof(targetIds));
        }

        if (targetIds.Select(static id => id.Value).Distinct().Count() != targetIds.Count)
        {
            throw new ArgumentException("A Windows group target scope is invalid.", nameof(targetIds));
        }

        if (allTargets && targetIds.Count != 0)
        {
            throw new ArgumentException("An all-target Windows group cannot enumerate target IDs.", nameof(targetIds));
        }

        GroupSid = groupSid;
        _roles = Array.AsReadOnly(roles.ToArray());
        AllTargets = allTargets;
        _targetIds = Array.AsReadOnly(targetIds.ToArray());
    }

    public ActorSecurityIdentifier GroupSid { get; }

    public IReadOnlyList<ApplicationRole> Roles => _roles;

    public bool AllTargets { get; }

    public IReadOnlyList<MonitoredInstanceId> TargetIds => _targetIds;
}

/// <summary>
/// Resolves authenticated Windows SID claims through explicit group bindings. Authentication alone
/// grants no role or target access, and account names are never used as durable authorization keys.
/// </summary>
public sealed class WindowsGroupRoleResolver
{
    public const int MaximumBindings = 256;
    public const int MaximumDisabledPrincipals = 4_096;

    private readonly ReadOnlyDictionary<string, WindowsGroupRoleBinding> _bindings;
    private readonly HashSet<string> _disabledActorSids;

    public WindowsGroupRoleResolver(
        IReadOnlyList<WindowsGroupRoleBinding> bindings,
        IReadOnlyList<ActorSecurityIdentifier>? disabledActorSids = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        disabledActorSids ??= Array.Empty<ActorSecurityIdentifier>();

        if (bindings.Count > MaximumBindings)
        {
            throw new ArgumentException($"At most {MaximumBindings} Windows group bindings are allowed.", nameof(bindings));
        }

        if (disabledActorSids.Count > MaximumDisabledPrincipals)
        {
            throw new ArgumentException(
                $"At most {MaximumDisabledPrincipals} disabled principals are allowed.",
                nameof(disabledActorSids));
        }

        var mapping = new Dictionary<string, WindowsGroupRoleBinding>(StringComparer.Ordinal);
        foreach (WindowsGroupRoleBinding binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (!mapping.TryAdd(binding.GroupSid.Value, binding))
            {
                throw new ArgumentException("Windows group SID bindings must be unique.", nameof(bindings));
            }
        }

        ValidateConfiguredScopeBounds(bindings);

        _bindings = new ReadOnlyDictionary<string, WindowsGroupRoleBinding>(mapping);
        _disabledActorSids = new HashSet<string>(
            disabledActorSids.Select(static sid => sid?.Value ?? throw new ArgumentException(
                "A disabled-principal SID cannot be null.",
                nameof(disabledActorSids))),
            StringComparer.Ordinal);
    }

    public AuthorizationContext Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated is not true)
        {
            throw new UnauthorizedAccessException("Windows authentication is required.");
        }

        ActorSecurityIdentifier actorSid = ReadActorSid(principal);
        var roleScopes = new Dictionary<ApplicationRole, RoleScopeAccumulator>();

        foreach (Claim claim in principal.FindAll(ClaimTypes.GroupSid))
        {
            if (!_bindings.TryGetValue(claim.Value, out WindowsGroupRoleBinding? binding))
            {
                continue;
            }

            foreach (ApplicationRole role in binding.Roles)
            {
                if (!roleScopes.TryGetValue(role, out RoleScopeAccumulator? scope))
                {
                    scope = new RoleScopeAccumulator();
                    roleScopes.Add(role, scope);
                }

                scope.Add(binding);
            }
        }

        AuthorizationPrincipalState state = _disabledActorSids.Contains(actorSid.Value)
            ? AuthorizationPrincipalState.Disabled
            : AuthorizationPrincipalState.Active;
        RoleAuthorizationGrant[] grants = roleScopes
            .OrderBy(static entry => entry.Key)
            .Select(static entry => new RoleAuthorizationGrant(entry.Key, entry.Value.CreateScope()))
            .ToArray();
        return new AuthorizationContext(actorSid, state, grants);
    }

    private static ActorSecurityIdentifier ReadActorSid(ClaimsPrincipal principal)
    {
        Claim? claim = principal.FindFirst(ClaimTypes.PrimarySid) ??
            principal.FindFirst(ClaimTypes.NameIdentifier);
        if (claim is null)
        {
            throw new UnauthorizedAccessException("The authenticated Windows principal has no SID claim.");
        }

        try
        {
            return new ActorSecurityIdentifier(claim.Value);
        }
        catch (ArgumentException exception)
        {
            throw new UnauthorizedAccessException("The authenticated Windows principal SID is invalid.", exception);
        }
    }

    private static void ValidateConfiguredScopeBounds(IReadOnlyList<WindowsGroupRoleBinding> bindings)
    {
        var targetsByRole = new Dictionary<ApplicationRole, HashSet<Guid>>();
        var aggregateTargets = new HashSet<Guid>();

        foreach (WindowsGroupRoleBinding binding in bindings)
        {
            if (binding.AllTargets)
            {
                continue;
            }

            foreach (ApplicationRole role in binding.Roles)
            {
                if (!targetsByRole.TryGetValue(role, out HashSet<Guid>? roleTargets))
                {
                    roleTargets = [];
                    targetsByRole.Add(role, roleTargets);
                }

                foreach (MonitoredInstanceId targetId in binding.TargetIds)
                {
                    if (roleTargets.Add(targetId.Value) &&
                        roleTargets.Count > TargetAuthorizationScope.MaximumTargetCount)
                    {
                        throw new ArgumentException(
                            $"Combined Windows group scope for one role cannot exceed {TargetAuthorizationScope.MaximumTargetCount} targets.",
                            nameof(bindings));
                    }

                    aggregateTargets.Add(targetId.Value);
                }
            }
        }

        if (aggregateTargets.Count > TargetAuthorizationScope.MaximumTargetCount)
        {
            throw new ArgumentException(
                $"Combined Windows group scopes cannot exceed {TargetAuthorizationScope.MaximumTargetCount} unique targets.",
                nameof(bindings));
        }
    }

    private sealed class RoleScopeAccumulator
    {
        private readonly Dictionary<Guid, MonitoredInstanceId> _targetIds = [];

        private bool AllTargets { get; set; }

        public void Add(WindowsGroupRoleBinding binding)
        {
            if (AllTargets)
            {
                return;
            }

            if (binding.AllTargets)
            {
                AllTargets = true;
                _targetIds.Clear();
                return;
            }

            foreach (MonitoredInstanceId targetId in binding.TargetIds)
            {
                _targetIds.TryAdd(targetId.Value, targetId);
            }
        }

        public TargetAuthorizationScope CreateScope() => AllTargets
            ? TargetAuthorizationScope.ForAllTargets()
            : TargetAuthorizationScope.ForTargets(_targetIds.Values.ToArray());
    }
}
