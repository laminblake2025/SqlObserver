using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class DeadlockAlertTransitionTests
{
    [Fact]
    public void UnknownInterruptsFireAndClearConfirmationsAndPreservesAnAcknowledgedEpisode()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.count", AlertRuleKind.DeadlockCount, null,
            AlertComparison.GreaterThan, 2, 1, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        AlertObservation Observe(int seconds, int? count)
        {
            DateTimeOffset now = at.AddSeconds(seconds);
            return DeadlockAlertObservationFactory.Create(rule,
                new(target, 1, now, count is null ? null : now.AddMinutes(1), 1, Guid.NewGuid(), now.AddSeconds(-1),
                    count is null ? null : now.AddMinutes(-5).AddSeconds(-1), count is null ? null : now.AddSeconds(-1),
                    count, count, count is null ? DeadlockAlertEvidenceReason.SourceIncomplete : DeadlockAlertEvidenceReason.Current,
                    new string('b', 64), new string('a', 64)));
        }
        AlertEvaluationResult Next(AlertRuleState prior, int seconds, int? count) =>
            AlertEvaluator.Evaluate(rule, prior, Observe(seconds, count));

        AlertEvaluationResult pending = Next(new(rule.RuleId, target), 0, 3);
        Assert.Equal(AlertState.Pending, pending.State.State);
        AlertEvaluationResult unknown = Next(pending.State, 15, null);
        Assert.Equal(AlertState.Normal, unknown.State.State);
        Assert.Equal(0, unknown.State.ConsecutiveMatches);
        pending = Next(unknown.State, 30, 3);
        AlertObservation firingObservation = Observe(45, 3);
        AlertEvaluationResult firing = AlertEvaluator.Evaluate(rule, pending.State, firingObservation);
        Assert.Equal(AlertEventKind.Fired, firing.Event);
        Assert.True(AlertEvaluator.Evaluate(rule, firing.State, firingObservation).IsReplay);
        Assert.Throws<ArgumentException>(() => Next(firing.State, 30, null));
        AlertEvaluationResult held = Next(firing.State, 60, 2);
        Assert.Equal(0, held.State.ConsecutiveClears); // Active threshold is 2 - 1.
        AlertEvaluationResult clear = Next(held.State, 75, 1);
        Assert.Equal(1, clear.State.ConsecutiveClears);
        var acknowledged = new AlertRuleState(rule.RuleId, target, AlertState.Acknowledged,
            clear.State.ConsecutiveMatches, clear.State.FirstMatchUtc, clear.State.LastObservedUtc,
            clear.State.FiredUtc, at.AddSeconds(76), clear.State.AlertId, clear.State.EpisodeId,
            clear.State.LastOperationId, clear.State.EvidenceDigest, clear.State.Reason,
            episodeStartedUtc: clear.State.EpisodeStartedUtc, acknowledgedBy: "operator",
            revision: clear.State.Revision + 1, consecutiveClears: 1);
        unknown = Next(acknowledged, 90, null);
        Assert.Equal(AlertState.Acknowledged, unknown.State.State);
        Assert.Equal(acknowledged.AlertId, unknown.State.AlertId);
        Assert.Equal(acknowledged.EpisodeId, unknown.State.EpisodeId);
        Assert.Equal("operator", unknown.State.AcknowledgedBy);
        Assert.Equal(0, unknown.State.ConsecutiveClears);
        Assert.Null(unknown.Event);
        clear = Next(unknown.State, 105, 0);
        Assert.Equal(1, clear.State.ConsecutiveClears);
        clear = Next(clear.State, 420, 0); // A gap restarts clear confirmation.
        Assert.Equal(1, clear.State.ConsecutiveClears);
        AlertEvaluationResult resolved = Next(clear.State, 435, 0);
        Assert.Equal(AlertState.Resolved, resolved.State.State);
        Assert.Equal(AlertEventKind.Resolved, resolved.Event);
        pending = Next(resolved.State, 450, 3);
        AlertEvaluationResult refired = Next(pending.State, 465, 3);
        Assert.Equal(AlertEventKind.Fired, refired.Event);
        Assert.NotEqual(firing.State.AlertId, refired.State.AlertId);
        Assert.Null(refired.State.AcknowledgedUtc);
    }
}
