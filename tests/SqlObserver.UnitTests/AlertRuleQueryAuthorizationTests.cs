using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class AlertRuleQueryAuthorizationTests
{
    [Fact]
    public async Task MixedGrantsCannotReadAnotherTargetsRules()
    {
        var administered = new MonitoredInstanceId(Guid.NewGuid());
        var viewed = new MonitoredInstanceId(Guid.NewGuid());
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-7003"), AuthorizationPrincipalState.Active,
            [
                new RoleAuthorizationGrant(ApplicationRole.TargetAdministrator,
                    TargetAuthorizationScope.ForTargets([administered])),
                new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([viewed])),
            ]);
        var repository = new RecordingRuleRead();
        var service = new AlertRuleQueryService(repository);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListRulesForAdminAsync(
            authorization, viewed, 10, null, CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Calls);

        AlertRuleListPage page = await service.ListRulesForAdminAsync(
            authorization, administered, 10, null, CancellationToken.None);
        Assert.Equal(administered, page.TargetId);
        Assert.Equal(1, repository.Calls);
    }

    private sealed class RecordingRuleRead : IAlertRuleReadPort
    {
        public int Calls { get; private set; }

        public ValueTask<AlertRuleListPage> ListRulesForAdminAsync(
            MonitoredInstanceId targetId, int limit, AlertRuleListCursor? cursor,
            RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new AlertRuleListPage(targetId, DateTimeOffset.UtcNow,
                [], null));
        }
    }
}
