using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BackupZoneQualificationAuthorizationTests
{
    [Fact]
    public async Task MixedGrantCannotProvisionRevokeOrReadAnotherTargetsQualification()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        var repository = new Repository(); var audit = new Audit();
        var service = new BackupZoneQualificationAdministrationService(repository, audit);
        var authorization = new AuthorizationContext(new("S-1-5-21-1-2-3-1001"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.TargetAdministrator, TargetAuthorizationScope.ForTargets([other])),
             new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target]))]);
        foreach (bool enabled in new[] { true, false })
        {
            var qualification = enabled ? new BackupHistoricalZoneQualification(target, new(1), 1, TimeZoneInfo.Utc,
                "UTC", new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)) : null;
            var change = new BackupZoneQualificationChange(target, new(1), 0, Guid.NewGuid(), qualification,
                enabled ? new string('a', 64) : null);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.SetAsync(authorization,
                change, new(Guid.NewGuid()), new(TimeSpan.FromSeconds(5)), CancellationToken.None));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadAsync(authorization, target, new(1), CancellationToken.None));
        Assert.Equal(0, repository.Calls); Assert.Equal(2, audit.Calls);
        Assert.Equal(AdministrativeAuditReason.TargetOutOfScope, audit.Reason);
    }

    [Fact]
    public void ChangeRequiresEvidenceCorrectIdentityAndBoundedRules()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var qualification = new BackupHistoricalZoneQualification(target, new(1), 1, TimeZoneInfo.Utc, "UTC",
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new BackupZoneQualificationChange(target, new(1), 0, Guid.Empty, qualification, new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new BackupZoneQualificationChange(target, new(1), 0, Guid.NewGuid(), qualification, null));
        Assert.Throws<ArgumentException>(() => new BackupZoneQualificationChange(target, new(2), 0, Guid.NewGuid(), qualification, new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new BackupZoneQualificationChange(target, new(1), 0, Guid.NewGuid(), null, new string('a', 64)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupZoneQualificationChange(target, new(1), long.MaxValue, Guid.NewGuid(), null, null));
    }

    private sealed class Repository : IBackupZoneQualificationAdministrationPort
    {
        public int Calls { get; private set; }
        public ValueTask<BackupZoneQualificationMutationResult> SetAsync(SetBackupZoneQualificationRequest request, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new BackupZoneQualificationMutationResult(BackupZoneQualificationMutationStatus.Applied, 1)); }
        public ValueTask<BackupZoneQualificationSnapshot?> ReadAsync(MonitoredInstanceId target, ObservationTargetRevision revision, CancellationToken token)
        { Calls++; return ValueTask.FromResult<BackupZoneQualificationSnapshot?>(null); }
    }
    private sealed class Audit : IAdministrativeAuditPort
    {
        public int Calls { get; private set; }
        public AdministrativeAuditReason? Reason { get; private set; }
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken token)
        {
            Calls++; Reason = request.Record.Reason;
            return ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()), new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)));
        }
    }
}
