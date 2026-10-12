using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.Alerting;

public static class BlockingAlertObservationFactory
{
    public static AlertObservation Create(AlertRuleDefinition rule, BlockingAlertEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(rule); ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        string reasonCode = evidence.Reason switch
        {
            BlockingAlertEvidenceReason.Current => "current",
            BlockingAlertEvidenceReason.CollectorDisabled => "collector_disabled",
            BlockingAlertEvidenceReason.SourceUnavailable => "source_unavailable",
            BlockingAlertEvidenceReason.SourceStale => "source_stale",
            BlockingAlertEvidenceReason.SourceIncomplete => "source_incomplete",
            BlockingAlertEvidenceReason.SourceInconsistent => "source_inconsistent",
            _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
        };
        string reason = FormattableString.Invariant($"{BlockingAlertRuleContract.SourceKind}|target={evidence.TargetRevision}|reason={reasonCode}|digest={evidence.SourceDigest}");
        Guid operation = AlertOperationIdentity.Compute(evidence.TargetId, rule.RuleId, evidence.ReadAtUtc, reason);
        var observation = new AlertObservation(evidence.TargetId, rule.RuleId, evidence.ReadAtUtc,
            evidence.MaximumWaitSeconds, null, reason, sampleId: operation.ToString("D"), runId: evidence.RunId,
            sourceKind: BlockingAlertRuleContract.SourceKind, sourceCollector: "blocking.current", sourceVersion: "1",
            sourceSchemaVersion: 1, sourceDigest: evidence.SourceDigest);
        BlockingAlertRuleContract.ValidateObservation(rule, observation); return observation;
    }
}
