using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BackupFreshnessPolicyAuthorizationTests
{
    [Fact]
    public async Task MixedGrantsCannotAdministerAnotherTargetsPolicyOrReadAcrossAuditorScope()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var other = new MonitoredInstanceId(Guid.NewGuid());
        var actor = new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001");
        var authorization = new AuthorizationContext(actor, AuthorizationPrincipalState.Active,
            [new(ApplicationRole.TargetAdministrator, TargetAuthorizationScope.ForTargets([other])),
             new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target])),
             new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([other]))]);
        var repository = new Repository(); var audit = new Audit(); var service = new BackupFreshnessPolicyService(repository, audit);
        var policy = new BackupFreshnessPolicy(target, new(1), new string('a', 64), 1, true, TimeSpan.FromDays(1),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), FullBackupRequirement.FullImage, false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.SetAsync(authorization,
            new(policy, 0, Guid.NewGuid(), new(actor, new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, target),
                new(TimeSpan.FromSeconds(5))), CancellationToken.None));
        Assert.Equal(0, repository.Calls); Assert.Equal(1, audit.Calls); Assert.Equal(AdministrativeAuditReason.TargetOutOfScope, audit.Reason);
        var reader = new AuthorizationContext(actor, AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target])),
             new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([other]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.ReadAsync(reader,
            new(other, new(1), new string('a', 64), DateTimeOffset.UnixEpoch, new(TimeSpan.FromSeconds(5))), CancellationToken.None));
        Assert.Equal(0, repository.Calls);
    }

    private sealed class Repository : IBackupFreshnessPolicyPort
    {
        public int Calls { get; private set; }
        public ValueTask<BackupPolicyMutationResult> SetAsync(SetBackupFreshnessPolicyRequest request, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new BackupPolicyMutationResult(BackupPolicyMutationStatus.Applied, 1)); }
        public ValueTask<BackupFreshnessPolicy?> ReadAsync(ReadBackupFreshnessPolicyRequest request, CancellationToken token)
        { Calls++; return ValueTask.FromResult<BackupFreshnessPolicy?>(null); }
    }
    private sealed class Audit : IAdministrativeAuditPort
    {
        public int Calls { get; private set; }
        public AdministrativeAuditReason? Reason { get; private set; }
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken token)
        { Calls++; Reason = request.Record.Reason; return ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()), DateTimeOffset.UnixEpoch)); }
    }
}
