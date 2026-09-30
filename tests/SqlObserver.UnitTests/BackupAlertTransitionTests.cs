using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BackupAlertTransitionTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly Guid RuleId = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AlertRuleKind.BackupFullPolicy)] [InlineData(AlertRuleKind.BackupLogPolicy)]
    public void UnknownEvidenceRestartsPendingConfirmation(AlertRuleKind kind)
    {
        AlertRuleDefinition rule = Rule(kind);
        AlertEvaluationResult pending = AlertEvaluator.Evaluate(rule, new(RuleId, Target), Observe(rule, 1, 1));
        Assert.Equal(AlertState.Pending, pending.State.State);
        AlertEvaluationResult unknown = AlertEvaluator.Evaluate(rule, pending.State, Observe(rule, null, 2));
        Assert.Equal(AlertState.Normal, unknown.State.State);
        Assert.Equal(0, unknown.State.ConsecutiveMatches); Assert.Null(unknown.State.FirstMatchUtc);
        Assert.Null(unknown.State.AlertId); Assert.Null(unknown.State.EpisodeId); Assert.Null(unknown.Event);
        AlertEvaluationResult again = AlertEvaluator.Evaluate(rule, unknown.State, Observe(rule, 1, 3));
        Assert.Equal(AlertState.Pending, again.State.State);
        AlertEvaluationResult firing = AlertEvaluator.Evaluate(rule, again.State, Observe(rule, 1, 4));
        Assert.Equal(AlertEventKind.Fired, firing.Event);
        Assert.NotNull(firing.State.AlertId); Assert.NotNull(firing.State.EpisodeId);
    }

    [Theory]
    [InlineData(AlertState.Firing)] [InlineData(AlertState.Acknowledged)]
    public void UnknownInterruptsClearConfirmationAndKeepsTheSameEpisode(AlertState active)
    {
        AlertRuleDefinition rule = Rule(); AlertRuleState prior = Active(active);
        AlertEvaluationResult clear = AlertEvaluator.Evaluate(rule, prior, Observe(rule, 0, 1));
        Assert.Equal(1, clear.State.ConsecutiveClears);
        AlertEvaluationResult unknown = AlertEvaluator.Evaluate(rule, clear.State, Observe(rule, null, 2));
        Assert.Equal(active, unknown.State.State);
        Assert.Equal(0, unknown.State.ConsecutiveClears); Assert.Equal(0, unknown.State.ConsecutiveMatches);
        Assert.Null(unknown.State.LastValue); Assert.Null(unknown.Event); Assert.Null(unknown.State.FirstMatchUtc);
        Assert.Equal(prior.AlertId, unknown.State.AlertId); Assert.Equal(prior.EpisodeId, unknown.State.EpisodeId);
        Assert.Equal(prior.FiredUtc, unknown.State.FiredUtc); Assert.Equal(prior.AcknowledgedUtc, unknown.State.AcknowledgedUtc);
        Assert.Equal(prior.AcknowledgedBy, unknown.State.AcknowledgedBy);
        AlertEvaluationResult next = AlertEvaluator.Evaluate(rule, unknown.State, Observe(rule, 0, 3));
        Assert.Equal(active, next.State.State); Assert.Equal(1, next.State.ConsecutiveClears);
        AlertEvaluationResult resolved = AlertEvaluator.Evaluate(rule, next.State, Observe(rule, 0, 4));
        Assert.Equal(AlertEventKind.Resolved, resolved.Event); Assert.Equal(prior.EpisodeId, resolved.State.EpisodeId);
    }

    [Fact]
    public void ALongUnobservedGapAlsoRestartsBackupClearConfirmation()
    {
        AlertRuleDefinition rule = Rule();
        AlertEvaluationResult first = AlertEvaluator.Evaluate(rule, Active(), Observe(rule, 0, 1));
        AlertEvaluationResult afterGap = AlertEvaluator.Evaluate(rule, first.State, Observe(rule, 0, 20));
        Assert.Equal(AlertState.Firing, afterGap.State.State); Assert.Equal(1, afterGap.State.ConsecutiveClears); Assert.Null(afterGap.Event);
    }

    [Fact]
    public void ReplayIsReadOnlyAndCannotHideAChangedCanonicalObservation()
    {
        AlertRuleDefinition rule = Rule(); AlertObservation observation = Observe(rule, null, 1);
        AlertEvaluationResult first = AlertEvaluator.Evaluate(rule, Active(), observation);
        AlertEvaluationResult replay = AlertEvaluator.Evaluate(rule, first.State, observation);
        Assert.True(replay.IsReplay); Assert.Same(first.State, replay.State); Assert.Null(replay.Event);
        var divergent = new AlertObservation(Target, RuleId, observation.ObservedAtUtc, 1, null, observation.Reason,
            observation.OperationId, sampleId: observation.SampleId, runId: observation.RunId, sourceKind: observation.SourceKind,
            sourceCollector: "backups.status", sourceVersion: "2", sourceSchemaVersion: 2, sourceDigest: observation.SourceDigest);
        Assert.Throws<AlertReplayConflictException>(() => AlertEvaluator.Evaluate(rule, first.State, divergent));
        var forged = new AlertObservation(Target, RuleId, observation.ObservedAtUtc, 1, null, observation.Reason,
            observation.OperationId, observation.EvidenceDigest, observation.SampleId, observation.RunId, observation.SourceKind,
            sourceCollector: "backups.status", sourceVersion: "2", sourceSchemaVersion: 2, sourceDigest: observation.SourceDigest);
        Assert.Throws<ArgumentException>(() => AlertEvaluator.Evaluate(rule, first.State, forged));
    }

    [Fact]
    public void RegressedOrDuplicatedNonReplayCutoffsCannotAdvanceConfirmation()
    {
        AlertRuleDefinition rule = Rule(); AlertEvaluationResult first = AlertEvaluator.Evaluate(rule, Active(), Observe(rule, 0, 2));
        Assert.Throws<ArgumentException>(() => AlertEvaluator.Evaluate(rule, first.State, Observe(rule, 0, 1)));
        Assert.Throws<ArgumentException>(() => AlertEvaluator.Evaluate(rule, first.State, Observe(rule, 1, 2)));
    }

    [Theory]
    [InlineData(AlertRuleKind.BackupFullPolicy)] [InlineData(AlertRuleKind.BackupLogPolicy)] [InlineData(AlertRuleKind.BackupObservedFaults)]
    public void UnknownDuringMaintenanceNeverEmitsAnEvent(AlertRuleKind kind)
    {
        AlertRuleDefinition rule = Rule(kind);
        var window = new MaintenanceWindow(Guid.NewGuid(), Target, At, At.AddHours(1), "planned");
        AlertEvaluationResult result = AlertEvaluator.Evaluate(rule, Active(), Observe(rule, null, 1), window);
        Assert.True(result.DeliverySuppressed); Assert.Equal("maintenance", result.Reason); Assert.Null(result.Event);
        Assert.Equal(AlertState.Firing, result.State.State);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0.5)] [InlineData(513)]
    public void InvalidAffectedDatabaseCountsAreRejected(double value)
    {
        AlertRuleDefinition rule = Rule();
        Assert.Throws<ArgumentException>(() => AlertEvaluator.Evaluate(rule, Active(), Observe(rule, value, 1)));
    }

    [Fact]
    public void FaultAbsenceCannotBeUsedAsAZeroAndBackupRulesRequireValidConfirmationSettings()
    {
        AlertRuleDefinition rule = Rule(AlertRuleKind.BackupObservedFaults);
        Assert.Throws<ArgumentException>(() => AlertEvaluator.Evaluate(rule, Active(), Observe(rule, 0, 1)));
        foreach (AlertRuleKind kind in new[] { AlertRuleKind.BackupFullPolicy, AlertRuleKind.BackupLogPolicy, AlertRuleKind.BackupObservedFaults })
            Assert.True(AlertCatalog.IsApproved(Rule(kind)));
        Assert.Throws<ArgumentException>(() => new AlertRuleDefinition(RuleId, "backup.invalid", AlertRuleKind.BackupFullPolicy,
            null, AlertComparison.LessThan, 1, 0, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15)));
        Assert.Throws<ArgumentException>(() => new AlertRuleDefinition(RuleId, "backup.invalid", AlertRuleKind.BackupFullPolicy,
            null, AlertComparison.GreaterThan, 0, 0, 2, TimeSpan.Zero, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void FactoryMapsOnlyValidatedSignalsAndBindsCapturedSourceContext()
    {
        var counts = new BackupFleetCheckCounts(1, 0, 0, 0, 0, []);
        var header = new BackupEvidenceHeader(Target, At, 1, Guid.NewGuid(), At, At, At, "UTC", At.DateTime.AddDays(-35),
            BackupFreshnessCoverage.Complete, BackupEvidenceStatus.Retained, 2, 2, 2, 1, true, 1, 1);
        var summary = new BackupFleetSummary(header, At.AddMinutes(2), BackupFleetStatus.WithinPolicy, counts, counts, 0);
        var evidence = new BackupAlertEvidence(Target, 1, At, summary, new('a', 64));
        AlertObservation full = BackupAlertObservationFactory.Create(Rule(), evidence);
        Assert.Equal(0, full.Value); Assert.Equal(header.RunId, full.RunId); Assert.Equal(evidence.SourceDigest, full.SourceDigest);
        Assert.Equal("backup_full_policy_v2", full.SourceKind); Assert.Null(full.CollectorHealthy); Assert.Null(full.MetricId);
        AlertObservation fault = BackupAlertObservationFactory.Create(Rule(AlertRuleKind.BackupObservedFaults), evidence);
        Assert.Null(fault.Value); Assert.Contains("state=0", fault.Reason!, StringComparison.Ordinal);
        AlertObservation missing = BackupAlertObservationFactory.Create(Rule(), new(Target, 1, At, null, null));
        Assert.Null(missing.Value); Assert.Null(missing.RunId); Assert.Null(missing.SourceDigest);
        AlertObservation changed = BackupAlertObservationFactory.Create(Rule(), evidence with { SourceDigest = new('b', 64) });
        Assert.NotEqual(full.SampleId, changed.SampleId); Assert.NotEqual(full.EvidenceDigest, changed.EvidenceDigest);
        Assert.NotEqual(full.OperationId, changed.OperationId);
    }

    private static AlertRuleDefinition Rule(AlertRuleKind kind = AlertRuleKind.BackupFullPolicy) => new(RuleId, "backup.test", kind,
        null, AlertComparison.GreaterThan, 0, 0, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
    private static AlertObservation Observe(AlertRuleDefinition rule, double? value, int minute) => new(Target, RuleId, At.AddMinutes(minute),
        value, null, $"backup|value={value}|minute={minute}", sampleId: Guid.NewGuid().ToString("D"), runId: Guid.NewGuid(),
        sourceKind: BackupAlertRuleContract.SourceKind(rule.Kind), sourceCollector: "backups.status", sourceVersion: "2",
        sourceSchemaVersion: 2, sourceDigest: new('a', 64));
    private static AlertRuleState Active(AlertState state = AlertState.Firing) => new(RuleId, Target, state, 2, At, At,
        firedUtc: At, acknowledgedUtc: state == AlertState.Acknowledged ? At : null,
        alertId: Guid.NewGuid(), episodeId: Guid.NewGuid(), episodeStartedUtc: At,
        acknowledgedBy: state == AlertState.Acknowledged ? "S-1-5-21-1" : null, lastValue: 1);
}
