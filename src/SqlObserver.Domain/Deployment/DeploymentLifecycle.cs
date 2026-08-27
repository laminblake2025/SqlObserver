using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace SqlObserver.Domain.Deployment;

/// <summary>Lifecycle operations understood by the assessment contract.</summary>
 [JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentLifecycleAction
{
    [JsonStringEnumMemberName("install")]
    Install = 1,
    [JsonStringEnumMemberName("upgrade")]
    Upgrade = 2,
    [JsonStringEnumMemberName("recover")]
    Recover = 3,
    [JsonStringEnumMemberName("uninstall")]
    Uninstall = 4,
    [JsonStringEnumMemberName("purge")]
    Purge = 5,
}

/// <summary>Closed result set for one lifecycle preflight check.</summary>
 [JsonConverter(typeof(JsonStringEnumConverter))]
public enum LifecycleCheckStatus
{
    [JsonStringEnumMemberName("not_evaluated")]
    NotEvaluated = 1,
    [JsonStringEnumMemberName("passed")]
    Passed = 2,
    [JsonStringEnumMemberName("failed")]
    Failed = 3,
    [JsonStringEnumMemberName("blocked")]
    Blocked = 4,
}

/// <summary>Closed result set for a lifecycle assessment.</summary>
 [JsonConverter(typeof(JsonStringEnumConverter))]
public enum LifecycleAssessmentStatus
{
    [JsonStringEnumMemberName("not_ready")]
    NotReady = 1,
    [JsonStringEnumMemberName("ready_for_review")]
    ReadyForReview = 2,
    [JsonStringEnumMemberName("ready_to_mutate")]
    ReadyToMutate = 3,
}

/// <summary>Safe identity of the product under assessment. It contains no path, secret, or certificate.</summary>
public sealed class ProductIdentityBinding
{
    public const int MaximumProductIdLength = 64;
    public const int MaximumVersionLength = 48;
    public const int MaximumCommitLength = 128;
    public const int MaximumDigestLength = 128;
    public const long MaximumBundleSize = 4L * 1024 * 1024 * 1024;
    public const int MaximumRunIdLength = 64;
    public const int MaximumEnvironmentLength = 64;

    public ProductIdentityBinding(
        string productId,
        string version,
        string commit,
        string bundleDigest,
        long bundleSize,
        string runId,
        string environment)
    {
        ProductId = Token(productId, nameof(productId), MaximumProductIdLength);
        Version = Token(version, nameof(version), MaximumVersionLength);
        Commit = HexOrToken(commit, nameof(commit), MaximumCommitLength);
        BundleDigest = HexOrToken(bundleDigest, nameof(bundleDigest), MaximumDigestLength, requireSha256: true);
        if (bundleSize is < 1 or > MaximumBundleSize) throw new ArgumentOutOfRangeException(nameof(bundleSize));
        BundleSize = bundleSize;
        RunId = Token(runId, nameof(runId), MaximumRunIdLength);
        Environment = Token(environment, nameof(environment), MaximumEnvironmentLength);
    }

    public string ProductId { get; }
    public string Version { get; }
    public string Commit { get; }
    public string BundleDigest { get; }
    public long BundleSize { get; }
    public string RunId { get; }
    public string Environment { get; }

    private static string Token(string value, string name, int max)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0 || value.Length > max || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"{name} must be a bounded safe token.", name);
        return value;
    }

    private static string HexOrToken(string value, string name, int max, bool requireSha256 = false)
    {
        value = Token(value, name, max);
        if ((requireSha256 && value.Length != 64) || (!requireSha256 && value.Length < 8) || value.Any(static c => !(c is >= '0' and <= '9') && !(c is >= 'a' and <= 'f') && !(c is >= 'A' and <= 'F')))
            throw new ArgumentException($"{name} must be a hexadecimal digest.", name);
        return value;
    }
}

