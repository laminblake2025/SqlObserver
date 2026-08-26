using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>A caller-owned idempotency identity for one scheduled collector run.</summary>
public sealed record CollectorRunId
{
    public CollectorRunId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A collector-run identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public enum CollectorRunOutcome
{
    Succeeded = 1,
    Partial = 2,
    TimedOut = 3,
    TransientFailure = 4,
    PermanentFailure = 5,
    PermissionDenied = 6,
    Unsupported = 7,
    OutputInvalid = 8,
    LeaseLost = 9,
    CircuitOpen = 10,
}

/// <summary>A closed catalog of safe failure reasons; raw provider details never cross this boundary.</summary>
public enum CollectorRunReason
{
    Completed = 1,
    SourceRowLimit = 2,
    ResponseByteLimit = 3,
    DeadlineExceeded = 4,
    TransientTargetFailure = 5,
    PermanentTargetFailure = 6,
    RequiredPermissionMissing = 7,
    TargetUnsupported = 8,
    OutputValidationFailed = 9,
    LeaseOwnershipLost = 10,
    CircuitCurrentlyOpen = 11,
    CapabilityProfileMissing = 12,
    CapabilityProfileStale = 13,
    CapabilityMissing = 14,
    TargetVersionUnsupported = 15,
    TargetPlatformUnsupported = 16,
    TargetEditionUnsupported = 17,
    BlockingGraphLimit = 18,
    OverlapDeduplicated = 19,
    Degraded = 20,
    VisibilityIncomplete = 21,
}

public enum CollectorLossKind
{
    None = 1,
    SourceRowLimit = 2,
    ResponseByteLimit = 3,
    OutputValidationFailure = 4,
    IngestionRejection = 5,
    BlockingGraphLimit = 6,
    DuplicateOverlap = 7,
    VisibilityIncomplete = 8,
}

/// <summary>Visible, conservative evidence whenever collection cannot claim a complete sample.</summary>
public sealed class CollectorLossEvidence
{
    public CollectorLossEvidence(
        CollectorLossKind kind,
        int minimumLostItems,
        bool countIsExact,
        int minimumLostBytes = 0)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minimumLostItems);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLostBytes);

        bool isNone = kind == CollectorLossKind.None;
        if (isNone != (minimumLostItems == 0 && minimumLostBytes == 0 && countIsExact))
        {
            throw new ArgumentException(
                "No-loss evidence must be exact and empty; visible loss must account for at least one item or byte.");
        }

        if (!isNone && minimumLostItems == 0 && minimumLostBytes == 0)
        {
            throw new ArgumentException("Visible loss must account for at least one item or byte.");
        }

        Kind = kind;
        MinimumLostItems = minimumLostItems;
        CountIsExact = countIsExact;
        MinimumLostBytes = minimumLostBytes;
    }

    public CollectorLossKind Kind { get; }

    public int MinimumLostItems { get; }

    public bool CountIsExact { get; }

    public int MinimumLostBytes { get; }

    public bool HasLoss => Kind != CollectorLossKind.None;

    public static CollectorLossEvidence None { get; } = new(
        CollectorLossKind.None,
        minimumLostItems: 0,
        countIsExact: true);
}

/// <summary>Separates target rows, emitted objects, and bytes so fan-out cannot hide loss.</summary>
public sealed class CollectorRunAccounting
{
    public CollectorRunAccounting(
        int sourceRowsRead,
        int outputItemsProduced,
        int responseBytes,
        int outputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceRowsRead);
        ArgumentOutOfRangeException.ThrowIfNegative(outputItemsProduced);
        ArgumentOutOfRangeException.ThrowIfNegative(responseBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(outputBytes);

        if (outputBytes > responseBytes && responseBytes != 0)
        {
            throw new ArgumentException("Output bytes cannot exceed non-zero response bytes.", nameof(outputBytes));
        }

        SourceRowsRead = sourceRowsRead;
        OutputItemsProduced = outputItemsProduced;
        ResponseBytes = responseBytes;
        OutputBytes = outputBytes;
    }

    public int SourceRowsRead { get; }

    public int OutputItemsProduced { get; }

    public int ResponseBytes { get; }

