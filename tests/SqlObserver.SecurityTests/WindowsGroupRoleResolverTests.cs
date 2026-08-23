using System.Security.Claims;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.SecurityTests;

public sealed class WindowsGroupRoleResolverTests
{
    private static readonly ActorSecurityIdentifier UserSid = new("S-1-5-21-1000");
    private static readonly ActorSecurityIdentifier GroupSid = new("S-1-5-32-544");

    [Fact]
    public void ExactGroupSidMapsRolesAndTargetScope()
    {
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        var binding = new WindowsGroupRoleBinding(
            GroupSid,
            [ApplicationRole.Viewer, ApplicationRole.TargetAdministrator],
            allTargets: false,
            [targetId]);
        var resolver = new WindowsGroupRoleResolver([binding]);

        AuthorizationContext context = resolver.Resolve(CreatePrincipal(UserSid, GroupSid));

        Assert.True(context.IsActive);
        Assert.True(context.HasRole(ApplicationRole.Viewer));
        Assert.True(context.HasRole(ApplicationRole.TargetAdministrator));
        Assert.True(context.CanAccess(targetId));
        Assert.False(context.CanAccess(new MonitoredInstanceId(Guid.NewGuid())));
        Assert.Equal(UserSid, context.ActorSid);
    }

    [Fact]
    public void BroadReadScopeDoesNotWidenScopedAdministratorGrant()
    {
        var administeredTarget = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var otherTarget = new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var viewerGroup = new ActorSecurityIdentifier("S-1-5-32-545");
        var administratorGroup = new ActorSecurityIdentifier("S-1-5-32-546");
        var resolver = new WindowsGroupRoleResolver(
        [
            new WindowsGroupRoleBinding(
                viewerGroup,
                [ApplicationRole.Viewer],
                allTargets: true),
            new WindowsGroupRoleBinding(
                administratorGroup,
                [ApplicationRole.TargetAdministrator],
                allTargets: false,
                [administeredTarget]),
        ]);

        AuthorizationContext context = resolver.Resolve(
            CreatePrincipal(UserSid, viewerGroup, administratorGroup));

        Assert.True(context.HasRoleForAllTargets(ApplicationRole.Viewer));
        Assert.False(context.HasRoleForAllTargets(ApplicationRole.TargetAdministrator));
        Assert.True(context.CanAccess(ApplicationRole.TargetAdministrator, administeredTarget));
        Assert.False(context.CanAccess(ApplicationRole.TargetAdministrator, otherTarget));
        Assert.True(context.GetScopeForRoles([ApplicationRole.Viewer]).AllTargets);
        Assert.Equal(
            administeredTarget,
            Assert.Single(context.GetScopeForRoles([ApplicationRole.TargetAdministrator]).TargetIds));
    }

    [Fact]
    public void CombinedPerRoleScopeIsRejectedDuringConfiguration()
    {
        MonitoredInstanceId[] maximumScope = CreateTargetRange(
            start: 1,
            TargetAuthorizationScope.MaximumTargetCount);
        var overflowTarget = new MonitoredInstanceId(
            Guid.Parse("00000000-0000-0000-0001-000000000001"));
        var firstGroup = new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier("S-1-5-32-547"),
            [ApplicationRole.Viewer],
            allTargets: false,
            maximumScope);
        var secondGroup = new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier("S-1-5-32-548"),
            [ApplicationRole.Viewer],
            allTargets: false,
            [overflowTarget]);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new WindowsGroupRoleResolver([firstGroup, secondGroup]));

        Assert.Equal("bindings", exception.ParamName);
    }

    [Fact]
    public void CombinedCrossRoleScopeIsRejectedDuringConfiguration()
    {
        var viewerGroup = new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier("S-1-5-32-549"),
            [ApplicationRole.Viewer],
            allTargets: false,
            CreateTargetRange(start: 1, count: 600));
        var administratorGroup = new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier("S-1-5-32-550"),
            [ApplicationRole.TargetAdministrator],
            allTargets: false,
            CreateTargetRange(start: 601, count: 425));

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new WindowsGroupRoleResolver([viewerGroup, administratorGroup]));

        Assert.Equal("bindings", exception.ParamName);
    }

    [Fact]
    public void AllTargetBindingDoesNotHideAnOversizedScopedSubset()
    {
        var allTargetGroup = new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier("S-1-5-32-551"),
            [ApplicationRole.Viewer],
            allTargets: true);
        var firstScopedGroup = new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier("S-1-5-32-552"),
            [ApplicationRole.Viewer],
            allTargets: false,
            CreateTargetRange(start: 1, TargetAuthorizationScope.MaximumTargetCount));
        var overflowScopedGroup = new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier("S-1-5-32-553"),
            [ApplicationRole.Viewer],
            allTargets: false,
            CreateTargetRange(start: 2_000, count: 1));

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new WindowsGroupRoleResolver([allTargetGroup, firstScopedGroup, overflowScopedGroup]));

        Assert.Equal("bindings", exception.ParamName);
    }

    [Fact]
    public void DisabledPrincipalCannotUseMappedRolesOrScope()
    {
        var binding = new WindowsGroupRoleBinding(
            GroupSid,
            [ApplicationRole.TargetAdministrator],
            allTargets: true);
        var resolver = new WindowsGroupRoleResolver([binding], [UserSid]);

        AuthorizationContext context = resolver.Resolve(CreatePrincipal(UserSid, GroupSid));

        Assert.False(context.IsActive);
        Assert.False(context.HasRole(ApplicationRole.TargetAdministrator));
        Assert.False(context.CanAccess(new MonitoredInstanceId(Guid.NewGuid())));
    }

    [Fact]
    public void AuthenticationWithoutMappedGroupGrantsNothing()
    {
        var resolver = new WindowsGroupRoleResolver(Array.Empty<WindowsGroupRoleBinding>());

        AuthorizationContext context = resolver.Resolve(CreatePrincipal(UserSid));

        Assert.True(context.IsActive);
        Assert.Empty(context.Roles);
        Assert.False(context.TargetScope.AllTargets);
        Assert.Empty(context.TargetScope.TargetIds);
    }

    [Fact]
    public void AccountNameCannotSubstituteForRequiredSidClaim()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "CONTOSO\\Operator")],
            authenticationType: "Negotiate");
        var resolver = new WindowsGroupRoleResolver(Array.Empty<WindowsGroupRoleBinding>());

        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public void UnauthenticatedPrincipalIsRejectedBeforeMapping()
    {
        var resolver = new WindowsGroupRoleResolver(Array.Empty<WindowsGroupRoleBinding>());

        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(new ClaimsPrincipal()));
    }

    private static ClaimsPrincipal CreatePrincipal(
        ActorSecurityIdentifier actorSid,
        params ActorSecurityIdentifier[] groupSids)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, actorSid.Value),
        };
        claims.AddRange(groupSids.Select(static sid => new Claim(ClaimTypes.GroupSid, sid.Value)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Negotiate"));
    }

    private static MonitoredInstanceId[] CreateTargetRange(int start, int count) =>
        Enumerable.Range(start, count)
            .Select(static value => new MonitoredInstanceId(
                Guid.Parse($"00000000-0000-0000-0000-{value:D12}")))
            .ToArray();
}
