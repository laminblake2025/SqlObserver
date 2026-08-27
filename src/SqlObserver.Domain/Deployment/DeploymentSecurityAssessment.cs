using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SqlObserver.Domain.Deployment;

/// <summary>Closed wire status for one deployment-security check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentSecurityCheckStatus
{
    [JsonStringEnumMemberName("not_evaluated")] NotEvaluated = 1,
    [JsonStringEnumMemberName("passed")] Passed = 2,
    [JsonStringEnumMemberName("failed")] Failed = 3,
    [JsonStringEnumMemberName("blocked")] Blocked = 4,
}

/// <summary>Assessment status. This v1 contract never authorizes activation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentSecurityAssessmentStatus
{
    [JsonStringEnumMemberName("not_ready")] NotReady = 1,
    [JsonStringEnumMemberName("ready_for_review")] ReadyForReview = 2,
    [JsonStringEnumMemberName("ready_to_activate")] ReadyToActivate = 3,
}

/// <summary>Safe, closed identifiers and ordering for ADR-0017 v1.</summary>
public static class DeploymentSecurityCheckCatalog
{
    public static IReadOnlyList<string> OrderedIds { get; } = new ReadOnlyCollection<string>([
        "owner_policy",
        "identity_topology",
        "secret_store_policy",
        "certificate_lifecycle",
        "postgresql_transport",
        "mcp_endpoint",
        "target_connection_policy",
        "credential_policy",
        "sensitive_content",
        "configuration_integrity",
        "runtime_permissions",
        "release_evidence"]);
}

/// <summary>A bounded check result. It has no raw endpoint, path, secret, or exception field.</summary>
public sealed class DeploymentSecurityAssessmentCheck
{
    public const int MaximumCodeLength = 64;
    public DeploymentSecurityAssessmentCheck(string checkId, DeploymentSecurityCheckStatus status, string code)
    {
        CheckId = RequireToken(checkId, nameof(checkId), 64);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        Code = RequireToken(code, nameof(code), MaximumCodeLength);
    }

    public string CheckId { get; }
    public DeploymentSecurityCheckStatus Status { get; }
    public string Code { get; }

    private static string RequireToken(string value, string name, int max)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0 || value.Length > max || value[0] is not (>= 'a' and <= 'z') || value.Skip(1).Any(static c => !(c is >= 'a' and <= 'z') && !(c is >= '0' and <= '9') && c is not ('_' or '-')))
            throw new ArgumentException("The value must be a bounded lowercase safe code.", name);
        return value;
    }

}

/// <summary>
/// Closed deployment-security assessment payload. The constructor validates the exact catalog
/// and deliberately ignores any caller attempt to supply readiness; this v1 is observation-only.
/// </summary>
public sealed class DeploymentSecurityAssessment
{
    private readonly ReadOnlyCollection<DeploymentSecurityAssessmentCheck> _checks;

    public DeploymentSecurityAssessment(IReadOnlyList<DeploymentSecurityAssessmentCheck> checks, DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count != DeploymentSecurityCheckCatalog.OrderedIds.Count)
            throw new ArgumentException("The assessment must contain exactly the fixed v1 checks.", nameof(checks));
        if (evaluatedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The timestamp must be UTC.", nameof(evaluatedAtUtc));
        var copy = new DeploymentSecurityAssessmentCheck[checks.Count];
        for (int i = 0; i < checks.Count; i++)
        {
            copy[i] = checks[i] ?? throw new ArgumentException("A check cannot be null.", nameof(checks));
            if (!string.Equals(copy[i].CheckId, DeploymentSecurityCheckCatalog.OrderedIds[i], StringComparison.Ordinal))
                throw new ArgumentException("Checks must use the exact catalog order.", nameof(checks));
        }
        _checks = Array.AsReadOnly(copy);
        EvaluatedAtUtc = evaluatedAtUtc;
        // Assessment-only v1 never grants activation, even when a caller supplies all passes.
        Status = DeploymentSecurityAssessmentStatus.NotReady;
        ReadyToActivate = false;
    }

    [JsonConverter(typeof(CanonicalUtcJsonConverter))]
    public DateTimeOffset EvaluatedAtUtc { get; }
    public IReadOnlyList<DeploymentSecurityAssessmentCheck> Checks => _checks;
    public DeploymentSecurityAssessmentStatus Status { get; }
    public bool ReadyToActivate { get; }
}

/// <summary>Safe disposition accepted by the application observation port.</summary>
public enum DeploymentSecurityObservationDisposition
{
    Missing = 1,
    Ambiguous = 2,
    Accepted = 3,
    Unsafe = 4,
    // Friendly aliases for adapters that already model terminal states as pass/fail/block.
    Blocked = Ambiguous,
    Passed = Accepted,
    Failed = Unsafe,
}

/// <summary>A sanitized observation with a bounded code and no source payload.</summary>
public sealed class DeploymentSecurityObservation
{
    public DeploymentSecurityObservation(string checkId, DeploymentSecurityObservationDisposition disposition, string code)
    {
        ArgumentNullException.ThrowIfNull(checkId);
        if (checkId.Length == 0 || checkId.Length > 64 || checkId[0] is not (>= 'a' and <= 'z') || checkId.Skip(1).Any(static c => !(c is >= 'a' and <= 'z') && !(c is >= '0' and <= '9') && c is not ('_' or '-')))
            throw new ArgumentException("The check ID is not a safe token.", nameof(checkId));
        CheckId = checkId;
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        Disposition = disposition;
        Code = code ?? throw new ArgumentNullException(nameof(code));
        if (Code.Length == 0 || Code.Length > DeploymentSecurityAssessmentCheck.MaximumCodeLength || Code[0] is not (>= 'a' and <= 'z') || Code.Skip(1).Any(static c => !(c is >= 'a' and <= 'z') && !(c is >= '0' and <= '9') && c is not ('_' or '-')))
            throw new ArgumentException("The code is not safe.", nameof(code));
    }
    public string CheckId { get; }
    public DeploymentSecurityObservationDisposition Disposition { get; }
    public string Code { get; }
}
