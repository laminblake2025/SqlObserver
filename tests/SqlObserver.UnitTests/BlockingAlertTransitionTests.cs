using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BlockingAlertTransitionTests
{
    private static readonly MonitoredInstanceId Target=new(Guid.NewGuid());
    private static readonly DateTimeOffset At=new(2026,9,30,12,0,0,TimeSpan.Zero);
    private static AlertRuleDefinition Rule()=>new(Guid.NewGuid(),"blocking.duration",AlertRuleKind.BlockingDuration,null,
        AlertComparison.GreaterThan,60,10,2,TimeSpan.FromMinutes(5),TimeSpan.FromSeconds(5),clearConfirmationCount:2);
    private static AlertObservation Observe(AlertRuleDefinition rule,int seconds,long? milliseconds)
    {
        DateTimeOffset now=At.AddSeconds(seconds);
        return BlockingAlertObservationFactory.Create(rule,new(Target,1,now,milliseconds is null?null:now.AddMinutes(1),1,
            Guid.NewGuid(),now.AddSeconds(-1),milliseconds is >0?now.AddSeconds(-1):null,
            milliseconds is null?null:milliseconds>0?1:0,milliseconds,
            milliseconds is null?BlockingAlertEvidenceReason.SourceIncomplete:BlockingAlertEvidenceReason.Current,
            new string('b',64),new string('a',64)));
    }
    [Fact]
    public void UnknownInterruptsBothConfirmationStreaksAndPreservesAnAcknowledgedEpisode()
    {
        AlertRuleDefinition rule=Rule(); AlertRuleState prior=new(rule.RuleId,Target);
        AlertEvaluationResult pending=AlertEvaluator.Evaluate(rule,prior,Observe(rule,0,65000));
        Assert.Equal(AlertState.Pending,pending.State.State);
        AlertEvaluationResult unknown=AlertEvaluator.Evaluate(rule,pending.State,Observe(rule,5,null));
        Assert.Equal(AlertState.Normal,unknown.State.State); Assert.Equal(0,unknown.State.ConsecutiveMatches);
        pending=AlertEvaluator.Evaluate(rule,unknown.State,Observe(rule,10,65000));
        AlertEvaluationResult firing=AlertEvaluator.Evaluate(rule,pending.State,Observe(rule,15,65000));
        Assert.Equal(AlertState.Firing,firing.State.State); Assert.Equal(AlertEventKind.Fired,firing.Event);
        AlertEvaluationResult clear=AlertEvaluator.Evaluate(rule,firing.State,Observe(rule,20,50000));
        Assert.Equal(1,clear.State.ConsecutiveClears); Assert.Equal(AlertState.Firing,clear.State.State);
        var acknowledged=new AlertRuleState(rule.RuleId,Target,AlertState.Acknowledged,clear.State.ConsecutiveMatches,
            clear.State.FirstMatchUtc,clear.State.LastObservedUtc,clear.State.FiredUtc,At.AddSeconds(21),clear.State.AlertId,
            clear.State.EpisodeId,clear.State.LastOperationId,clear.State.EvidenceDigest,clear.State.Reason,
            episodeStartedUtc:clear.State.EpisodeStartedUtc,acknowledgedBy:"operator",revision:clear.State.Revision+1,
            consecutiveClears:1);
        AlertEvaluationResult held=AlertEvaluator.Evaluate(rule,acknowledged,Observe(rule,25,null));
        Assert.Equal(AlertState.Acknowledged,held.State.State); Assert.Equal(acknowledged.AlertId,held.State.AlertId);
        Assert.Equal(acknowledged.EpisodeId,held.State.EpisodeId); Assert.Equal("operator",held.State.AcknowledgedBy);
        Assert.Equal(0,held.State.ConsecutiveClears); Assert.Null(held.Event); Assert.Null(held.State.LastValue);
        clear=AlertEvaluator.Evaluate(rule,held.State,Observe(rule,30,0)); Assert.Equal(1,clear.State.ConsecutiveClears);
        AlertEvaluationResult resolved=AlertEvaluator.Evaluate(rule,clear.State,Observe(rule,35,0));
        Assert.Equal(AlertState.Resolved,resolved.State.State); Assert.Equal(AlertEventKind.Resolved,resolved.Event);
    }
    [Fact]
    public void ReplayIsReadOnlyAndRegressedTimeIsRejected()
    {
        AlertRuleDefinition rule=Rule(); AlertObservation observation=Observe(rule,0,65001);
        AlertEvaluationResult first=AlertEvaluator.Evaluate(rule,new(rule.RuleId,Target),observation);
        Assert.True(AlertEvaluator.Evaluate(rule,first.State,observation).IsReplay);
        Assert.Throws<ArgumentException>(()=>AlertEvaluator.Evaluate(rule,first.State,Observe(rule,-1,null)));
        var divergent=new AlertObservation(Target,rule.RuleId,observation.ObservedAtUtc,66,null,observation.Reason,
            observation.OperationId,sampleId:observation.SampleId,runId:observation.RunId,
            sourceKind:observation.SourceKind,sourceCollector:observation.SourceCollector,sourceVersion:"1",
            sourceSchemaVersion:1,sourceDigest:observation.SourceDigest);
        Assert.Throws<AlertReplayConflictException>(()=>AlertEvaluator.Evaluate(rule,first.State,divergent));
    }
    [Theory]
    [InlineData(-1,0,5,300)] [InlineData(86401,0,5,300)] [InlineData(60,61,5,300)]
    [InlineData(60,10,4,300)] [InlineData(60,10,61,300)] [InlineData(60,10,5,4)]
    public void InvalidDurationPolicyIsRejected(double threshold,double hysteresis,int interval,int window)
        =>Assert.Throws<ArgumentException>(()=>new AlertRuleDefinition(Guid.NewGuid(),"blocking.duration",AlertRuleKind.BlockingDuration,
            null,AlertComparison.GreaterThan,threshold,hysteresis,2,TimeSpan.FromSeconds(window),TimeSpan.FromSeconds(interval),clearConfirmationCount:2));
}
