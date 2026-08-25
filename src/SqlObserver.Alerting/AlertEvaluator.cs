using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Alerting;

/// <summary>Pure deterministic state machine for confirmation, hysteresis, acknowledgement and resolution.</summary>
public static class AlertEvaluator
{
    public static AlertEvaluationResult Evaluate(
        AlertRuleDefinition rule,
        AlertRuleState prior,
        AlertObservation observation,
        MaintenanceWindow? maintenance = null)
    {
        ArgumentNullException.ThrowIfNull(rule); ArgumentNullException.ThrowIfNull(prior); ArgumentNullException.ThrowIfNull(observation);
        if (prior.RuleId != rule.RuleId || prior.TargetId.Value != observation.TargetId.Value || observation.RuleId != rule.RuleId)
            throw new ArgumentException("Rule and target identities must match.");
        // A replay is a read-only operation.  A reused operation identity is only
        // valid when its evidence digest is byte-for-byte identical.
        if (prior.LastOperationId == observation.OperationId)
        {
            if (!string.Equals(prior.EvidenceDigest, observation.EvidenceDigest, StringComparison.Ordinal))
                throw new AlertReplayConflictException("The operation identity was reused with divergent evidence.");
            return new AlertEvaluationResult(prior, null, prior.DeliverySuppressed, prior.Reason ?? "replayed", true);
        }
        bool matches = IsMatch(rule, observation, prior.State);
        DateTimeOffset now = observation.ObservedAtUtc;
        bool inMaintenance = maintenance is not null && maintenance.TargetId.Value == observation.TargetId.Value && maintenance.Contains(now);
        int count = matches ? Math.Min(rule.ConfirmationCount, prior.ConsecutiveMatches + 1) : 0;
        DateTimeOffset? first = matches ? prior.FirstMatchUtc ?? now : null;
        if (matches && first is not null && now - first > rule.ConfirmationWindow) { count = 1; first = now; }
        AlertState next = prior.State;
        AlertEventKind? evt = null;
        if (!matches)
        {
            if (prior.State is AlertState.Firing or AlertState.Acknowledged)
            { next = AlertState.Resolved; evt = AlertEventKind.Resolved; }
            else next = AlertState.Normal;
        }
        else if (prior.State is AlertState.Normal or AlertState.Resolved)
        {
            next = count >= rule.ConfirmationCount ? AlertState.Firing : AlertState.Pending;
            if (next == AlertState.Firing) evt = AlertEventKind.Fired;
        }
        else if (prior.State == AlertState.Pending && count >= rule.ConfirmationCount)
        { next = AlertState.Firing; evt = AlertEventKind.Fired; }
        else if (prior.State is AlertState.Firing or AlertState.Acknowledged) next = prior.State;
        bool refire = next == AlertState.Firing && prior.State == AlertState.Resolved;
        bool freshEpisode = prior.State == AlertState.Resolved;
        Guid? alertId = prior.AlertId;
        Guid? episodeId = prior.EpisodeId;
        if (next == AlertState.Pending)
        {
            alertId = null;
            episodeId = null;
        }
        // Pending is confirmation state only.  Alert/episode identities are
        // allocated at the durable Firing transition, so a later confirmation
        // cannot strand a notification identity that never fired.
        if (next == AlertState.Firing && (freshEpisode || alertId is null))
        {
            episodeId = observation.OperationId;
            alertId = DeterministicAlertId(observation.TargetId, rule.RuleId, episodeId.Value);
        }
        var state = new AlertRuleState(rule.RuleId, observation.TargetId, next, count, first, now,
            next == AlertState.Firing ? (freshEpisode ? now : prior.FiredUtc ?? now) : freshEpisode ? null : prior.FiredUtc,
            freshEpisode ? null : prior.AcknowledgedUtc, alertId, episodeId, observation.OperationId, observation.EvidenceDigest, observation.Reason,
            lastReason: observation.Reason,
            deliverySuppressed: inMaintenance,
            resolvedUtc: freshEpisode ? null : next == AlertState.Resolved ? now : prior.ResolvedUtc,
            episodeStartedUtc: next == AlertState.Pending ? null : next == AlertState.Firing && (freshEpisode || prior.EpisodeStartedUtc is null) ? now : prior.EpisodeStartedUtc,
            acknowledgedBy: freshEpisode ? null : prior.AcknowledgedBy,
            revision: prior.Revision + 1,
            lastValue: observation.Value);
        return new AlertEvaluationResult(state, evt, inMaintenance, inMaintenance ? "maintenance" : "evaluated");
    }

    private static Guid DeterministicAlertId(MonitoredInstanceId target, Guid ruleId, Guid episodeId)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"alert|{target.Value:D}|{ruleId:D}|{episodeId:D}"));
        string hex = Convert.ToHexString(digest).ToLowerInvariant()[..32];
        return Guid.Parse($"{hex[..12]}5{hex[13..16]}8{hex[17..32]}");
    }

    public static bool IsMatch(AlertRuleDefinition rule, AlertObservation observation, AlertState state)
    {
        if (!rule.Enabled) return false;
        double? value = rule.Kind == AlertRuleKind.MetricThreshold ? observation.Value : observation.CollectorHealthy is null ? null : observation.CollectorHealthy.Value ? 1d : 0d;
        if (value is null) return false;
        double threshold = rule.Threshold;
        if (state is AlertState.Firing or AlertState.Acknowledged)
            threshold = rule.Comparison is AlertComparison.GreaterThan or AlertComparison.GreaterThanOrEqual ? threshold - rule.Hysteresis : threshold + rule.Hysteresis;
        return rule.Comparison switch
        {
            AlertComparison.GreaterThan => value > threshold,
            AlertComparison.GreaterThanOrEqual => value >= threshold,
            AlertComparison.LessThan => value < threshold,
            AlertComparison.LessThanOrEqual => value <= threshold,
            AlertComparison.Equal => Math.Abs(value.Value - threshold) <= rule.Hysteresis,
            _ => false,
        };
    }
}

public static class AlertRetrySchedule
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24)];
    public static TimeSpan ForAttempt(int attempt) => attempt is < 1 or > 8 ? throw new ArgumentOutOfRangeException(nameof(attempt)) : Delays[attempt - 1];
}