/// <summary>Descriptor for a future pinned owner policy; no public constructor can accept it.</summary>
public sealed class PinnedOwnerPolicy
{
    internal PinnedOwnerPolicy(string policyId, string policyDigest)
    {
        PolicyId = RequireToken(policyId, nameof(policyId), 64);
        PolicyDigest = RequireToken(policyDigest, nameof(policyDigest), 128);
        if (PolicyDigest.Length < 8 || PolicyDigest.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("The policy digest must be hexadecimal.", nameof(policyDigest));
    }

    public string PolicyId { get; }
    public string PolicyDigest { get; }

    private static string RequireToken(string value, string name, int max)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0 || value.Length > max || value.Any(static c => c is < '!' or > '~' || c is '"' or '\\'))
            throw new ArgumentException("The value is not a safe token.", name);
        return value;
    }
}

public sealed class LifecycleAssessmentCheck
{
    public const int MaximumCheckIdLength = 64;
    public const int MaximumCodeLength = 64;
    public const int MaximumDetailLength = 160;

    public LifecycleAssessmentCheck(
        string checkId,
        LifecycleCheckStatus status,
        string code,
        string? detail = null)
    {
        CheckId = RequireToken(checkId, nameof(checkId), MaximumCheckIdLength);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        Code = RequireToken(code, nameof(code), MaximumCodeLength);
        Detail = detail is null ? null : RequireToken(detail, nameof(detail), MaximumDetailLength);
    }

    public string CheckId { get; }
    public LifecycleCheckStatus Status { get; }
    public string Code { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; }

    private static string RequireToken(string value, string name, int max)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0 || value.Length > max || value.Any(static c => !(c is >= 'a' and <= 'z') && !(c is >= '0' and <= '9') && c != '_' && c != '-'))
            throw new ArgumentException("The value contains unsupported characters.", name);
        return value;
    }
}

public sealed class DeploymentLifecycleAssessment
{
    public const int MaximumChecks = 256;
    private readonly ReadOnlyCollection<LifecycleAssessmentCheck> _checks;

    public DeploymentLifecycleAssessment(
        DeploymentLifecycleAction action,
        ProductIdentityBinding product,
        IReadOnlyList<LifecycleAssessmentCheck> checks,
        DateTimeOffset evaluatedAtUtc,
        PinnedOwnerPolicy? ownerPolicy = null)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count > MaximumChecks) throw new ArgumentException("Too many lifecycle checks.", nameof(checks));
        if (evaluatedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The timestamp must be UTC.", nameof(evaluatedAtUtc));

        var copy = new LifecycleAssessmentCheck[checks.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < checks.Count; i++)
        {
            LifecycleAssessmentCheck check = checks[i] ?? throw new ArgumentException("A check cannot be null.", nameof(checks));
            if (!ids.Add(check.CheckId)) throw new ArgumentException("Check IDs must be unique.", nameof(checks));
            copy[i] = check;
        }

        Action = action;
        Product = product;
        _checks = Array.AsReadOnly(copy);
        EvaluatedAtUtc = evaluatedAtUtc;
        OwnerPolicy = ownerPolicy;
        // This foundation has no mutator and therefore never authorizes a mutation.
        ReadyToMutate = false;
        // ADR-0016 is Proposed and no trusted owner-policy asset exists. Caller-supplied
        // checks can never authorize readiness during this foundation milestone.
        Status = LifecycleAssessmentStatus.NotReady;
    }

    public DeploymentLifecycleAction Action { get; }
    public ProductIdentityBinding Product { get; }
    public IReadOnlyList<LifecycleAssessmentCheck> Checks => _checks;
    [JsonConverter(typeof(CanonicalUtcJsonConverter))]
    public DateTimeOffset EvaluatedAtUtc { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PinnedOwnerPolicy? OwnerPolicy { get; }
    public LifecycleAssessmentStatus Status { get; }
    public bool ReadyToMutate { get; }
}
