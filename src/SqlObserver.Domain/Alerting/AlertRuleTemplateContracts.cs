using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Alerting;

/// <summary>Rule configuration only. Source approval and target identity are supplied by the server.</summary>
public sealed record AlertRuleTemplateValues
{
    public AlertRuleTemplateValues(string name, string kind, string? metricId, string comparison,
        double threshold, double hysteresis, int confirmationCount, int confirmationSeconds,
        int evaluationSeconds, int clearConfirmationCount, bool enabled)
    {
        if (confirmationSeconds is < 0 or > 604800 || evaluationSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(confirmationSeconds));
        _ = new AlertRuleDefinition(Guid.NewGuid(), name, AlertRuleKindWire.Parse(kind),
            metricId is null ? null : new MetricId(metricId), ParseComparison(comparison), threshold,
            hysteresis, confirmationCount, TimeSpan.FromSeconds(confirmationSeconds),
            TimeSpan.FromSeconds(evaluationSeconds), enabled, clearConfirmationCount);
        Name = name; Kind = kind; MetricId = metricId; Comparison = comparison; Threshold = threshold;
        Hysteresis = hysteresis; ConfirmationCount = confirmationCount; ConfirmationSeconds = confirmationSeconds;
        EvaluationSeconds = evaluationSeconds; ClearConfirmationCount = clearConfirmationCount; Enabled = enabled;
    }
    public string Name { get; }
    public string Kind { get; }
    public string? MetricId { get; }
    public string Comparison { get; }
    public double Threshold { get; }
    public double Hysteresis { get; }
    public int ConfirmationCount { get; }
    public int ConfirmationSeconds { get; }
    public int EvaluationSeconds { get; }
    public int ClearConfirmationCount { get; }
    public bool Enabled { get; }
    public AlertRuleDefinition ToRule(Guid ruleId) => new(ruleId, Name, AlertRuleKindWire.Parse(Kind),
        MetricId is null ? null : new MetricId(MetricId), ParseComparison(Comparison), Threshold, Hysteresis,
        ConfirmationCount, TimeSpan.FromSeconds(ConfirmationSeconds), TimeSpan.FromSeconds(EvaluationSeconds),
        Enabled, ClearConfirmationCount);
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConfigurationDigest => AlertRuleTemplateValidation.Digest(string.Join('|', Name, Kind, MetricId ?? "<null>",
        Comparison, Threshold.ToString("R", CultureInfo.InvariantCulture), Hysteresis.ToString("R", CultureInfo.InvariantCulture),
        ConfirmationCount.ToString(CultureInfo.InvariantCulture), ConfirmationSeconds.ToString(CultureInfo.InvariantCulture),
        EvaluationSeconds.ToString(CultureInfo.InvariantCulture), ClearConfirmationCount.ToString(CultureInfo.InvariantCulture), Enabled ? "true" : "false"));
    public static AlertComparison ParseComparison(string value) => value switch
    {
        "greater_than" => AlertComparison.GreaterThan, "greater_than_or_equal" => AlertComparison.GreaterThanOrEqual,
        "less_than" => AlertComparison.LessThan, "less_than_or_equal" => AlertComparison.LessThanOrEqual,
        "equal" => AlertComparison.Equal, _ => throw new ArgumentException("Unknown alert comparison.", nameof(value)),
    };
}

public sealed record AlertRuleTemplateGroup
{
    public AlertRuleTemplateGroup(string kind, string value)
    {
        if (kind is not ("environment" or "tag")) throw new ArgumentException("Unknown group selector.", nameof(kind));
        if (value is null || value.Length is < 1 or > 64 || value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(static c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-')))
            throw new ArgumentException("A normalized group label is required.", nameof(value));
        Kind = kind; Value = value;
    }
    public string Kind { get; }
    public string Value { get; }
}

public static class AlertRuleTemplateValidation
{
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    public static string Reason(string value) => DomainValidation.RequireSafeText(value, nameof(value), 512);
    public static string Title(string value) => DomainValidation.RequireSafeText(value, nameof(value), 256);
    public static string SourceName(string value) => DomainValidation.RequireAsciiToken(value, nameof(value), 128,
        static c => DomainValidation.IsAsciiLetter(c) || DomainValidation.IsAsciiDigit(c) || c is '.' or '_' or '-', true);
    public static Guid Id(Guid value) => value != Guid.Empty ? value : throw new ArgumentException("A nonempty UUID is required.", nameof(value));
    public static long Positive(long value) => value is > 0 and <= MaximumSafeInteger ? value : throw new ArgumentOutOfRangeException(nameof(value));
    public static string CatalogDigest(string value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        ? value : throw new ArgumentException("A lower-case SHA256 catalog digest is required.", nameof(value));
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
