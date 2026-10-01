using System.Globalization;

namespace SqlObserver.Domain.Alerting;

/// <summary>Longest observed current wait, in seconds; null is unknown, never clear.</summary>
public static class BlockingAlertRuleContract
{
    public const string SourceKind = "blocking_duration_v1";

    public static void ValidateObservation(AlertRuleDefinition rule, AlertObservation observation)
    {
        ArgumentNullException.ThrowIfNull(rule); ArgumentNullException.ThrowIfNull(observation);
        string[] parts = observation.Reason?.Split('|') ?? [];
        bool reasonValid = parts.Length == 4 && parts[0] == SourceKind && parts[1].StartsWith("target=", StringComparison.Ordinal) &&
            long.TryParse(parts[1].AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out long revision) && revision > 0 &&
            parts[1] == "target=" + revision.ToString(CultureInfo.InvariantCulture) &&
            parts[2] is "reason=current" or "reason=collector_disabled" or "reason=source_unavailable" or
                "reason=source_stale" or "reason=source_incomplete" or "reason=source_inconsistent" &&
            parts[3] == "digest=" + observation.SourceDigest;
        if (rule.Kind != AlertRuleKind.BlockingDuration || rule.RuleId != observation.RuleId ||
            observation.SourceKind != SourceKind || observation.SourceCollector != "blocking.current" ||
            observation.SourceVersion != "1" || observation.SourceSchemaVersion != 1 ||
            observation.MetricId is not null || observation.CollectorHealthy is not null ||
            observation.SourceDigest is not { Length: 64 } digest || digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !reasonValid || (observation.Value is not null) != (parts[2] == "reason=current") ||
            observation.RunId == Guid.Empty || observation.Value is not null && observation.RunId is null ||
            observation.Value is < 0 or > 9007199254740.991 ||
            observation.SampleId != observation.OperationId.ToString("D") ||
            observation.OperationId != AlertOperationIdentity.Compute(observation.TargetId, observation.RuleId, observation.ObservedAtUtc, observation.Reason) ||
            observation.EvidenceDigest != AlertEvidenceDigest.Compute(observation))
            throw new ArgumentException("Blocking alert observation has an invalid duration or source binding.", nameof(observation));
    }
}
