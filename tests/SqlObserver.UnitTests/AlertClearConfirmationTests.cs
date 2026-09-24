using SqlObserver.Alerting;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class AlertClearConfirmationTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("a439474b-6bca-4edb-942a-15886bb96250"));
    private static readonly Guid RuleId = Guid.Parse("d788b2ad-7e58-415b-b704-64654ef40130");
    private static readonly Guid AlertId = Guid.Parse("55f09468-0d57-4983-8580-89d4e7d3578b");
    private static readonly Guid Episode = Guid.Parse("40c8b474-c07c-44e1-b5da-389e4c0dfc11");
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AlertState.Firing)]
    [InlineData(AlertState.Acknowledged)]
    public void TwoClearsPreserveTheActiveEpisodeUntilFinalConfirmation(AlertState active)
    {
        AlertRuleDefinition rule = Rule();
        AlertRuleState prior = Active(active);
        AlertEvaluationResult first = AlertEvaluator.Evaluate(rule, prior, Observe(74, 1));
        Assert.Equal(active, first.State.State);
        Assert.Equal(1, first.State.ConsecutiveClears);
        Assert.Equal(prior.ConsecutiveMatches, first.State.ConsecutiveMatches);
        Assert.Equal(prior.FirstMatchUtc, first.State.FirstMatchUtc);
        Assert.Equal(AlertId, first.State.AlertId);
        Assert.Equal(Episode, first.State.EpisodeId);
        Assert.Equal(prior.FiredUtc, first.State.FiredUtc);
        Assert.Equal(prior.AcknowledgedUtc, first.State.AcknowledgedUtc);
        Assert.Equal(prior.AcknowledgedBy, first.State.AcknowledgedBy);
        Assert.Null(first.Event);
        AlertEvaluationResult second = AlertEvaluator.Evaluate(rule, first.State, Observe(73, 2));
        Assert.Equal(AlertState.Resolved, second.State.State);
        Assert.Equal(AlertEventKind.Resolved, second.Event);
        Assert.Equal(0, second.State.ConsecutiveClears);
        Assert.Equal(0, second.State.ConsecutiveMatches);
        Assert.Null(second.State.FirstMatchUtc);
        Assert.Equal(AlertId, second.State.AlertId);
        Assert.Equal(Episode, second.State.EpisodeId);
        Assert.Equal(Start.AddMinutes(2), second.State.ResolvedUtc);
        AlertEvaluationResult later = AlertEvaluator.Evaluate(rule, second.State, Observe(72, 3));
        Assert.Null(later.Event);
        Assert.Equal(0, later.State.ConsecutiveClears);
    }

    [Theory]
    [InlineData(AlertComparison.GreaterThan, 75, 76)]
    [InlineData(AlertComparison.GreaterThanOrEqual, 74, 75)]
    [InlineData(AlertComparison.LessThan, 85, 84)]
    [InlineData(AlertComparison.LessThanOrEqual, 86, 85)]
    public void HysteresisBoundaryAndReturningToTheBandResetClearProgress(AlertComparison comparison, double clearValue, double bandValue)
    {
        AlertRuleDefinition rule = Rule(comparison: comparison);
        AlertEvaluationResult first = AlertEvaluator.Evaluate(rule, Active(), Observe(clearValue, 1));
        Assert.Equal(1, first.State.ConsecutiveClears);
        AlertEvaluationResult band = AlertEvaluator.Evaluate(rule, first.State, Observe(bandValue, 2));
        Assert.Equal(AlertState.Firing, band.State.State);
        Assert.Equal(0, band.State.ConsecutiveClears);
        Assert.Null(band.Event);
        AlertEvaluationResult again = AlertEvaluator.Evaluate(rule, band.State, Observe(clearValue, 3));
        Assert.Equal(AlertState.Firing, again.State.State);
        Assert.Equal(1, again.State.ConsecutiveClears);
    }

    [Fact]
    public void ExplicitSingleClearKeepsLegacyBehavior()
    {
        AlertEvaluationResult result = AlertEvaluator.Evaluate(Rule(clearCount: 1), Active(), Observe(74, 1));
        Assert.Equal(AlertState.Resolved, result.State.State);
        Assert.Equal(AlertEventKind.Resolved, result.Event);
        Assert.Equal(0, result.State.ConsecutiveClears);
    }

    [Fact]
    public void ClearConfirmationCountsEvidenceAcrossGapsAndMaintenanceStillSuppressesDelivery()
    {
        AlertEvaluationResult first = AlertEvaluator.Evaluate(Rule(), Active(), Observe(74, 1));
        var maintenance = new MaintenanceWindow(Guid.NewGuid(), Target, Start.AddDays(1), Start.AddDays(2), "planned");
        AlertObservation afterGap = new(Target, RuleId, Start.AddDays(1).AddMinutes(1), 74, null, "observed");
        AlertEvaluationResult final = AlertEvaluator.Evaluate(Rule(), first.State, afterGap, maintenance);
        Assert.Equal(AlertState.Resolved, final.State.State);
        Assert.Equal(AlertEventKind.Resolved, final.Event);
        Assert.True(final.DeliverySuppressed);
    }

    [Fact]
    public void ReplayDoesNotCountAsAnotherClearAndDivergentEvidenceIsRejected()
    {
        AlertObservation observed = Observe(74, 1);
        AlertEvaluationResult first = AlertEvaluator.Evaluate(Rule(), Active(), observed);
        AlertEvaluationResult replay = AlertEvaluator.Evaluate(Rule(), first.State, observed);
        Assert.True(replay.IsReplay);
        Assert.Same(first.State, replay.State);
        Assert.Equal(1, replay.State.ConsecutiveClears);
        Assert.Null(replay.Event);
        var divergent = new AlertObservation(Target, RuleId, observed.ObservedAtUtc, 73, null, observed.Reason, observed.OperationId);
        Assert.Throws<AlertReplayConflictException>(() => AlertEvaluator.Evaluate(Rule(), first.State, divergent));
    }

    [Theory]
    [InlineData(AlertRuleKind.MetricThreshold, AlertState.Firing)]
    [InlineData(AlertRuleKind.MetricThreshold, AlertState.Acknowledged)]
    [InlineData(AlertRuleKind.CollectorHealth, AlertState.Firing)]
    [InlineData(AlertRuleKind.CollectorHealth, AlertState.Acknowledged)]
    public void UnknownEvidenceCannotBeInterpretedAsRecovery(AlertRuleKind kind, AlertState active)
    {
        AlertRuleState prior = Active(active, 1);
        AlertRuleDefinition rule = Rule(kind: kind);
        // A value for the other rule kind must not substitute for missing evidence.
        AlertObservation unknown = new(Target, RuleId, Start.AddMinutes(1), kind == AlertRuleKind.MetricThreshold ? null : 0, kind == AlertRuleKind.CollectorHealth ? null : true, "unavailable");
        Assert.Throws<ArgumentException>(() => AlertEvaluator.Evaluate(rule, prior, unknown));
        Assert.Equal(1, prior.ConsecutiveClears);
        Assert.Equal(active, prior.State);
    }

    [Fact]
    public void DisabledRuleIsNotAHealthyObservation()
        => Assert.Throws<ArgumentException>(() => AlertEvaluator.Evaluate(Rule(enabled: false), Active(), Observe(74, 1)));

    [Fact]
    public void CollectorHealthUsesTwoKnownHealthySamplesAndNoHysteresis()
    {
        AlertRuleDefinition rule = Rule(kind: AlertRuleKind.CollectorHealth);
        AlertObservation Healthy(int minute) => new(Target, RuleId, Start.AddMinutes(minute), null, true, "healthy");
        AlertEvaluationResult first = AlertEvaluator.Evaluate(rule, Active(), Healthy(1));
        Assert.Equal(AlertState.Firing, first.State.State);
        Assert.Equal(1, first.State.ConsecutiveClears);
        AlertEvaluationResult second = AlertEvaluator.Evaluate(rule, first.State, Healthy(2));
        Assert.Equal(AlertState.Resolved, second.State.State);
    }

    [Fact]
    public void ANewEpisodeStartsWithNoClearProgress()
    {
        AlertEvaluationResult cleared = AlertEvaluator.Evaluate(Rule(), Active(clears: 1), Observe(74, 1));
        AlertEvaluationResult pending = AlertEvaluator.Evaluate(Rule(), cleared.State, Observe(90, 2));
        Assert.Equal(AlertState.Pending, pending.State.State);
        Assert.Equal(0, pending.State.ConsecutiveClears);
        AlertEvaluationResult fired = AlertEvaluator.Evaluate(Rule(), pending.State, Observe(90, 3));
        Assert.Equal(AlertState.Firing, fired.State.State);
        Assert.Equal(0, fired.State.ConsecutiveClears);
        Assert.NotEqual(Episode, fired.State.EpisodeId);
        Assert.NotEqual(AlertId, fired.State.AlertId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void ClearCountIsBounded(int count) => Assert.Throws<ArgumentOutOfRangeException>(() => Rule(clearCount: count));

    private static AlertRuleDefinition Rule(int clearCount = 2, AlertComparison comparison = AlertComparison.GreaterThanOrEqual, AlertRuleKind kind = AlertRuleKind.MetricThreshold, bool enabled = true)
        => new(RuleId, kind == AlertRuleKind.CollectorHealth ? "collector.health" : "metric.threshold", kind,
            kind == AlertRuleKind.MetricThreshold ? new MetricId("engine.user_connections") : null,
            kind == AlertRuleKind.CollectorHealth ? AlertComparison.LessThan : comparison,
            kind == AlertRuleKind.CollectorHealth ? 1 : 80, kind == AlertRuleKind.CollectorHealth ? 0 : 5,
            2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), enabled, clearCount);

    private static AlertRuleState Active(AlertState state = AlertState.Firing, int clears = 0)
        => new(RuleId, Target, state, 2, Start.AddMinutes(-2), Start, Start.AddMinutes(-1),
            state == AlertState.Acknowledged ? Start : null, AlertId, Episode,
            episodeStartedUtc: Start.AddMinutes(-1), acknowledgedBy: state == AlertState.Acknowledged ? "operator" : null,
            consecutiveClears: clears);
    private static AlertObservation Observe(double value, int minute) => new(Target, RuleId, Start.AddMinutes(minute), value, null, "observed");
}
