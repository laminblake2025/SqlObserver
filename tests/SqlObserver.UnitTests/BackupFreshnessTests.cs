using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BackupFreshnessTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("f30fcaa5-5e41-4d52-a302-e08bc46d9ea3"));
    private static readonly ObservationTargetRevision Revision = new(3);
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 15, 0, 0, TimeSpan.Zero);
    private static readonly string Database = new('a', 64);

    [Fact]
    public void ComparesFullAndLogIndependentlyAgainstExplicitPolicy()
    {
        var result = Assess([Sample(BackupKind.Full, TimeSpan.FromHours(20)), Sample(BackupKind.Log, TimeSpan.FromMinutes(16))]);
        Assert.Equal(7, result.PolicyRevision);
        Assert.Equal(Now, result.AssessedAtUtc);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, result.Full.Status);
        Assert.Equal(TimeSpan.FromHours(20), result.Full.ObservedAge);
        Assert.Equal(BackupFreshnessStatus.PolicyViolated, result.Log.Status);
        Assert.Equal(BackupFreshnessReason.AgeExceeded, result.Log.Reason);
        Assert.Equal(TimeSpan.FromMinutes(16), result.Log.ObservedAge);
    }

    [Fact]
    public void ThresholdAndFreshnessBoundariesAreInclusive()
    {
        var result = BackupFreshnessEvaluator.Assess(Policy(), Evidence(
            [Sample(BackupKind.Full, TimeSpan.FromHours(24)), Sample(BackupKind.Log, TimeSpan.FromMinutes(15))],
            observedAt: Now.AddMinutes(-15)), Now);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, result.Full.Status);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, result.Log.Status);
        var later = BackupFreshnessEvaluator.Assess(Policy(), Evidence(
            [Sample(BackupKind.Full, TimeSpan.FromHours(24)), Sample(BackupKind.Log, TimeSpan.FromMinutes(15))]), Now.AddSeconds(1));
        Assert.Equal(BackupFreshnessReason.AgeExceeded, later.Full.Reason);
        Assert.Equal(BackupFreshnessReason.AgeExceeded, later.Log.Reason);
    }

    [Theory]
    [InlineData(BackupRecoveryModel.Full)]
    [InlineData(BackupRecoveryModel.BulkLogged)]
    public void FullAndBulkLoggedRequireRegularLogEvidence(BackupRecoveryModel model)
    {
        var result = BackupFreshnessEvaluator.Assess(Policy(), Evidence(
            [Sample(BackupKind.Full, TimeSpan.FromHours(1)), Sample(BackupKind.Log, TimeSpan.FromMinutes(1), copyOnly: true)], model: model), Now);
        Assert.Equal(BackupFreshnessReason.BackupNotObserved, result.Log.Reason);
        Assert.Equal(BackupFreshnessStatus.Indeterminate, result.Log.Status);
    }

    [Fact]
    public void SimpleRecoveryDoesNotRequireLogBackups()
    {
        var result = BackupFreshnessEvaluator.Assess(Policy(), Evidence(
            [Sample(BackupKind.Full, TimeSpan.FromHours(1))], model: BackupRecoveryModel.Simple), Now);
        Assert.Equal(BackupFreshnessStatus.NotRequired, result.Log.Status);
        Assert.Equal(BackupFreshnessReason.SimpleRecovery, result.Log.Reason);
        Assert.Null(result.Log.MaximumAge);
    }

    [Fact]
    public void UnknownRecoveryAndMissingLogPolicyRemainIndeterminate()
    {
        var unknown = BackupFreshnessEvaluator.Assess(Policy(), Evidence([], model: BackupRecoveryModel.Unknown), Now);
        Assert.Equal(BackupFreshnessReason.RecoveryModelUnknown, unknown.Log.Reason);
        var unconfigured = BackupFreshnessEvaluator.Assess(Policy(logConfigured: false), Evidence([]), Now);
        Assert.Equal(BackupFreshnessReason.LogThresholdNotConfigured, unconfigured.Log.Reason);
        Assert.Null(unconfigured.Log.MaximumAge);
    }

    [Fact]
    public void CopyOnlyFullQualifiesForFullImageButCannotBecomeDifferentialBase()
    {
        var samples = new[] { Sample(BackupKind.Full, TimeSpan.FromDays(2)), Sample(BackupKind.Full, TimeSpan.FromMinutes(10), copyOnly: true) };
        var image = Assess(samples);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, image.Full.Status);
        Assert.True(image.Full.SelectedBackup!.CopyOnly);
        var basis = BackupFreshnessEvaluator.Assess(Policy(requirement: FullBackupRequirement.DifferentialBase), Evidence(samples), Now);
        Assert.Equal(BackupFreshnessReason.AgeExceeded, basis.Full.Reason);
        Assert.False(basis.Full.SelectedBackup!.CopyOnly);
        var onlyCopy = BackupFreshnessEvaluator.Assess(Policy(requirement: FullBackupRequirement.DifferentialBase),
            Evidence([samples[1]]), Now);
        Assert.Equal(BackupFreshnessReason.BackupNotObserved, onlyCopy.Full.Reason);
    }

    [Fact]
    public void DifferentialDoesNotReplaceFullOrLogEvidence()
    {
        var result = Assess([Sample(BackupKind.Differential, TimeSpan.FromMinutes(1))]);
        Assert.Equal(BackupFreshnessReason.BackupNotObserved, result.Full.Reason);
        Assert.Equal(BackupFreshnessReason.BackupNotObserved, result.Log.Reason);
    }

    [Theory]
    [InlineData(BackupFreshnessCoverage.NotCollected, BackupFreshnessReason.NotCollected)]
    [InlineData(BackupFreshnessCoverage.Partial, BackupFreshnessReason.SourceIncomplete)]
    [InlineData(BackupFreshnessCoverage.Truncated, BackupFreshnessReason.SourceTruncated)]
    [InlineData(BackupFreshnessCoverage.PermissionDenied, BackupFreshnessReason.PermissionDenied)]
    [InlineData(BackupFreshnessCoverage.Failed, BackupFreshnessReason.CollectionFailed)]
    [InlineData(BackupFreshnessCoverage.Unsupported, BackupFreshnessReason.Unsupported)]
    public void MissingCoverageNeverProducesWithinThreshold(BackupFreshnessCoverage coverage, BackupFreshnessReason reason)
    {
        BackupFreshnessSample[] samples = coverage is BackupFreshnessCoverage.Partial or BackupFreshnessCoverage.Truncated
            ? [Sample(BackupKind.Full, TimeSpan.FromMinutes(1)), Sample(BackupKind.Log, TimeSpan.FromMinutes(1))] : [];
        var result = BackupFreshnessEvaluator.Assess(Policy(), Evidence(
            samples, coverage: coverage), Now);
        Assert.Equal(BackupFreshnessStatus.Indeterminate, result.Full.Status);
        Assert.Equal(BackupFreshnessStatus.Indeterminate, result.Log.Status);
        Assert.Equal(reason, result.Full.Reason);
        Assert.Equal(reason, result.Log.Reason);
    }

    [Theory]
    [InlineData(BackupFreshnessCoverage.NotCollected)]
    [InlineData(BackupFreshnessCoverage.PermissionDenied)]
    [InlineData(BackupFreshnessCoverage.Failed)]
    [InlineData(BackupFreshnessCoverage.Unsupported)]
    public void FailedOrUnpublishedReadsCannotPublishAnObservedPrefix(BackupFreshnessCoverage coverage) =>
        Assert.Throws<ArgumentException>(() => Evidence([Sample(BackupKind.Full, TimeSpan.FromMinutes(1))], coverage: coverage));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void LocalAgHistoryCannotEstablishLogicalDatabaseFreshness(bool? preferred)
    {
        var result = BackupFreshnessEvaluator.Assess(Policy(), Evidence(
            [Sample(BackupKind.Full, TimeSpan.FromMinutes(1)), Sample(BackupKind.Log, TimeSpan.FromMinutes(1))],
            scope: BackupFreshnessScope.AvailabilityGroupLocalOnly, preferred: preferred), Now);
        Assert.Equal(BackupFreshnessStatus.Indeterminate, result.Full.Status);
        Assert.Equal(BackupFreshnessReason.OtherReplicaEvidenceRequired, result.Full.Reason);
        Assert.Equal(BackupFreshnessReason.OtherReplicaEvidenceRequired, result.Log.Reason);
    }

    [Fact]
    public void CompleteAllReplicaEvidenceCanBeAssessedWithoutRequiringLocalPreference()
    {
        var result = BackupFreshnessEvaluator.Assess(Policy(), Evidence(
            [Sample(BackupKind.Full, TimeSpan.FromMinutes(10)), Sample(BackupKind.Log, TimeSpan.FromMinutes(2))],
            scope: BackupFreshnessScope.AvailabilityGroupAllReplicas, preferred: false), Now);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, result.Full.Status);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, result.Log.Status);
    }

    [Fact]
    public void UnknownTopologyIsDistinctFromObservedStandalone()
    {
        var result = BackupFreshnessEvaluator.Assess(Policy(), Evidence([], scope: BackupFreshnessScope.Unknown), Now);
        Assert.Equal(BackupFreshnessReason.TopologyUnknown, result.Full.Reason);
    }

    [Fact]
    public void StaleAndFutureObservationCannotClaimFreshnessOrSimpleRecoveryExemption()
    {
        var stale = BackupFreshnessEvaluator.Assess(Policy(), Evidence([], observedAt: Now.AddMinutes(-15).AddSeconds(-1),
            model: BackupRecoveryModel.Simple), Now);
        Assert.Equal(BackupFreshnessReason.StaleObservation, stale.Full.Reason);
        Assert.Equal(BackupFreshnessReason.StaleObservation, stale.Log.Reason);
        var future = BackupFreshnessEvaluator.Assess(Policy(), Evidence([], observedAt: Now.AddSeconds(1)), Now);
        Assert.Equal(BackupFreshnessReason.FutureSourceTime, future.Full.Reason);
    }

    [Fact]
    public void FutureBackupTimeIsUnknownWithoutClampingAgeToZero()
    {
        var result = Assess([Sample(BackupKind.Full, TimeSpan.FromSeconds(-1))]);
        Assert.Equal(BackupFreshnessReason.FutureSourceTime, result.Full.Reason);
        Assert.Null(result.Full.ObservedAge);
        Assert.Equal(Now.AddSeconds(1), result.Full.SelectedBackup!.FinishUtc);
    }

    [Fact]
    public void UnresolvedFinishDoesNotBorrowTheHostTimeZoneOrAnOlderTimestamp()
    {
        var local = new DateTime(2026, 9, 30, 10, 59, 0, DateTimeKind.Unspecified);
        var unknown = new BackupFreshnessSample(BackupKind.Full, true, null, local, true, false, true);
        var evidence = Evidence([Sample(BackupKind.Full, TimeSpan.FromHours(1)), unknown]);
        var image = BackupFreshnessEvaluator.Assess(Policy(), evidence, Now);
        Assert.Equal(BackupFreshnessReason.FinishTimeUnknown, image.Full.Reason);
        Assert.Equal(local, evidence.Samples[1].SourceLocalFinish);
        var basis = BackupFreshnessEvaluator.Assess(Policy(requirement: FullBackupRequirement.DifferentialBase), evidence, Now);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, basis.Full.Status);
    }

    [Fact]
    public void LatestDamagedBackupRemainsViolationAndFaultSurvivesPartialCoverage()
    {
        var damaged = new BackupFreshnessSample(BackupKind.Full, false, Now.AddMinutes(-1), null, true, true, true);
        var complete = Assess([damaged]);
        Assert.Equal(BackupFreshnessStatus.PolicyViolated, complete.Full.Status);
        Assert.Equal(BackupFreshnessReason.BackupDamaged, complete.Full.Reason);
        var partial = BackupFreshnessEvaluator.Assess(Policy(), Evidence([damaged], coverage: BackupFreshnessCoverage.Partial), Now);
        Assert.Equal(BackupFreshnessStatus.Indeterminate, partial.Full.Status);
        Assert.Equal(BackupFreshnessReason.BackupDamaged, Assert.Single(partial.ObservedIssues).Reason);
    }

    [Theory]
    [InlineData(false, true, false, BackupFreshnessReason.ChecksumMissing, BackupFreshnessStatus.PolicyViolated)]
    [InlineData(null, true, false, BackupFreshnessReason.ChecksumUnknown, BackupFreshnessStatus.Indeterminate)]
    [InlineData(true, null, false, BackupFreshnessReason.BackupMetadataUnknown, BackupFreshnessStatus.Indeterminate)]
    [InlineData(true, true, null, BackupFreshnessReason.BackupMetadataUnknown, BackupFreshnessStatus.Indeterminate)]
    public void UnknownMetadataAndMissingChecksumsHaveDistinctResults(bool? checksum, bool? metadata, bool? damaged,
        BackupFreshnessReason reason, BackupFreshnessStatus status)
    {
        var result = Assess([new BackupFreshnessSample(BackupKind.Full, false, Now.AddMinutes(-1), null, checksum, damaged, metadata)]);
        Assert.Equal(status, result.Full.Status);
        Assert.Equal(reason, result.Full.Reason);
    }

    [Fact]
    public void ChecksumRequirementIsExplicitAndDisabledPolicyNeverEvaluatesAge()
    {
        var sample = new BackupFreshnessSample(BackupKind.Full, false, Now.AddMinutes(-1), null, false, false, true);
        var optional = BackupFreshnessEvaluator.Assess(Policy(checksums: false), Evidence([sample]), Now);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, optional.Full.Status);
        Assert.Empty(optional.ObservedIssues);
        var disabled = BackupFreshnessEvaluator.Assess(Policy(enabled: false), Evidence([], coverage: BackupFreshnessCoverage.Failed), Now);
        Assert.Equal(BackupFreshnessStatus.Disabled, disabled.Full.Status);
        Assert.Equal(BackupFreshnessStatus.Disabled, disabled.Log.Status);
    }

    [Fact]
    public void EvidenceOwnsItsSampleListAndRejectsDuplicateSlots()
    {
        var source = new[] { Sample(BackupKind.Full, TimeSpan.FromHours(1)) };
        var evidence = Evidence(source);
        source[0] = Sample(BackupKind.Log, TimeSpan.FromMinutes(1));
        Assert.Equal(BackupKind.Full, Assert.Single(evidence.Samples).Kind);
        Assert.Throws<ArgumentException>(() => Evidence([source[0], source[0]]));
        Assert.Throws<ArgumentException>(() => Evidence([null!]));
        Assert.Throws<ArgumentException>(() => new BackupFreshnessEvidence(Target, Revision, Database, null, Now,
            BackupRecoveryModel.Full, BackupFreshnessScope.Standalone, BackupFreshnessCoverage.Complete, null, []));
        Assert.Throws<ArgumentException>(() => Evidence([], preferred: true));
    }

    [Fact]
    public void CrossTargetDatabaseAndRevisionEvidenceAreRejectedEvenWhenDisabled()
    {
        var policy = Policy(enabled: false);
        foreach (BackupFreshnessEvidence evidence in new[]
        {
            Evidence([], target: new MonitoredInstanceId(Guid.NewGuid())),
            Evidence([], revision: new ObservationTargetRevision(4)),
            Evidence([], database: new string('b', 64)),
        })
            Assert.Throws<ArgumentException>(() => BackupFreshnessEvaluator.Assess(policy, evidence, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2592001)]
    public void PolicyRejectsUnboundedAndNonpositiveThresholds(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(fullAge: TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void PolicyAndEvidenceRejectMalformedIdentityStatesAndTimes()
    {
        Assert.Throws<ArgumentException>(() => Policy(database: "database-name"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(policyRevision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(fullAge: TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(observationAge: TimeSpan.FromHours(2)));
        Assert.Throws<ArgumentException>(() => Evidence([], scope: (BackupFreshnessScope)100));
        Assert.Throws<ArgumentException>(() => BackupFreshnessEvaluator.Assess(Policy(), Evidence([]), Now.ToOffset(TimeSpan.FromHours(2))));
        Assert.Throws<ArgumentException>(() => Sample(BackupKind.Full, TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentException>(() => new BackupFreshnessSample(BackupKind.Full, false, null, DateTime.UtcNow, true, false, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample(BackupKind.Differential, TimeSpan.Zero, copyOnly: true));
    }

    private static BackupFreshnessAssessment Assess(IReadOnlyList<BackupFreshnessSample> samples) =>
        BackupFreshnessEvaluator.Assess(Policy(), Evidence(samples), Now);

    private static BackupFreshnessSample Sample(BackupKind kind, TimeSpan age, bool copyOnly = false) =>
        new(kind, copyOnly, Now - age, null, true, false, true);

    private static BackupFreshnessPolicy Policy(bool enabled = true, bool checksums = true,
        FullBackupRequirement requirement = FullBackupRequirement.FullImage,
        TimeSpan? fullAge = null, TimeSpan? logAge = null, bool logConfigured = true, TimeSpan? observationAge = null,
        string? database = null, long policyRevision = 7) =>
        new(Target, Revision, database ?? Database, policyRevision, enabled,
            fullAge ?? TimeSpan.FromHours(24), logConfigured ? logAge ?? TimeSpan.FromMinutes(15) : null,
            observationAge ?? TimeSpan.FromMinutes(15), requirement, checksums);

    private static BackupFreshnessEvidence Evidence(IReadOnlyList<BackupFreshnessSample> samples,
        BackupRecoveryModel model = BackupRecoveryModel.Full, BackupFreshnessScope scope = BackupFreshnessScope.Standalone,
        BackupFreshnessCoverage coverage = BackupFreshnessCoverage.Complete, bool? preferred = null,
        DateTimeOffset? observedAt = null, MonitoredInstanceId? target = null,
        ObservationTargetRevision? revision = null, string? database = null) =>
        new(target ?? Target, revision ?? Revision, database ?? Database, new CollectorRunId(Guid.NewGuid()),
            observedAt ?? Now, model, scope, coverage, preferred, samples);
}
