using System.Globalization;

namespace SqlObserver.Domain.Collection;

/// <summary>Actual completed attempts; loss/failure counts overlap and are not additive.</summary>
public sealed record DatabaseFileWindowHistoryCoverage(int RunCount, int CompleteRunCount, int GapRunCount,
    int FailedRunCount, int LostRunCount, int MissingPayloadRunCount, int EmptyRunCount,
    int RetainedFileCount, int? RetentionDays, bool HasVisibilityGap);

public sealed record DatabaseFileWindowHistoryReceipt(Guid RunId, long SampleTargetRevision,
    DateTimeOffset CompletedAtUtc, string Outcome, string? LossKind, long? MinimumLostItems,
    int? ExpectedFileCount, int StoredFileCount, DateTimeOffset? FirstObservedAtUtc,
    DateTimeOffset? LastObservedAtUtc, DateTimeOffset? FirstCollectedAtUtc, DateTimeOffset? LastCollectedAtUtc,
    string State, string Reason);

/// <summary>Isolated retained configuration and cumulative counters, not file incarnation or growth events.</summary>
public sealed record DatabaseFileWindowHistoryFile(int DatabaseId, int FileId, string LogicalName,
    string FileType, string FileState, string SizeBytes, string? MaximumSizeBytes, string GrowthBytes,
    int GrowthPercent, string GrowthMode, string ReadCount, string WriteCount, string BytesRead,
    string BytesWritten, string IoStallMilliseconds, string? ReadStallMilliseconds, string? WriteStallMilliseconds,
    DateTimeOffset ObservedAtUtc, DateTimeOffset CollectedAtUtc);

