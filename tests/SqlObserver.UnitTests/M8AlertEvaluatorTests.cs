using SqlObserver.Alerting;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class M8AlertEvaluatorTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("3d4dd8f4-8f24-43ef-b3bb-ff603b1520b1"));
    private static readonly Guid RuleId = Guid.Parse("93df5d63-bd4c-472a-a0b0-0f7f2cc4e4a4");
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlertRuleDefinition Rule = new(RuleId, "cpu.high", AlertRuleKind.MetricThreshold, new MetricId("cpu.percent"), AlertComparison.GreaterThanOrEqual, 80, 5, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15));

    [Fact]
    public void ConfirmationRequiresTwoObservationsThenFires()
    {
        var normal = new AlertRuleState(RuleId, Target);
        var first = AlertEvaluator.Evaluate(Rule, normal, Observe(80, 0));
        Assert.Equal(AlertState.Pending, first.State.State);
        Assert.Null(first.Event);
        var second = AlertEvaluator.Evaluate(Rule, first.State, Observe(81, 1));
        Assert.Equal(AlertState.Firing, second.State.State);
        Assert.Equal(AlertEventKind.Fired, second.Event);
    }

    [Fact]
    public void HysteresisKeepsAlertFiringUntilLowerBoundary()
    {
        var firing = new AlertRuleState(RuleId, Target, AlertState.Firing, 2);
        var withinBand = AlertEvaluator.Evaluate(Rule, firing, Observe(77, 0));
        Assert.Equal(AlertState.Firing, withinBand.State.State);
        var resolved = AlertEvaluator.Evaluate(Rule, withinBand.State, Observe(74, 1));
        Assert.Equal(AlertState.Resolved, resolved.State.State);
        Assert.Equal(AlertEventKind.Resolved, resolved.Event);
    }

    [Fact]
    public void MaintenanceSuppressesDeliveryButStillTransitionsState()
    {
        DateTimeOffset start = BaseTime;
        var maintenance = new MaintenanceWindow(Guid.NewGuid(), Target, start, start.AddHours(1), "planned change");
        var result = AlertEvaluator.Evaluate(Rule, new AlertRuleState(RuleId, Target, AlertState.Pending, 1, start), Observe(90, 1), maintenance);
        Assert.Equal(AlertState.Firing, result.State.State);
        Assert.Equal(AlertEventKind.Fired, result.Event);
        Assert.True(result.DeliverySuppressed);
    }

    [Fact]
    public void RetryScheduleIsBoundedAndDeterministic()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), AlertRetrySchedule.ForAttempt(1));
        Assert.Equal(TimeSpan.FromHours(2), AlertRetrySchedule.ForAttempt(5));
        Assert.Equal(TimeSpan.FromHours(24), AlertRetrySchedule.ForAttempt(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => AlertRetrySchedule.ForAttempt(9));
    }

    [Fact]
    public void ResolvedRuleRefireStartsFreshEpisode()
    {
        var resolved = new AlertRuleState(RuleId, Target, AlertState.Resolved, 0, null, BaseTime, BaseTime.AddMinutes(-1), BaseTime.AddMinutes(-1));
        var result = AlertEvaluator.Evaluate(Rule, resolved, Observe(90, 1));
        Assert.Equal(AlertState.Pending, result.State.State);
        Assert.Null(result.State.FiredUtc);
        Assert.Null(result.State.AcknowledgedUtc);
    }

    [Fact]
    public void ExactOperationReplayIsReadOnlyAndDivergentReplayIsRejected()
    {
        var operation = Guid.NewGuid();
        var observation = new AlertObservation(Target, RuleId, BaseTime, 90, null, "sample", operation);
        var singleConfirmationRule = new AlertRuleDefinition(RuleId, "cpu.high", AlertRuleKind.MetricThreshold, new MetricId("cpu.percent"), AlertComparison.GreaterThanOrEqual, 80, 5, 1, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        var first = AlertEvaluator.Evaluate(singleConfirmationRule, new AlertRuleState(RuleId, Target), observation);
        var replay = AlertEvaluator.Evaluate(singleConfirmationRule, first.State, observation);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.State.LastOperationId, replay.State.LastOperationId);
        var divergent = new AlertObservation(Target, RuleId, BaseTime, 91, null, "sample", operation);
        Assert.Throws<AlertReplayConflictException>(() => AlertEvaluator.Evaluate(singleConfirmationRule, first.State, divergent));
    }

    [Fact]
    public void ARefireUsesANewDeterministicAlertIdentity()
    {
        var singleConfirmationRule = new AlertRuleDefinition(RuleId, "cpu.high", AlertRuleKind.MetricThreshold, new MetricId("cpu.percent"), AlertComparison.GreaterThanOrEqual, 80, 5, 1, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        var one = AlertEvaluator.Evaluate(singleConfirmationRule, new AlertRuleState(RuleId, Target), Observe(90, 0));
        var resolved = AlertEvaluator.Evaluate(singleConfirmationRule, one.State, Observe(70, 1));
        var refired = AlertEvaluator.Evaluate(singleConfirmationRule, resolved.State, Observe(90, 2));
        Assert.NotEqual(one.State.AlertId, refired.State.AlertId);
    }

    [Fact]
    public void EmbeddedCatalogIsDigestableAndRejectsUnapprovedBounds()
    {
        Assert.Matches("^[0-9a-f]{64}$", AlertCatalog.Digest);
        Assert.Contains(AlertCatalog.Entries, entry => entry.Name == "metric.threshold");
        var unapproved = new AlertRuleDefinition(Guid.NewGuid(), "metric.threshold", AlertRuleKind.MetricThreshold, new MetricId("metric.value"), AlertComparison.GreaterThanOrEqual, 2, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        Assert.False(AlertCatalog.IsApproved(unapproved));
    }

    [Fact]
    public void ConfiguredConnectionRulesKeepSupportedSignalAndExecutionBounds()
    {
        AlertRuleDefinition Rule(double threshold=25,double hysteresis=2,int seconds=15,AlertComparison comparison=AlertComparison.GreaterThan) =>
            new(Guid.NewGuid(),"stress.run",AlertRuleKind.MetricThreshold,new MetricId("engine.user_connections"),comparison,threshold,hysteresis,2,TimeSpan.FromSeconds(90),TimeSpan.FromSeconds(seconds));
        Assert.True(AlertCatalog.IsApproved(Rule()));
        Assert.False(AlertCatalog.IsApproved(Rule(threshold:1_000_001)));
        Assert.False(AlertCatalog.IsApproved(Rule(hysteresis:26)));
        Assert.False(AlertCatalog.IsApproved(Rule(seconds:14)));
        Assert.False(AlertCatalog.IsApproved(Rule(comparison:AlertComparison.LessThan)));
    }

    [Fact]
    public void CollectorHealthUnhealthyMatchesAndHealthyClears()
    {
        var healthRule = new AlertRuleDefinition(RuleId, "collector.health", AlertRuleKind.CollectorHealth, null, AlertComparison.LessThan, 1, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        var firing = AlertEvaluator.Evaluate(healthRule, new AlertRuleState(RuleId, Target), new AlertObservation(Target, RuleId, BaseTime, null, false, "collector_stale"));
        Assert.Equal(AlertState.Firing, firing.State.State);
        var cleared = AlertEvaluator.Evaluate(healthRule, firing.State, new AlertObservation(Target, RuleId, BaseTime.AddSeconds(30), null, true, "collector_current"));
        Assert.Equal(AlertState.Resolved, cleared.State.State);
    }

    [Fact]
    public void RefireAllocatesIdentityAtNewPendingEpisode()
    {
        var twoStep = new AlertRuleDefinition(RuleId, "cpu.high", AlertRuleKind.MetricThreshold, new MetricId("cpu.percent"), AlertComparison.GreaterThanOrEqual, 80, 5, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15));
        var oldEpisode = new AlertRuleState(RuleId, Target, AlertState.Resolved, 0, null, BaseTime, null, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AlertEvidenceDigest.Compute(Observe(70, 1)), "cleared", resolvedUtc: BaseTime);
        var pending = AlertEvaluator.Evaluate(twoStep, oldEpisode, Observe(90, 2));
        Assert.Equal(AlertState.Pending, pending.State.State);
        Assert.Null(pending.State.AlertId);
        Assert.Null(pending.State.EpisodeId);
        var firing = AlertEvaluator.Evaluate(twoStep, pending.State, Observe(91, 3));
        Assert.NotNull(firing.State.AlertId);
        Assert.NotNull(firing.State.EpisodeId);
    }

    private static AlertObservation Observe(double value, int minute) => new(Target, RuleId, BaseTime.AddMinutes(minute), value, null, null);
}
