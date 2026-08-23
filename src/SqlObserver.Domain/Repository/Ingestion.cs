using System.Collections.ObjectModel;
using SqlObserver.Domain.Diagnostics;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Repository;

/// <summary>Hard caps for one repository-ingestion call.</summary>
public sealed class IngestionLimits
{
    public const int MaximumItemCount = 10_000;
    public const int MaximumBatchBytes = 33_554_432;
    public const int MaximumItemBytes = 2_097_152;

    public IngestionLimits(int maxItems, int maxBatchBytes, int maxItemBytes)
    {
        if (maxItems is <= 0 or > MaximumItemCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxItems),
                $"The item limit must be between 1 and {MaximumItemCount}.");
        }

        if (maxBatchBytes is <= 0 or > MaximumBatchBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBatchBytes),
                $"The batch-byte limit must be between 1 and {MaximumBatchBytes}.");
        }

        if (maxItemBytes is <= 0 or > MaximumItemBytes || maxItemBytes > maxBatchBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxItemBytes),
                $"The item-byte limit must be positive, no greater than {MaximumItemBytes}, and no greater than the batch-byte limit.");
        }

        MaxItems = maxItems;
        MaxBatchBytes = maxBatchBytes;
        MaxItemBytes = maxItemBytes;
    }

    public int MaxItems { get; }

    public int MaxBatchBytes { get; }

    public int MaxItemBytes { get; }
}

public sealed class TelemetryBatch
{
    private readonly ReadOnlyCollection<MetricSample> _samples;

    public TelemetryBatch(IReadOnlyList<MetricSample> samples, IngestionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(limits);

        MetricSample[] copy = IngestionBatchValidation.CopyAndValidate(samples, limits, nameof(samples));
        var identities = new HashSet<(DateTimeOffset ObservedAtUtc, Guid SampleId)>();

        foreach (MetricSample sample in copy)
        {
            if (!identities.Add((sample.ObservedAtUtc, sample.SampleId.Value)))
            {
                throw new ArgumentException(
                    "A telemetry batch cannot contain duplicate UTC sample identities.",
                    nameof(samples));
            }
        }

        _samples = Array.AsReadOnly(copy);
        Limits = limits;
        TotalBytes = IngestionBatchValidation.CalculateTotalBytes(copy);
    }

    public IReadOnlyList<MetricSample> Samples => _samples;

    public IngestionLimits Limits { get; }

    public int ItemCount => _samples.Count;

    public int TotalBytes { get; }
}

public sealed class DiagnosticEventBatch
{
    private readonly ReadOnlyCollection<DiagnosticEventEnvelope> _events;

    public DiagnosticEventBatch(IReadOnlyList<DiagnosticEventEnvelope> events, IngestionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(limits);

        DiagnosticEventEnvelope[] copy = IngestionBatchValidation.CopyAndValidate(events, limits, nameof(events));
        var identities = new HashSet<(DateTimeOffset OccurredAtUtc, Guid EventId)>();

        foreach (DiagnosticEventEnvelope diagnosticEvent in copy)
        {
            if (!identities.Add((diagnosticEvent.OccurredAtUtc, diagnosticEvent.EventId.Value)))
            {
                throw new ArgumentException(
                    "A diagnostic-event batch cannot contain duplicate UTC event identities.",
                    nameof(events));
            }
        }

        _events = Array.AsReadOnly(copy);
        Limits = limits;
        TotalBytes = IngestionBatchValidation.CalculateTotalBytes(copy);
    }

    public IReadOnlyList<DiagnosticEventEnvelope> Events => _events;

    public IngestionLimits Limits { get; }

    public int ItemCount => _events.Count;

    public int TotalBytes { get; }
}

/// <summary>Visible row and byte accounting for a completed bounded ingestion call.</summary>
public sealed class IngestionResult
{
    public IngestionResult(
        int attemptedCount,
        int insertedCount,
        int duplicateCount,
        int rejectedCount,
        int attemptedBytes,
        int persistedBytes,
        DateTimeOffset completedAtUtc)
    {
        if (attemptedCount is <= 0 or > IngestionLimits.MaximumItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptedCount));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(insertedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(rejectedCount);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(insertedCount, attemptedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duplicateCount, attemptedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rejectedCount, attemptedCount);

        if (insertedCount + duplicateCount + rejectedCount != attemptedCount)
        {
            throw new ArgumentException("Inserted, duplicate, and rejected counts must account for every attempted item.");
        }

        if (attemptedBytes is <= 0 or > IngestionLimits.MaximumBatchBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptedBytes));
        }

        if (persistedBytes < 0 || persistedBytes > attemptedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(persistedBytes),
                "Persisted bytes must be non-negative and cannot exceed attempted bytes.");
        }

        AttemptedCount = attemptedCount;
        InsertedCount = insertedCount;
        DuplicateCount = duplicateCount;
        RejectedCount = rejectedCount;
        AttemptedBytes = attemptedBytes;
        PersistedBytes = persistedBytes;
        CompletedAtUtc = DomainValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));
    }

    public int AttemptedCount { get; }

    public int InsertedCount { get; }

    public int DuplicateCount { get; }

    public int RejectedCount { get; }

    public int AttemptedBytes { get; }

    public int PersistedBytes { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public static IngestionResult FromTelemetryBatch(
        TelemetryBatch batch,
        int insertedCount,
        int duplicateCount,
        int rejectedCount,
        int persistedBytes,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new IngestionResult(
            batch.ItemCount,
            insertedCount,
            duplicateCount,
            rejectedCount,
            batch.TotalBytes,
            persistedBytes,
            completedAtUtc);
    }

    public static IngestionResult FromDiagnosticEventBatch(
        DiagnosticEventBatch batch,
        int insertedCount,
        int duplicateCount,
        int rejectedCount,
        int persistedBytes,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new IngestionResult(
            batch.ItemCount,
            insertedCount,
            duplicateCount,
            rejectedCount,
            batch.TotalBytes,
            persistedBytes,
            completedAtUtc);
    }
}

internal static class IngestionBatchValidation
{
    public static T[] CopyAndValidate<T>(
        IReadOnlyList<T> records,
        IngestionLimits limits,
        string parameterName)
        where T : class, IIngestionRecord
    {
        if (records.Count is 0 || records.Count > limits.MaxItems)
        {
            throw new ArgumentException(
                $"A batch must contain between 1 and {limits.MaxItems} records.",
                parameterName);
        }

        var copy = new T[records.Count];
        int totalBytes = 0;

        for (int index = 0; index < records.Count; index++)
        {
            T record = records[index] ?? throw new ArgumentException(
                "A batch cannot contain a null record.",
                parameterName);

            if (record.EstimatedSizeBytes is <= 0 || record.EstimatedSizeBytes > limits.MaxItemBytes)
            {
                throw new ArgumentException(
                    $"Each record must account for between 1 and {limits.MaxItemBytes} bytes.",
                    parameterName);
            }

            totalBytes = checked(totalBytes + record.EstimatedSizeBytes);

            if (totalBytes > limits.MaxBatchBytes)
            {
                throw new ArgumentException(
                    $"The batch cannot account for more than {limits.MaxBatchBytes} bytes.",
                    parameterName);
            }

            copy[index] = record;
        }

        return copy;
    }

    public static int CalculateTotalBytes<T>(IReadOnlyList<T> records)
        where T : IIngestionRecord
    {
        int totalBytes = 0;

        for (int index = 0; index < records.Count; index++)
        {
            totalBytes = checked(totalBytes + records[index].EstimatedSizeBytes);
        }

        return totalBytes;
    }
}