public static class DatabaseFileWindowHistoryValidation
{
    public const long MaximumRevision = 9_007_199_254_740_991;
    public const int MaximumPageSize = 100;
    public const int MaximumRuns = 10_000;
    public const int MaximumFilesPerRun = 10_000;
    public const int MaximumRetainedFiles = 250_000;
    public static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(15);
    public static void Id(Guid value)
    { if (value == Guid.Empty) throw new ArgumentException("A nonempty UUID is required.", nameof(value)); }
    public static void Revision(long value)
    { if (value is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Utc(DateTimeOffset value) => _ = DomainValidation.RequireUtcMicrosecondAligned(value, nameof(value));
    public static void Window(DateTimeOffset from, DateTimeOffset to)
    {
        Utc(from); Utc(to);
        if (from >= to || to - from > TimeSpan.FromDays(7)) throw new ArgumentException("A UTC window of at most seven days is required.");
    }
    public static void Digest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 digest is required.", nameof(value));
    }
    public static long Number(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) || result < 0 ||
            result.ToString(CultureInfo.InvariantCulture) != value) throw new ArgumentException("A canonical nonnegative Int64 string is required.", nameof(value));
        return result;
    }
    public static void Coverage(DatabaseFileWindowHistoryCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RunCount is < 0 or > MaximumRuns || value.CompleteRunCount < 0 || value.GapRunCount < 0 ||
            value.CompleteRunCount + value.GapRunCount != value.RunCount || value.FailedRunCount < 0 || value.FailedRunCount > value.GapRunCount ||
            value.LostRunCount < 0 || value.LostRunCount > value.GapRunCount || value.MissingPayloadRunCount < 0 || value.MissingPayloadRunCount > value.GapRunCount ||
            value.EmptyRunCount < 0 || value.EmptyRunCount > value.CompleteRunCount || value.RetainedFileCount is < 0 or > MaximumRetainedFiles ||
            value.RetentionDays is <= 0 || value.GapRunCount > 0 && !value.HasVisibilityGap)
            throw new ArgumentException("File-history coverage is inconsistent.", nameof(value));
    }
    public static void Receipt(DatabaseFileWindowHistoryReceipt value, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, long currentRevision)
    {
        ArgumentNullException.ThrowIfNull(value); Id(value.RunId); Revision(value.SampleTargetRevision); Utc(value.CompletedAtUtc);
        if (value.SampleTargetRevision > currentRevision || value.CompletedAtUtc < from || value.CompletedAtUtc >= to || value.CompletedAtUtc > cutoff ||
            value.Outcome is not ("succeeded" or "partial" or "timed_out" or "transient_failure" or "permanent_failure" or "permission_denied" or "unsupported" or "output_invalid" or "lease_lost" or "circuit_open") ||
            value.LossKind is not (null or "none" or "source_row_limit" or "response_byte_limit" or "output_validation_failure" or "ingestion_rejection") ||
            value.MinimumLostItems is < 0 or > int.MaxValue || value.ExpectedFileCount is < 0 or > MaximumFilesPerRun || value.StoredFileCount is < 0 or > MaximumFilesPerRun ||
            value.State is not ("available" or "partial" or "unavailable") ||
            value.Reason is not ("observed_files" or "observed_empty" or "collection_failed" or "collection_loss" or "collection_gap" or "source_schema_unsupported" or "payload_missing" or "stored_count_mismatch" or "source_clock_skew"))
            throw new ArgumentException("Invalid file source receipt.", nameof(value));
        bool hasSource = value.StoredFileCount > 0;
        if (hasSource != value.FirstObservedAtUtc.HasValue || hasSource != value.LastObservedAtUtc.HasValue ||
            hasSource != value.FirstCollectedAtUtc.HasValue || hasSource != value.LastCollectedAtUtc.HasValue)
            throw new ArgumentException("File source clocks must describe retained rows.", nameof(value));
        bool skew = false;
        if (hasSource)
        {
            Utc(value.FirstObservedAtUtc!.Value); Utc(value.LastObservedAtUtc!.Value);
            Utc(value.FirstCollectedAtUtc!.Value); Utc(value.LastCollectedAtUtc!.Value);
            if (value.FirstObservedAtUtc > value.LastObservedAtUtc || value.FirstCollectedAtUtc > value.LastCollectedAtUtc || value.LastCollectedAtUtc > cutoff)
                throw new ArgumentException("Invalid retained source clock range.", nameof(value));
            skew = value.LastObservedAtUtc > cutoff || (value.FirstObservedAtUtc.Value - value.FirstCollectedAtUtc.Value).Duration() > TimeSpan.FromMinutes(5) ||
                (value.LastObservedAtUtc.Value - value.LastCollectedAtUtc.Value).Duration() > TimeSpan.FromMinutes(5);
        }
        if (value.State == "available" && (value.Outcome != "succeeded" || value.LossKind != "none" || value.MinimumLostItems != 0 ||
            value.ExpectedFileCount != value.StoredFileCount || skew || value.Reason != (hasSource ? "observed_files" : "observed_empty")) ||
            value.Reason is "observed_files" or "observed_empty" && value.State != "available" ||
            value.Reason == "source_clock_skew" && (!hasSource || value.State == "available"))
            throw new ArgumentException("Incomplete file source was called available.", nameof(value));
    }
    public static void File(DatabaseFileWindowHistoryFile value, DateTimeOffset cutoff)
    {
        ArgumentNullException.ThrowIfNull(value); _ = new SqlServerObjectName(value.LogicalName);
        Utc(value.ObservedAtUtc); Utc(value.CollectedAtUtc);
        if (value.DatabaseId is < 1 or > 32767 || value.FileId is < 1 or > 65535 || value.CollectedAtUtc > cutoff ||
            value.FileType is not ("rows" or "log" or "filestream" or "fulltext" or "other") ||
            value.FileState is not ("online" or "restoring" or "recovering" or "recovery_pending" or "suspect" or "emergency" or "offline" or "defunct" or "other") || value.GrowthPercent is < 0 or > 100)
            throw new ArgumentException("Invalid retained file fact.", nameof(value));
        long size = Number(value.SizeBytes), growth = Number(value.GrowthBytes), totalStall = Number(value.IoStallMilliseconds);
        _ = Number(value.ReadCount); _ = Number(value.WriteCount); _ = Number(value.BytesRead); _ = Number(value.BytesWritten);
        if (value.MaximumSizeBytes is { } max && Number(max) < size || (value.ReadStallMilliseconds is not null) != (value.WriteStallMilliseconds is not null))
            throw new ArgumentException("Invalid retained file bounds.", nameof(value));
        if (value.ReadStallMilliseconds is { } read && value.WriteStallMilliseconds is { } write &&
            (Number(read) > totalStall || Number(write) != totalStall - Number(read)))
            throw new ArgumentException("Directional stalls do not equal total stalls.", nameof(value));
        string expectedMode = growth > 0 ? value.GrowthPercent > 0 ? "ambiguous" : "fixed_bytes" : value.GrowthPercent > 0 ? "percent" : "disabled";
        if (value.GrowthMode != expectedMode) throw new ArgumentException("Growth mode diverged from retained values.", nameof(value));
    }
}
