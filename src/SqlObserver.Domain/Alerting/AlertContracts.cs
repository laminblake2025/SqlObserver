using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Alerting;

public enum AlertRuleKind { MetricThreshold = 1, CollectorHealth = 2 }
public enum AlertComparison { GreaterThan = 1, GreaterThanOrEqual = 2, LessThan = 3, LessThanOrEqual = 4, Equal = 5 }
public enum AlertState { Normal = 1, Pending = 2, Firing = 3, Acknowledged = 4, Resolved = 5 }
public enum AlertEventKind { Fired = 1, Resolved = 2, Acknowledged = 3 }

public static class AlertIdentifier
{
    public static bool TryParseRfc4122(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty &&
        string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
    public static Guid ParseRfc4122(string value) =>
        TryParseRfc4122(value, out Guid id) ? id : throw new FormatException("Identifier must be a non-empty RFC 4122 UUID in canonical D form.");
}

/// <summary>Immutable, product-owned alert rule. It contains no target connection material.</summary>
public sealed class AlertRuleDefinition
{
    public const int MaximumNameLength = 128;
    public const int MaximumDescriptionLength = 512;

    public AlertRuleDefinition(
        Guid ruleId,
        string name,
        AlertRuleKind kind,
        MetricId? metricId,
        AlertComparison comparison,
        double threshold,
        double hysteresis,
        int confirmationCount,
        TimeSpan confirmationWindow,
        TimeSpan evaluationInterval,
        bool enabled = true,
        int clearConfirmationCount = 1)
    {
        if (ruleId == Guid.Empty) throw new ArgumentException("Rule identity is required.", nameof(ruleId));
        Name = DomainValidation.RequireAsciiToken(name, nameof(name), MaximumNameLength,
            static c => DomainValidation.IsAsciiLetter(c) || DomainValidation.IsAsciiDigit(c) || c is '.' or '_' or '-', true);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(comparison)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == AlertRuleKind.MetricThreshold && metricId is null) throw new ArgumentException("Metric rules require a metric identifier.", nameof(metricId));
        if (kind == AlertRuleKind.CollectorHealth && metricId is not null) throw new ArgumentException("Collector-health rules cannot carry a metric identifier.", nameof(metricId));
        if (!double.IsFinite(threshold) || !double.IsFinite(hysteresis) || hysteresis < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (confirmationCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(confirmationCount));
        if (clearConfirmationCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(clearConfirmationCount));
        if (confirmationWindow < TimeSpan.Zero || confirmationWindow > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(confirmationWindow));
        if (evaluationInterval < TimeSpan.FromSeconds(1) || evaluationInterval > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(evaluationInterval));
        RuleId = ruleId; Kind = kind; MetricId = metricId; Comparison = comparison; Threshold = threshold; Hysteresis = hysteresis;
        ConfirmationCount = confirmationCount; ConfirmationWindow = confirmationWindow; EvaluationInterval = evaluationInterval; Enabled = enabled;
        ClearConfirmationCount = clearConfirmationCount;
    }

