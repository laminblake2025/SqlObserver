using System.Collections.ObjectModel;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Authorization;

public enum ApplicationRole
{
    Viewer = 1,
    Operator = 2,
    TargetAdministrator = 3,
    SecurityAdministrator = 4,
    Auditor = 5,
    CollectorService = 6,
    QueryTextReader = 7,
}

public enum AuthorizationPrincipalState
{
    Active = 1,
    Disabled = 2,
}

/// <summary>An immutable, deny-by-default set of target identifiers.</summary>
public sealed class TargetAuthorizationScope
{
    public const int MaximumTargetCount = 1_024;

    private readonly ReadOnlyCollection<MonitoredInstanceId> _targetIds;
    private readonly HashSet<Guid> _targetValues;

    private TargetAuthorizationScope(bool allTargets, IReadOnlyList<MonitoredInstanceId> targetIds)
    {
        if (allTargets && targetIds.Count != 0)
        {
            throw new ArgumentException("An all-target scope cannot also enumerate target identifiers.", nameof(targetIds));
        }

        if (targetIds.Count > MaximumTargetCount)
        {
            throw new ArgumentException(
                $"A target scope cannot contain more than {MaximumTargetCount} identifiers.",
                nameof(targetIds));
        }

        var copy = new MonitoredInstanceId[targetIds.Count];
        _targetValues = new HashSet<Guid>();

        for (int index = 0; index < targetIds.Count; index++)
        {
            MonitoredInstanceId targetId = targetIds[index] ?? throw new ArgumentException(
                "A target scope cannot contain a null identifier.",
                nameof(targetIds));

            if (!_targetValues.Add(targetId.Value))
            {
                throw new ArgumentException("A target scope cannot contain duplicate identifiers.", nameof(targetIds));
            }

            copy[index] = targetId;
        }

        AllTargets = allTargets;
        _targetIds = Array.AsReadOnly(copy);
    }

    public bool AllTargets { get; }

    public IReadOnlyList<MonitoredInstanceId> TargetIds => _targetIds;

    public static TargetAuthorizationScope None() => new(false, Array.Empty<MonitoredInstanceId>());

    public static TargetAuthorizationScope ForAllTargets() => new(true, Array.Empty<MonitoredInstanceId>());

    public static TargetAuthorizationScope ForTargets(IReadOnlyList<MonitoredInstanceId> targetIds)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        return new TargetAuthorizationScope(false, targetIds);
    }

    public bool Contains(MonitoredInstanceId targetId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        return AllTargets || _targetValues.Contains(targetId.Value);
    }
}

/// <summary>One application role and the exact target scope for which it is granted.</summary>
public sealed class RoleAuthorizationGrant
{
    public RoleAuthorizationGrant(ApplicationRole role, TargetAuthorizationScope targetScope)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ArgumentNullException.ThrowIfNull(targetScope);
        Role = role;
        TargetScope = targetScope;
    }

    public ApplicationRole Role { get; }

    public TargetAuthorizationScope TargetScope { get; }
}

/// <summary>Server-resolved Windows principal, roles, and target scope.</summary>
public sealed class AuthorizationContext
{
    public const int MaximumRoleCount = 16;

    private readonly ReadOnlyCollection<ApplicationRole> _roles;
    private readonly HashSet<ApplicationRole> _roleSet;
    private readonly ReadOnlyDictionary<ApplicationRole, TargetAuthorizationScope> _roleScopes;

    public AuthorizationContext(
        ActorSecurityIdentifier actorSid,
        AuthorizationPrincipalState principalState,
        IReadOnlyList<ApplicationRole> roles,
        TargetAuthorizationScope targetScope)
        : this(
            actorSid,
            principalState,
            CreateUniformGrants(roles, targetScope))
    {
    }

    public AuthorizationContext(
        ActorSecurityIdentifier actorSid,
        AuthorizationPrincipalState principalState,
        IReadOnlyList<RoleAuthorizationGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(actorSid);
        ArgumentNullException.ThrowIfNull(grants);

        if (!Enum.IsDefined(principalState))
        {
            throw new ArgumentOutOfRangeException(nameof(principalState));
        }

        if (grants.Count > MaximumRoleCount)
        {
            throw new ArgumentException(
                $"An authorization context cannot contain more than {MaximumRoleCount} roles.",
                nameof(grants));
        }

        var copy = new ApplicationRole[grants.Count];
        _roleSet = new HashSet<ApplicationRole>();
        var roleScopes = new Dictionary<ApplicationRole, TargetAuthorizationScope>();

        for (int index = 0; index < grants.Count; index++)
        {
            RoleAuthorizationGrant grant = grants[index] ?? throw new ArgumentException(
                "Authorization grants cannot contain null entries.",
                nameof(grants));
            ApplicationRole role = grant.Role;

            if (!Enum.IsDefined(role))
            {
                throw new ArgumentOutOfRangeException(nameof(grants), "An authorization role is invalid.");
            }

            if (!_roleSet.Add(role))
            {
                throw new ArgumentException("Authorization roles must be unique.", nameof(grants));
            }

            copy[index] = role;
            roleScopes.Add(role, grant.TargetScope);
        }

        ActorSid = actorSid;
        PrincipalState = principalState;
        _roles = Array.AsReadOnly(copy);
        _roleScopes = new ReadOnlyDictionary<ApplicationRole, TargetAuthorizationScope>(roleScopes);
        TargetScope = UnionScopes(grants.Select(static grant => grant.TargetScope));
    }

    public ActorSecurityIdentifier ActorSid { get; }

    public AuthorizationPrincipalState PrincipalState { get; }

    public IReadOnlyList<ApplicationRole> Roles => _roles;

    public TargetAuthorizationScope TargetScope { get; }

    public bool IsActive => PrincipalState == AuthorizationPrincipalState.Active;

    public bool HasRole(ApplicationRole role) => IsActive && _roleSet.Contains(role);

    public bool CanAccess(MonitoredInstanceId targetId) => IsActive && TargetScope.Contains(targetId);

    public bool CanAccess(ApplicationRole role, MonitoredInstanceId targetId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        return IsActive &&
            _roleScopes.TryGetValue(role, out TargetAuthorizationScope? scope) &&
            scope.Contains(targetId);
    }

    public bool HasRoleForAllTargets(ApplicationRole role) =>
        IsActive &&
        _roleScopes.TryGetValue(role, out TargetAuthorizationScope? scope) &&
        scope.AllTargets;

    public TargetAuthorizationScope GetScopeForRoles(IReadOnlyList<ApplicationRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return IsActive
            ? UnionScopes(roles
                .Distinct()
                .Where(_roleScopes.ContainsKey)
                .Select(role => _roleScopes[role]))
            : TargetAuthorizationScope.None();
    }

    private static RoleAuthorizationGrant[] CreateUniformGrants(
        IReadOnlyList<ApplicationRole> roles,
        TargetAuthorizationScope targetScope)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(targetScope);
        return roles.Select(role => new RoleAuthorizationGrant(role, targetScope)).ToArray();
    }

    private static TargetAuthorizationScope UnionScopes(IEnumerable<TargetAuthorizationScope> scopes)
    {
        var targetIds = new Dictionary<Guid, MonitoredInstanceId>();
        foreach (TargetAuthorizationScope scope in scopes)
        {
            if (scope.AllTargets)
            {
                return TargetAuthorizationScope.ForAllTargets();
            }

            foreach (MonitoredInstanceId targetId in scope.TargetIds)
            {
                targetIds.TryAdd(targetId.Value, targetId);
            }
        }

        return TargetAuthorizationScope.ForTargets(targetIds.Values.ToArray());
    }
}
