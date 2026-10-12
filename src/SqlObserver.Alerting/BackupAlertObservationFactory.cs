using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.Alerting;

/// <summary>Maps only a validated captured aggregate; caller-provided verdicts are never accepted.</summary>
public static class BackupAlertObservationFactory
{
    public static AlertObservation Create(AlertRuleDefinition rule, BackupAlertEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(rule);
        IReadOnlyList<BackupAlertSignal> signals = BackupAlertSignalProjection.Build(evidence);
        BackupAlertSignalKind kind = rule.Kind switch
        {
            AlertRuleKind.BackupFullPolicy => BackupAlertSignalKind.FullPolicy,
            AlertRuleKind.BackupLogPolicy => BackupAlertSignalKind.LogPolicy,
            AlertRuleKind.BackupObservedFaults => BackupAlertSignalKind.ObservedFaults,
            _ => throw new ArgumentException("A backup alert rule is required.", nameof(rule)),
        };
        BackupAlertSignal signal = signals.Single(candidate => candidate.Kind == kind);
        string reason = FormattableString.Invariant($"{BackupAlertRuleContract.SourceKind(rule.Kind)}|target={evidence.TargetRevision}|state={(int)signal.State}|reason={(int)signal.Reason}|digest={evidence.SourceDigest ?? "none"}");
        Guid sample = AlertOperationIdentity.Compute(evidence.TargetId, rule.RuleId, evidence.ReadAtUtc, reason);
        var observation = new AlertObservation(evidence.TargetId, rule.RuleId, evidence.ReadAtUtc,
            signal.AffectedDatabases, null, reason, sampleId: sample.ToString("D"),
            runId: evidence.Summary?.Context.RunId, sourceKind: BackupAlertRuleContract.SourceKind(rule.Kind),
            sourceCollector: "backups.status", sourceVersion: "2", sourceSchemaVersion: 2, sourceDigest: evidence.SourceDigest);
        BackupAlertRuleContract.ValidateObservation(rule, observation);
        return observation;
    }
}
