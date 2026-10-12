using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BackupAssessmentQueryTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task MixedGrantsCannotReadAcrossAnAuditorScope()
    {
        var repository = new Repository(Page()); var service = new BackupAssessmentQueryService(repository);
        var auth = new AuthorizationContext(new("S-1-5-21-1-2-3-1001"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Guid.NewGuid())])),
             new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([Target]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.ReadAsync(auth, Request(), CancellationToken.None));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task RevocationWithholdsAgeButPreservesRecordedTimeAndObservedFaults()
    {
        BackupEvidencePage source = Page(trusted: false, damaged: true);
        var result = await new BackupAssessmentQueryService(new Repository(source)).ReadAsync(Reader(), Request(), CancellationToken.None);
        var assessment = Assert.Single(result.Items);
        Assert.Equal(BackupFreshnessReason.TimestampQualificationUnavailable, assessment.Full.Reason);
        Assert.Equal(BackupFreshnessStatus.Indeterminate, assessment.Full.Status); Assert.Null(assessment.Full.ObservedAge);
        Assert.NotNull(assessment.Source.Evidence.Samples[0].Freshness.FinishUtc);
        Assert.Contains(assessment.ObservedIssues, issue => issue.Reason == BackupFreshnessReason.BackupDamaged);
        Assert.Contains(assessment.ObservedIssues, issue => issue.Reason == BackupFreshnessReason.ChecksumMissing);
        Assert.Equal(BackupFreshnessStatus.NotRequired, assessment.Log.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InvalidRepositoryIdentityCountsOrderingAndQualificationAreRejected(int defect)
    {
        BackupEvidencePage page = Page(); BackupDatabaseEvidence item = page.Items[0];
        page = defect switch
        {
            0 => page with { Header = page.Header with { TargetId = new(Guid.NewGuid()) } },
            1 => page with { Header = page.Header with { RetainedItems = 1 } },
            2 => page with { Items = [item with { DatabaseOrdinal = 2 }] },
            _ => page with { Items = [item with { QualificationTrusted = [] }] },
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new BackupAssessmentQueryService(new Repository(page))
            .ReadAsync(Reader(), Request(), CancellationToken.None));
    }

    private static BackupEvidencePage Page(bool trusted = true, bool damaged = false)
    {
        var identity = new BackupV2RunIdentity(Target, new(1), new(Run)); string db = new('d', 64), fork = new('f', 64);
        var context = new BackupV2DatabaseContext(db, fork, fork, null, null, null, true, BackupRecoveryModel.Simple,
            "ONLINE", BackupFreshnessScope.Standalone, null, null, At, "UTC", At.DateTime.AddDays(-35), false);
        var sample = new BackupV2SampleRead(new(BackupKind.Full, false, At.AddMinutes(-1), At.DateTime.AddMinutes(-1), false, damaged, true),
            At.DateTime.AddMinutes(-2), 0, 1000, fork, fork, new(BackupFinishTimeState.Resolved, At.AddMinutes(-1), 1));
        var evidence = new BackupV2DatabaseRead(context, [sample], identity, BackupFreshnessCoverage.Complete);
        var policy = new BackupFreshnessPolicy(Target, new(1), db, 1, true, TimeSpan.FromDays(1), null,
            TimeSpan.FromMinutes(5), FullBackupRequirement.FullImage, true);
        var header = new BackupEvidenceHeader(Target, At, 1, Run, At, At, At, "UTC", context.HistoryLowerBoundLocal,
            BackupFreshnessCoverage.Complete, BackupEvidenceStatus.Retained, 2, 2, 2, 1, true, 1, 1);
        return new(header, [new(1, evidence, policy, [trusted])], null);
    }
    private static BackupEvidenceRequest Request() => new(Target, At, 50, Timeout);
    private static AuthorizationContext Reader() => new(new("S-1-5-21-1-2-3-1001"), AuthorizationPrincipalState.Active,
        [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([Target]))]);
    private sealed class Repository(BackupEvidencePage page) : IBackupEvidencePort
    {
        public int Calls { get; private set; }
        public ValueTask<BackupEvidencePage> ReadAsync(BackupEvidenceRequest request, CancellationToken token)
        { Calls++; return ValueTask.FromResult(page); }
    }
}