    public Guid RuleId { get; }
    public string Name { get; }
    public AlertRuleKind Kind { get; }
    public MetricId? MetricId { get; }
    public AlertComparison Comparison { get; }
    public double Threshold { get; }
    public double Hysteresis { get; }
    public int ConfirmationCount { get; }
    public int ClearConfirmationCount { get; }
    public TimeSpan ConfirmationWindow { get; }
    public TimeSpan EvaluationInterval { get; }
    public bool Enabled { get; }
}

public sealed record AlertObservation
{
    public AlertObservation(MonitoredInstanceId targetId, Guid ruleId, DateTimeOffset observedAtUtc, double? value, bool? collectorHealthy, string? reason, Guid? operationId = null, string? evidenceDigest = null, string? sampleId = null, Guid? runId = null, string? sourceKind = null, string? metricId = null, string? sourceCollector = null, string? sourceVersion = null, int? sourceSchemaVersion = null, string? sourceDigest = null)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        if (ruleId == Guid.Empty) throw new ArgumentException("Rule identity is required.", nameof(ruleId));
        if (value is not null && !double.IsFinite(value.Value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (operationId == Guid.Empty) throw new ArgumentException("Operation identity cannot be empty.", nameof(operationId));
        TargetId = targetId; RuleId = ruleId; ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc)); Value = value; CollectorHealthy = collectorHealthy; Reason = reason; SampleId = sampleId; RunId = runId; SourceKind = sourceKind; MetricId = metricId; SourceCollector = sourceCollector; SourceVersion = sourceVersion; SourceSchemaVersion = sourceSchemaVersion; SourceDigest = sourceDigest;
        OperationId = operationId ?? AlertOperationIdentity.Compute(TargetId, RuleId, ObservedAtUtc, Reason);
        EvidenceDigest = evidenceDigest is null ? AlertEvidenceDigest.Compute(this) : AlertEvidenceDigest.Validate(evidenceDigest);
    }
    public MonitoredInstanceId TargetId { get; }
    public Guid RuleId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public double? Value { get; }
    public bool? CollectorHealthy { get; }
    public string? Reason { get; }
    public string? SampleId { get; }
    public Guid? RunId { get; }
    public string? SourceKind { get; }
    public string? MetricId { get; }
    public string? SourceCollector { get; }
    public string? SourceVersion { get; }
    public int? SourceSchemaVersion { get; }
    public string? SourceDigest { get; }
    public Guid OperationId { get; }
    public string EvidenceDigest { get; }
}

public sealed class AlertRuleState
{
    public AlertRuleState(Guid ruleId, MonitoredInstanceId targetId, AlertState state = AlertState.Normal,
        int consecutiveMatches = 0, DateTimeOffset? firstMatchUtc = null, DateTimeOffset? lastObservedUtc = null,
        DateTimeOffset? firedUtc = null, DateTimeOffset? acknowledgedUtc = null,
        Guid? alertId = null, Guid? episodeId = null, Guid? lastOperationId = null, string? evidenceDigest = null, string? reason = null, string? lastReason = null, bool deliverySuppressed = false, DateTimeOffset? resolvedUtc = null, DateTimeOffset? episodeStartedUtc = null, string? acknowledgedBy = null, long revision = 1, double? lastValue = null, int consecutiveClears = 0)
    {
        if (ruleId == Guid.Empty) throw new ArgumentException("Rule identity is required.", nameof(ruleId));
        ArgumentNullException.ThrowIfNull(targetId);
        if (!Enum.IsDefined(state) || consecutiveMatches < 0 || revision <= 0) throw new ArgumentOutOfRangeException(nameof(state));
        if (consecutiveClears is < 0 or > 100 || consecutiveClears > 0 && state is not (AlertState.Firing or AlertState.Acknowledged))
            throw new ArgumentOutOfRangeException(nameof(consecutiveClears));
        if (alertId == Guid.Empty || episodeId == Guid.Empty || lastOperationId == Guid.Empty) throw new ArgumentException("Alert identifiers cannot be empty.", nameof(alertId));
        RuleId = ruleId; TargetId = targetId; State = state; ConsecutiveMatches = consecutiveMatches; Revision = revision;
        ConsecutiveClears = consecutiveClears;
        FirstMatchUtc = firstMatchUtc; LastObservedUtc = lastObservedUtc; FiredUtc = firedUtc; AcknowledgedUtc = acknowledgedUtc;
        LastValue = lastValue;
        AlertId = alertId; EpisodeId = episodeId; LastOperationId = lastOperationId; EvidenceDigest = evidenceDigest is null ? null : AlertEvidenceDigest.Validate(evidenceDigest); Reason = reason; LastReason = lastReason ?? reason; DeliverySuppressed = deliverySuppressed; ResolvedUtc = resolvedUtc; EpisodeStartedUtc = episodeStartedUtc; AcknowledgedBy = acknowledgedBy;
    }
    public Guid RuleId { get; }
    public MonitoredInstanceId TargetId { get; }
    public AlertState State { get; }
    public int ConsecutiveMatches { get; }
    public int ConsecutiveClears { get; }
    public DateTimeOffset? FirstMatchUtc { get; }
    public DateTimeOffset? LastObservedUtc { get; }
    public DateTimeOffset? FiredUtc { get; }
    public DateTimeOffset? AcknowledgedUtc { get; }
    public double? LastValue { get; }
    public Guid? AlertId { get; }
    public Guid? EpisodeId { get; }
    public Guid? LastOperationId { get; }
    public string? EvidenceDigest { get; }
    public string? Reason { get; }
    public string? LastReason { get; }
    public bool DeliverySuppressed { get; }
    public DateTimeOffset? ResolvedUtc { get; }
    public DateTimeOffset? EpisodeStartedUtc { get; }
    public string? AcknowledgedBy { get; }
    public long Revision { get; }
}

