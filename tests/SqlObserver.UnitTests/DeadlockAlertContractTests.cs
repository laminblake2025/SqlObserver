using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class DeadlockAlertContractTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AlertRuleDefinition Rule(double threshold = 0, double hysteresis = 0) =>
        new(Guid.NewGuid(), "deadlocks.count", AlertRuleKind.DeadlockCount, null,
            AlertComparison.GreaterThan, threshold, hysteresis, 2, TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(15), clearConfirmationCount: 2);

    private static DeadlockAlertEvidence Evidence(int? count) =>
        new(Target, 3, At, count is null ? null : At.AddMinutes(1), 1, Guid.NewGuid(), At.AddSeconds(-1),
            count is null ? null : At.AddSeconds(-1).AddMinutes(-5), count is null ? null : At.AddSeconds(-1),
            count, count, count is null ? DeadlockAlertEvidenceReason.SourceIncomplete : DeadlockAlertEvidenceReason.Current,
            new string('b', 64), new string('a', 64));

    [Fact]
    public void CompleteZeroAndUnknownRemainDistinctWithCanonicalIdentities()
    {
        AlertRuleDefinition rule = Rule();
        AlertObservation zero = DeadlockAlertObservationFactory.Create(rule, Evidence(0));
        AlertObservation unknown = DeadlockAlertObservationFactory.Create(rule, Evidence(null));
        Assert.Equal(0d, zero.Value);
        Assert.Null(unknown.Value);
        Assert.Contains("reason=source_incomplete", unknown.Reason, StringComparison.Ordinal);
        Assert.NotEqual(zero.OperationId, unknown.OperationId);
        foreach (AlertObservation observation in new[] { zero, unknown })
        {
            Assert.Equal(observation.OperationId.ToString("D"), observation.SampleId);
            Assert.Equal(AlertEvidenceDigest.Compute(observation), observation.EvidenceDigest);
            Assert.Equal("deadlocks.system-health", observation.SourceCollector);
        }
        Assert.Equal(rule.Kind, AlertRuleKindWire.Parse(AlertRuleKindWire.Format(rule.Kind)));
        Assert.True(AlertCatalog.IsApproved(rule));
    }

    [Fact]
    public void ARecomputedDigestCannotAuthorizeForgedCountOrSourceIdentity()
    {
        AlertRuleDefinition rule = Rule();
        AlertObservation canonical = DeadlockAlertObservationFactory.Create(rule, Evidence(2));
        AlertObservation Forge(double? count = 2, string collector = "deadlocks.system-health",
            string? sample = null, Guid? operation = null) =>
            new(Target, rule.RuleId, At, count, null, canonical.Reason, operation,
                sampleId: sample ?? canonical.SampleId, runId: canonical.RunId,
                sourceKind: canonical.SourceKind, sourceCollector: collector, sourceVersion: "1",
                sourceSchemaVersion: 1, sourceDigest: canonical.SourceDigest);

        foreach (AlertObservation forged in new[] { Forge(0.5), Forge(257), Forge(null),
            Forge(collector: "blocking.current"), Forge(sample: Guid.NewGuid().ToString("D")),
            Forge(operation: Guid.NewGuid()) })
            Assert.Throws<ArgumentException>(() => DeadlockAlertRuleContract.ValidateObservation(rule, forged));
    }

    [Fact]
    public void CountPolicyRejectsFractionalAndOutOfRangeSettings()
    {
        foreach ((double threshold, double hysteresis) in new[] { (0.5, 0d), (257d, 0d), (2d, 0.5), (2d, 3d) })
            Assert.Throws<ArgumentException>(() => Rule(threshold, hysteresis));
        Assert.Equal(256d, Rule(256, 256).Threshold);
    }
}