    public int OutputBytes { get; }
}

public enum CollectorCircuitState
{
    Closed = 1,
    Open = 2,
    HalfOpen = 3,
}

/// <summary>A repository-clock circuit snapshot for one target/collector contract.</summary>
public sealed class CollectorCircuitSnapshot
{
    public const int MaximumConsecutiveFailures = 1_000_000;

    public CollectorCircuitSnapshot(
        CollectorCircuitState state,
        int consecutiveFailures,
        DateTimeOffset repositoryTimeUtc,
        DateTimeOffset? openUntilUtc = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (consecutiveFailures is < 0 or > MaximumConsecutiveFailures)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
        }

        repositoryTimeUtc = DomainValidation.RequireUtcMicrosecondAligned(
            repositoryTimeUtc,
            nameof(repositoryTimeUtc));
        if (openUntilUtc is not null)
        {
            openUntilUtc = DomainValidation.RequireUtcMicrosecondAligned(
                openUntilUtc.Value,
                nameof(openUntilUtc));
        }

        if ((state == CollectorCircuitState.Open) != (openUntilUtc is not null))
        {
            throw new ArgumentException("Only an open circuit carries an open-until timestamp.", nameof(openUntilUtc));
        }

        if (openUntilUtc <= repositoryTimeUtc)
        {
            throw new ArgumentException("An open circuit must remain open after repository time.", nameof(openUntilUtc));
        }

        State = state;
        ConsecutiveFailures = consecutiveFailures;
        RepositoryTimeUtc = repositoryTimeUtc;
        OpenUntilUtc = openUntilUtc;
    }

    public CollectorCircuitState State { get; }

    public int ConsecutiveFailures { get; }

    public DateTimeOffset RepositoryTimeUtc { get; }

    public DateTimeOffset? OpenUntilUtc { get; }

    public static CollectorCircuitSnapshot Closed(DateTimeOffset repositoryTimeUtc) =>
        new(CollectorCircuitState.Closed, 0, repositoryTimeUtc);
}

/// <summary>The validated execution evidence persisted with one atomic collector commit.</summary>
public sealed class CollectorRunSummary
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(1);

    public CollectorRunSummary(
        CollectorRunId runId,
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        CollectorId collectorId,
        int collectorManifestVersion,
        int outputSchemaVersion,
        CollectorRunOutcome outcome,
        CollectorRunReason reason,
        TimeSpan duration,
        int attemptCount,
        CollectorRunAccounting accounting,
        CollectorLossEvidence loss)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(accounting);
        ArgumentNullException.ThrowIfNull(loss);

        if (collectorManifestVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(collectorManifestVersion));
        }

        if (outputSchemaVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSchemaVersion));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (duration < TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (attemptCount is < 0 or > 2 || (outcome != CollectorRunOutcome.CircuitOpen && attemptCount == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        if (outcome == CollectorRunOutcome.Succeeded && (reason != CollectorRunReason.Completed || loss.HasLoss))
        {
            throw new ArgumentException("A successful run must be complete and loss-free.");
        }

        if (outcome == CollectorRunOutcome.Partial && !loss.HasLoss)
        {
            throw new ArgumentException("A partial run must expose sample loss.", nameof(loss));
        }

        RunId = runId;
        TargetId = targetId;
        TargetRevision = targetRevision;
        CollectorId = collectorId;
        CollectorManifestVersion = collectorManifestVersion;
        OutputSchemaVersion = outputSchemaVersion;
        Outcome = outcome;
        Reason = reason;
        Duration = duration;
        AttemptCount = attemptCount;
        Accounting = accounting;
        Loss = loss;
    }

    public CollectorRunId RunId { get; }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision TargetRevision { get; }

    public CollectorId CollectorId { get; }

    public int CollectorManifestVersion { get; }

    public int OutputSchemaVersion { get; }

    public CollectorRunOutcome Outcome { get; }

    public CollectorRunReason Reason { get; }

    public TimeSpan Duration { get; }

    public int AttemptCount { get; }

    public int RetryCount => Math.Max(0, AttemptCount - 1);

    public CollectorRunAccounting Accounting { get; }

    public CollectorLossEvidence Loss { get; }
}
