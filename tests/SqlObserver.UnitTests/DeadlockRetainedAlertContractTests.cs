using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.UnitTests;

public sealed partial class DeadlockAlertContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(null)]
    public void RetainedEvidenceCarriesV2ProvenanceAndPreservesCountSemantics(int? count)
    {
        AlertRuleDefinition rule = Rule();
        DeadlockAlertEvidence evidence = Evidence(count) with
        { CollectorVersion = 2, OutputSchemaVersion = 2, CheckpointDigest = new string('c', 64), SourceDigest = new string('d', 64) };
        AlertObservation observation = DeadlockAlertObservationFactory.Create(rule, evidence);
        Assert.Equal(count, observation.Value);
        Assert.Equal("2", observation.SourceVersion);
        Assert.Equal(2, observation.SourceSchemaVersion);
        Assert.Equal(DeadlockAlertRuleContract.SourceKind, observation.SourceKind);
        Assert.Equal(AlertEvidenceDigest.Compute(observation), observation.EvidenceDigest);
        DeadlockAlertRuleContract.ValidateObservation(rule, observation);
    }

    [Fact]
    public void CurrentV2EvidenceRequiresAReceiptDigestAndRejectsMixedVersions()
    {
        DeadlockAlertEvidence current = Evidence(1) with { CollectorVersion = 2, OutputSchemaVersion = 2, CheckpointDigest = new string('c', 64) };
        current.Validate();
        Assert.Throws<InvalidDataException>(() => (current with { CheckpointDigest = null }).Validate());
        Assert.Throws<InvalidDataException>(() => (current with { CheckpointDigest = new string('C', 64) }).Validate());
        Assert.Throws<InvalidDataException>(() => (current with { CollectorVersion = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (current with { OutputSchemaVersion = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Evidence(1) with { CheckpointDigest = new string('c', 64) }).Validate());
        (Evidence(null) with { CollectorVersion = 2, OutputSchemaVersion = 2 }).Validate();
    }

    [Fact]
    public void UnknownRetainedEvidenceCannotResolveAFiringEpisode()
    {
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.count", AlertRuleKind.DeadlockCount, null,
            AlertComparison.GreaterThan, 0, 0, 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        AlertObservation Observe(int seconds, int? count)
        {
            DateTimeOffset now = At.AddSeconds(seconds);
            var evidence = new DeadlockAlertEvidence(Target, 3, now, count is null ? null : now.AddMinutes(1), 1,
                Guid.NewGuid(), now.AddSeconds(-1), count is null ? null : now.AddMinutes(-5).AddSeconds(-1),
                count is null ? null : now.AddSeconds(-1), count, count,
                count is null ? DeadlockAlertEvidenceReason.SourceIncomplete : DeadlockAlertEvidenceReason.Current,
                new string('b', 64), new string('a', 64), 2, 2, new string('c', 64));
            return DeadlockAlertObservationFactory.Create(rule, evidence);
        }
        AlertEvaluationResult firing = AlertEvaluator.Evaluate(rule, new(rule.RuleId, Target), Observe(0, 1));
        Assert.Equal(AlertEventKind.Fired, firing.Event);
        AlertEvaluationResult unknown = AlertEvaluator.Evaluate(rule, firing.State, Observe(15, null));
        Assert.Equal(AlertState.Firing, unknown.State.State);
        Assert.Equal(firing.State.EpisodeId, unknown.State.EpisodeId);
        Assert.Equal(0, unknown.State.ConsecutiveClears);
        AlertEvaluationResult firstZero = AlertEvaluator.Evaluate(rule, unknown.State, Observe(30, 0));
        Assert.Equal(AlertState.Firing, firstZero.State.State);
        AlertEvaluationResult resolved = AlertEvaluator.Evaluate(rule, firstZero.State, Observe(45, 0));
        Assert.Equal(AlertState.Resolved, resolved.State.State);
        Assert.Equal(AlertEventKind.Resolved, resolved.Event);
    }
}