public sealed record AlertEvaluationResult(AlertRuleState State, AlertEventKind? Event, bool DeliverySuppressed, string Reason, bool IsReplay = false);

public sealed class AlertReplayConflictException(string message) : InvalidOperationException(message);

public static class AlertEvidenceDigest
{
    public static string Compute(AlertObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        static string Marker(string? value) => value is null ? "<null>" : value;
        string canonical = string.Join("|", observation.TargetId.Value.ToString("D"), observation.RuleId.ToString("D"), Marker(observation.SourceKind), Marker(observation.MetricId), Marker(observation.SourceCollector), Marker(observation.SourceVersion), observation.SourceSchemaVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<null>", Marker(observation.SourceDigest), observation.ObservedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture), Marker(observation.SampleId), observation.RunId?.ToString("D") ?? "<null>", observation.Value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "<null>", observation.CollectorHealthy?.ToString().ToLowerInvariant() ?? "<null>", Marker(observation.Reason));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    public static string Validate(string digest)
    {
        if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Evidence digest must be a SHA-256 hex value.", nameof(digest));
        return digest.ToLowerInvariant();
    }
}

public static class AlertOperationIdentity
{
    public static Guid Compute(MonitoredInstanceId targetId, Guid ruleId, DateTimeOffset observedAtUtc) => Compute(targetId, ruleId, observedAtUtc, null);
    public static Guid Compute(MonitoredInstanceId targetId, Guid ruleId, DateTimeOffset observedAtUtc, string? sourceIdentity)
    {
        string canonical = $"alert-evaluation|{targetId.Value:D}|{ruleId:D}|{observedAtUtc.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'}|{sourceIdentity ?? string.Empty}";
        string hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..32];
        return Guid.Parse($"{hex[..12]}5{hex[13..16]}8{hex[17..32]}");
    }
}

public static class AlertOperationId
{
    public static Guid Compute(MonitoredInstanceId targetId, Guid ruleId, DateTimeOffset observedAtUtc) => AlertOperationIdentity.Compute(targetId, ruleId, observedAtUtc);
}

public sealed class MaintenanceWindow
{
    public MaintenanceWindow(Guid id, MonitoredInstanceId targetId, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, string reason)
    {
        if (id == Guid.Empty) throw new ArgumentException("Maintenance identity is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(targetId);
        startsAtUtc = DomainValidation.RequireUtcMicrosecondAligned(startsAtUtc, nameof(startsAtUtc));
        endsAtUtc = DomainValidation.RequireUtcMicrosecondAligned(endsAtUtc, nameof(endsAtUtc));
        if (endsAtUtc <= startsAtUtc || endsAtUtc - startsAtUtc > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(endsAtUtc));
        Reason = DomainValidation.RequireSafeText(reason, nameof(reason), 512);
        Id = id; TargetId = targetId; StartsAtUtc = startsAtUtc; EndsAtUtc = endsAtUtc;
    }
    public Guid Id { get; }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset StartsAtUtc { get; }
    public DateTimeOffset EndsAtUtc { get; }
    public string Reason { get; }
    public bool Contains(DateTimeOffset instantUtc) => instantUtc >= StartsAtUtc && instantUtc < EndsAtUtc;
}

public sealed record AlertActiveDto(Guid AlertId, Guid RuleId, MonitoredInstanceId TargetId, string RuleName,
    AlertState State, DateTimeOffset FirstObservedUtc, DateTimeOffset? FiredUtc, DateTimeOffset? AcknowledgedUtc,
    double? Value, string? Reason, bool DeliverySuppressed);
