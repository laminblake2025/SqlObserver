using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BackupAlertSignalTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Digest = new('a', 64);

    [Fact]
    public void CompleteFullAndLogChecksProduceIndependentClearingZeros()
    {
        var signals = Build(Summary(Counts(within: 2), Counts(within: 2)));
        Assert.All(signals.Take(2), signal =>
        {
            Assert.Equal(BackupAlertSignalState.Clear, signal.State);
            Assert.Equal(0, signal.AffectedDatabases);
        });
        Assert.Equal(BackupAlertSignalReason.FaultAbsenceUnproven, signals[2].Reason);
        Assert.Null(signals[2].AffectedDatabases);
    }

    [Fact]
    public void ConfirmedViolationsSurvivePartialCoverageAndUnknownNeighbours()
    {
        var signals = Build(Summary(Counts(violated: 1, unknown: 1), Counts(unknown: 2),
            coverage: BackupFreshnessCoverage.Partial, faults: 1));
        Assert.Equal(BackupAlertSignalState.Violated, signals[0].State);
        Assert.Equal(1, signals[0].AffectedDatabases);
        Assert.Equal(BackupAlertSignalState.Unknown, signals[1].State);
        Assert.Null(signals[1].AffectedDatabases);
        Assert.Equal(BackupAlertSignalReason.ObservedBackupFaults, signals[2].Reason);
        Assert.Equal(1, signals[2].AffectedDatabases);
    }

    [Fact]
    public void MissingPoliciesOrRevokedTimesNeverClearASeparateFaultSignal()
    {
        var signals = Build(Summary(Counts(unknown: 2), Counts(notRequired: 2)));
        Assert.Equal(BackupAlertSignalState.Unknown, signals[0].State);
        Assert.Equal(BackupAlertSignalState.NotRequired, signals[1].State);
        Assert.Equal(BackupAlertSignalState.Unknown, signals[2].State);
        Assert.Null(signals[0].AffectedDatabases);
        Assert.Null(signals[2].AffectedDatabases);
    }

    [Fact]
    public void DamageSurvivesUnknownAgesWithoutInventingAPolicyViolation()
    {
        var signals = Build(Summary(Counts(unknown: 2), Counts(unknown: 2), faults: 1));
        Assert.Equal(BackupAlertSignalState.Unknown, signals[0].State);
        Assert.Equal(BackupAlertSignalState.Violated, signals[2].State);
    }

    [Fact]
    public void EntirelyDisabledPoliciesAndMixedDisabledChecksAreNotClearingZeros()
    {
        var signals = Build(Summary(Counts(disabled: 2), Counts(disabled: 2)));
        Assert.Equal(BackupAlertSignalState.Disabled, signals[0].State);
        Assert.Null(signals[0].AffectedDatabases);
        Assert.Equal(BackupAlertSignalState.Disabled, signals[1].State);
        Assert.Equal(BackupAlertSignalState.Unknown, signals[2].State);
        signals = Build(Summary(Counts(within: 1, disabled: 1), Counts(notRequired: 2)));
        Assert.Equal(BackupAlertSignalState.Unknown, signals[0].State);
        Assert.Null(signals[0].AffectedDatabases);
    }

    [Fact]
    public void DisabledCollectionCannotProduceNewNotificationsOrResolveOldOnes()
    {
        var signals = Build(Summary(Counts(violated: 2), Counts(unknown: 2), enabled: false, faults: 2));
        Assert.All(signals, signal =>
        {
            Assert.Equal(BackupAlertSignalReason.CollectorDisabled, signal.Reason);
            Assert.Equal(BackupAlertSignalState.Unknown, signal.State);
            Assert.Null(signal.AffectedDatabases);
        });
    }

    [Fact]
    public void NoCurrentCacheNeverBecomesAHealthyEmptyFleet()
    {
        var signals = BackupAlertSignalProjection.Build(new(Target, 1, At, null, null));
        Assert.Equal(3, signals.Count);
        Assert.All(signals, signal =>
        {
            Assert.Equal(BackupAlertSignalReason.NoCurrentAggregate, signal.Reason);
            Assert.Null(signal.AffectedDatabases);
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MixedContextsExpiredCutoffsAndInvalidDigestsFailClosed(int defect)
    {
        BackupFleetSummary summary = Summary(Counts(within: 2), Counts(within: 2));
        var evidence = new BackupAlertEvidence(Target, 1, At, summary, Digest);
        evidence = defect switch
        {
            0 => evidence with { TargetId = new(Guid.NewGuid()) },
            1 => evidence with { TargetRevision = 2 },
            2 => evidence with { ReadAtUtc = summary.ValidUntilUtc },
            3 => evidence with { ReadAtUtc = At.AddTicks(-TimeSpan.TicksPerMicrosecond) },
            4 => evidence with { SourceDigest = Digest.ToUpperInvariant() },
            _ => evidence with { Summary = null },
        };
        Assert.Throws<InvalidDataException>(() => BackupAlertSignalProjection.Build(evidence));
    }

    private static IReadOnlyList<BackupAlertSignal> Build(BackupFleetSummary summary) =>
        BackupAlertSignalProjection.Build(new(Target, 1, At, summary, Digest));

    private static BackupFleetCheckCounts Counts(int within = 0, int violated = 0, int unknown = 0,
        int notRequired = 0, int disabled = 0) => new(within, violated, unknown, notRequired, disabled,
            unknown == 0 ? [] : [new(BackupFreshnessReason.TimestampQualificationUnavailable, unknown)]);

    private static BackupFleetSummary Summary(BackupFleetCheckCounts full, BackupFleetCheckCounts log,
        BackupFreshnessCoverage coverage = BackupFreshnessCoverage.Complete, bool enabled = true, int faults = 0)
    {
        var header = new BackupEvidenceHeader(Target, At, 1, Guid.NewGuid(), At, At, At, "UTC", At.DateTime.AddDays(-35),
            coverage, BackupEvidenceStatus.Retained, 4, 4, 4, 2, enabled, 2, 1);
        BackupFleetStatus status = full.PolicyViolated + log.PolicyViolated > 0 ? BackupFleetStatus.PolicyViolated
            : !enabled || coverage != BackupFreshnessCoverage.Complete || full.Indeterminate + log.Indeterminate > 0
                ? BackupFleetStatus.Indeterminate
            : full.Disabled + log.Disabled == 4 ? BackupFleetStatus.Disabled : BackupFleetStatus.WithinPolicy;
        return new(header, At.AddMinutes(2), status, full, log, faults);
    }
}
