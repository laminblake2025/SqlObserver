using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.Alerting;

public static class DeadlockAlertObservationFactory
{
    public static AlertObservation Create(AlertRuleDefinition rule, DeadlockAlertEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(rule); ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        string reasonCode = evidence.Reason switch
        {
            DeadlockAlertEvidenceReason.Current => "current",
            DeadlockAlertEvidenceReason.CollectorDisabled => "collector_disabled",
            DeadlockAlertEvidenceReason.SourceUnavailable => "source_unavailable",
            DeadlockAlertEvidenceReason.SourceStale => "source_stale",
            DeadlockAlertEvidenceReason.SourceIncomplete => "source_incomplete",
            DeadlockAlertEvidenceReason.SourceInconsistent => "source_inconsistent",
            _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
        };
        string reason = FormattableString.Invariant($"{DeadlockAlertRuleContract.SourceKind}|target={evidence.TargetRevision}|reason={reasonCode}|digest={evidence.SourceDigest}");
        Guid operation = AlertOperationIdentity.Compute(evidence.TargetId, rule.RuleId, evidence.ReadAtUtc, reason);
        var observation = new AlertObservation(evidence.TargetId, rule.RuleId, evidence.ReadAtUtc,
            evidence.DeadlockCount, null, reason, sampleId: operation.ToString("D"), runId: evidence.RunId,
            sourceKind: DeadlockAlertRuleContract.SourceKind, sourceCollector: "deadlocks.system-health", sourceVersion: "1",
            sourceSchemaVersion: 1, sourceDigest: evidence.SourceDigest);
        DeadlockAlertRuleContract.ValidateObservation(rule, observation); return observation;
    }
}
