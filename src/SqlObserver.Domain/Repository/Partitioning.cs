using System.Globalization;

namespace SqlObserver.Domain.Repository;

public enum PartitionGranularity
{
    Daily = 1,
    Monthly = 2,
}

/// <summary>A PostgreSQL-safe, unquoted base relation name with room for deterministic suffixes.</summary>
public sealed record PartitionSetName
{
    public const int MaximumLength = 53;

    public PartitionSetName(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character =>
                (character is >= 'a' and <= 'z') ||
                DomainValidation.IsAsciiDigit(character) ||
                character == '_',
            requireLeadingLetter: true);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A half-open UTC range and deterministic native-partition name.</summary>
public sealed class PartitionRange
{
    private PartitionRange(
        PartitionSetName setName,
        PartitionGranularity granularity,
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toExclusiveUtc,
        string name)
    {
        SetName = setName;
        Granularity = granularity;
        FromInclusiveUtc = fromInclusiveUtc;
        ToExclusiveUtc = toExclusiveUtc;
        Name = name;
    }

    public PartitionSetName SetName { get; }

    public PartitionGranularity Granularity { get; }

    public DateTimeOffset FromInclusiveUtc { get; }

    public DateTimeOffset ToExclusiveUtc { get; }

    public string Name { get; }

    public static PartitionRange Daily(PartitionSetName setName, DateTimeOffset utcInstant)
    {
        ArgumentNullException.ThrowIfNull(setName);
        utcInstant = DomainValidation.RequireUtc(utcInstant, nameof(utcInstant));
        var start = new DateTimeOffset(
            utcInstant.Year,
            utcInstant.Month,
            utcInstant.Day,
            0,
            0,
            0,
            TimeSpan.Zero);

        return new PartitionRange(
            setName,
            PartitionGranularity.Daily,
            start,
            start.AddDays(1),
            $"{setName.Value}_p{start.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
    }

    public static PartitionRange Monthly(PartitionSetName setName, DateTimeOffset utcInstant)
    {
        ArgumentNullException.ThrowIfNull(setName);
        utcInstant = DomainValidation.RequireUtc(utcInstant, nameof(utcInstant));
        var start = new DateTimeOffset(
            utcInstant.Year,
            utcInstant.Month,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);

        return new PartitionRange(
            setName,
            PartitionGranularity.Monthly,
            start,
            start.AddMonths(1),
            $"{setName.Value}_p{start.ToString("yyyyMM", CultureInfo.InvariantCulture)}");
    }

    public bool Contains(DateTimeOffset utcInstant)
    {
        utcInstant = DomainValidation.RequireUtc(utcInstant, nameof(utcInstant));
        return utcInstant >= FromInclusiveUtc && utcInstant < ToExclusiveUtc;
    }
}

public enum RetentionPreviewDisposition
{
    Keep = 1,
    EligibleForRemoval = 2,
    BlockedByRecoveryPrerequisite = 3,
}

/// <summary>The bounded repository-policy reason behind a read-only retention preview.</summary>
public enum RetentionPreviewReason
{
    PolicyDisabled = 1,
    DurationUnconfigured = 2,
    MinimumPartitionFloor = 3,
    WithinRetentionWindow = 4,
    RecoveryPrerequisiteUnsatisfied = 5,
    EligiblePreviewOnly = 6,
}

/// <summary>A read-only retention decision; it cannot perform a detach or drop.</summary>
public sealed class RetentionPreviewEntry
{
    private RetentionPreviewEntry(
        PartitionRange partition,
        DateTimeOffset cutoffUtc,
        DateTimeOffset evaluatedAtUtc,
        long estimatedRows,
        long estimatedBytes,
        bool policyEnabled,
        bool recoveryPrerequisiteSatisfied,
        RetentionPreviewReason reason,
        RetentionPreviewDisposition disposition)
    {
        Partition = partition;
        CutoffUtc = cutoffUtc;
        EvaluatedAtUtc = evaluatedAtUtc;
        EstimatedRows = estimatedRows;
        EstimatedBytes = estimatedBytes;
        PolicyEnabled = policyEnabled;
        RecoveryPrerequisiteSatisfied = recoveryPrerequisiteSatisfied;
        Reason = reason;
        Disposition = disposition;
    }

    public PartitionRange Partition { get; }

    public DateTimeOffset CutoffUtc { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }

    public long EstimatedRows { get; }

    public long EstimatedBytes { get; }

    public bool PolicyEnabled { get; }

    public bool RecoveryPrerequisiteSatisfied { get; }

    public RetentionPreviewReason Reason { get; }

    public RetentionPreviewDisposition Disposition { get; }

    public static RetentionPreviewEntry FromPolicyEvaluation(
        PartitionRange partition,
        DateTimeOffset cutoffUtc,
        DateTimeOffset evaluatedAtUtc,
        long estimatedRows,
        long estimatedBytes,
        bool policyEnabled,
        bool recoveryPrerequisiteSatisfied,
        RetentionPreviewReason reason)
    {
        ArgumentNullException.ThrowIfNull(partition);
        cutoffUtc = DomainValidation.RequireUtc(cutoffUtc, nameof(cutoffUtc));
        evaluatedAtUtc = DomainValidation.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));

        ArgumentOutOfRangeException.ThrowIfNegative(estimatedRows);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (policyEnabled == (reason == RetentionPreviewReason.PolicyDisabled))
        {
            throw new ArgumentException(
                "Policy-enabled state and retention reason are inconsistent.",
                nameof(reason));
        }

        if (reason == RetentionPreviewReason.RecoveryPrerequisiteUnsatisfied &&
            recoveryPrerequisiteSatisfied)
        {
            throw new ArgumentException(
                "An unsatisfied recovery reason cannot report a satisfied prerequisite.",
                nameof(recoveryPrerequisiteSatisfied));
        }

        if (reason == RetentionPreviewReason.EligiblePreviewOnly &&
            !recoveryPrerequisiteSatisfied)
        {
            throw new ArgumentException(
                "An eligible preview requires its recovery prerequisite.",
                nameof(recoveryPrerequisiteSatisfied));
        }

        RetentionPreviewDisposition disposition = reason switch
        {
            RetentionPreviewReason.RecoveryPrerequisiteUnsatisfied =>
                RetentionPreviewDisposition.BlockedByRecoveryPrerequisite,
            RetentionPreviewReason.EligiblePreviewOnly => RetentionPreviewDisposition.EligibleForRemoval,
            _ => RetentionPreviewDisposition.Keep,
        };

        return new RetentionPreviewEntry(
            partition,
            cutoffUtc,
            evaluatedAtUtc,
            estimatedRows,
            estimatedBytes,
            policyEnabled,
            recoveryPrerequisiteSatisfied,
            reason,
            disposition);
    }
}
