using System.Collections.ObjectModel;
using SqlObserver.Domain.Deployment;
using SqlObserver.Domain.Repository;
using System.Text.Json.Serialization;

namespace SqlObserver.Application.Ports;

 [JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationAssessmentStatus
{
    [JsonStringEnumMemberName("not_evaluated")]
    NotEvaluated = 1,
    [JsonStringEnumMemberName("current")]
    Current = 2,
    [JsonStringEnumMemberName("pending")]
    Pending = 3,
    [JsonStringEnumMemberName("drift")]
    Drift = 4,
    [JsonStringEnumMemberName("gap")]
    Gap = 5,
    [JsonStringEnumMemberName("unknown")]
    Unknown = 6,
    [JsonStringEnumMemberName("unsupported_major")]
    UnsupportedMajor = 7,
    [JsonStringEnumMemberName("lock_unavailable")]
    LockUnavailable = 8,
    [JsonStringEnumMemberName("timed_out")]
    TimedOut = 9,
    [JsonStringEnumMemberName("access_denied")]
    AccessDenied = 10,
    [JsonStringEnumMemberName("invalid_history")]
    InvalidHistory = 11,
}

public sealed class MigrationAssessmentRequest
{
    public const int MaximumHistory = MigrationNumber.MaximumValue;

    public MigrationAssessmentRequest(int maxHistory, RepositoryCallTimeout timeout)
    {
        if (maxHistory is <= 0 or > MaximumHistory) throw new ArgumentOutOfRangeException(nameof(maxHistory));
        ArgumentNullException.ThrowIfNull(timeout);
        MaxHistory = maxHistory;
        Timeout = timeout;
    }

    public int MaxHistory { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class MigrationAssessmentResult
{
    private readonly ReadOnlyCollection<MigrationHistoryEntry> _history;

    public MigrationAssessmentResult(
        MigrationAssessmentStatus status,
        IReadOnlyList<MigrationHistoryEntry> history,
        DateTimeOffset evaluatedAtUtc,
        string code,
        int? nextMigrationNumber = null,
        PostgreSqlVersion? serverVersion = null,
        int? observedHistoryCount = null)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count > MigrationAssessmentRequest.MaximumHistory) throw new ArgumentException("Migration history exceeds the supported bound.", nameof(history));
        if (evaluatedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The timestamp must be UTC.", nameof(evaluatedAtUtc));
        if (code.Length is 0 or > 64 || code.Any(static c => !(c is >= 'a' and <= 'z') && !(c is >= '0' and <= '9') && c != '_' && c != '-')) throw new ArgumentException("The code is not safe.", nameof(code));
        if (nextMigrationNumber is <= 0) throw new ArgumentOutOfRangeException(nameof(nextMigrationNumber));
        if (status == MigrationAssessmentStatus.Pending && nextMigrationNumber is null) throw new ArgumentException("Pending assessments require a next migration number.", nameof(nextMigrationNumber));
        if (status != MigrationAssessmentStatus.Pending && nextMigrationNumber is not null) throw new ArgumentException("Only pending assessments may include a next migration number.", nameof(nextMigrationNumber));
        if (observedHistoryCount is < 0 or > MigrationAssessmentRequest.MaximumHistory) throw new ArgumentOutOfRangeException(nameof(observedHistoryCount));

        var copy = new MigrationHistoryEntry[history.Count];
        for (int i = 0; i < history.Count; i++) copy[i] = history[i] ?? throw new ArgumentException("History cannot contain null entries.", nameof(history));
        Status = status;
        _history = Array.AsReadOnly(copy);
        EvaluatedAtUtc = evaluatedAtUtc;
        Code = code;
        NextMigrationNumber = nextMigrationNumber;
        ServerVersion = serverVersion;
        ObservedHistoryCount = observedHistoryCount ?? history.Count;
    }

    public MigrationAssessmentStatus Status { get; }
    public IReadOnlyList<MigrationHistoryEntry> History => _history;
    [JsonConverter(typeof(SqlObserver.Domain.Deployment.CanonicalUtcJsonConverter))]
    public DateTimeOffset EvaluatedAtUtc { get; }
    public string Code { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextMigrationNumber { get; }
    /// <summary>Observed only; no supported minimum patch floor is inferred by this foundation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PostgreSqlVersion? ServerVersion { get; }
    public int ObservedHistoryCount { get; }
    [JsonIgnore]
    public bool IsCurrent => Status == MigrationAssessmentStatus.Current;
}

public interface IMigrationAssessmentPort
{
    ValueTask<MigrationAssessmentResult> AssessAsync(
        MigrationAssessmentRequest request,
        CancellationToken cancellationToken);
}

public sealed class LifecycleAssessmentRequest
{
    public LifecycleAssessmentRequest(
        DeploymentLifecycleAction action,
        ProductIdentityBinding expectedProduct,
        ProductIdentityBinding? observedProduct,
        MigrationAssessmentRequest migration,
        PinnedOwnerPolicy? ownerPolicy = null)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        ArgumentNullException.ThrowIfNull(expectedProduct);
        ArgumentNullException.ThrowIfNull(migration);
        Action = action;
        ExpectedProduct = expectedProduct;
        ObservedProduct = observedProduct;
        Migration = migration;
        OwnerPolicy = ownerPolicy;
    }

    public DeploymentLifecycleAction Action { get; }
    public ProductIdentityBinding ExpectedProduct { get; }
    public ProductIdentityBinding? ObservedProduct { get; }
    public MigrationAssessmentRequest Migration { get; }
    public PinnedOwnerPolicy? OwnerPolicy { get; }
}

public interface ILifecycleAssessmentService
{
    ValueTask<DeploymentLifecycleAssessment> AssessAsync(
        LifecycleAssessmentRequest request,
        CancellationToken cancellationToken);
}
