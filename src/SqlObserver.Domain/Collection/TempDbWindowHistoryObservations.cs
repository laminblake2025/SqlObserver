using System.Globalization;


namespace SqlObserver.Domain.Collection;

/// <summary>Completion-window source accounting; available is not a TempDB health verdict.</summary>
public sealed record TempDbWindowHistoryCoverage(int RunCount, int CompleteRunCount, int GapRunCount,
    int FailedRunCount, int LostRunCount, int MissingPayloadRunCount, int EmptyRunCount,
    int RetainedFileCount, bool HasVisibilityGap);
public sealed record TempDbWindowHistoryReceipt(Guid RunId, long SampleTargetRevision, DateTimeOffset CompletedAtUtc,
    string Outcome, string? LossKind, long? MinimumLostItems, int? ExpectedFileCount, int StoredFileCount,
    bool SummaryPresent, DateTimeOffset? ObservedAtUtc, string State, string Reason);
public sealed record TempDbWindowHistorySummary(DateTimeOffset ObservedAtUtc, int SourceState,
    string? TotalBytes, string? UsedBytes, string? LogTotalBytes, string? LogUsedBytes, bool Truncated);
public sealed record TempDbWindowHistoryFile(int FileId, string SizeBytes, string UsedBytes, string FreeBytes,
    int SourceState, DateTimeOffset ObservedAtUtc);

public static class TempDbWindowHistoryValidation
{
    public const long MaximumRevision = 9007199254740991;
    public const int MaximumPageSize = 100, MaximumRuns = 10000, MaximumFilesPerRun = 10000, MaximumRetainedFiles = 250000;
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(7), CursorLifetime = TimeSpan.FromMinutes(15);
    public static void Id(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A nonempty identity is required."); }
    public static void Revision(long value) { if (value is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Utc(DateTimeOffset value) => DomainValidation.RequireUtcMicrosecondAligned(value, nameof(value));
    public static void Window(DateTimeOffset from, DateTimeOffset to)
    { Utc(from); Utc(to); if (from >= to || to - from > MaximumWindow) throw new ArgumentException("Invalid bounded window."); }
    public static void Digest(string value)
    { if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("Invalid evidence digest."); }
    public static long Number(string value)
    { if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 0 || n.ToString(CultureInfo.InvariantCulture) != value) throw new ArgumentException("Invalid exact Int64 fact."); return n; }
    public static void Coverage(TempDbWindowHistoryCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RunCount is < 0 or > MaximumRuns || value.CompleteRunCount < 0 || value.GapRunCount < 0 ||
            value.CompleteRunCount + value.GapRunCount != value.RunCount || value.FailedRunCount < 0 || value.FailedRunCount > value.GapRunCount ||
            value.LostRunCount < 0 || value.LostRunCount > value.GapRunCount || value.MissingPayloadRunCount < 0 || value.MissingPayloadRunCount > value.GapRunCount ||
            value.EmptyRunCount < 0 || value.EmptyRunCount > value.CompleteRunCount || value.RetainedFileCount is < 0 or > MaximumRetainedFiles ||
            value.GapRunCount > 0 && !value.HasVisibilityGap) throw new ArgumentException("Invalid source accounting.");
    }
    public static void Receipt(TempDbWindowHistoryReceipt value, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, long currentRevision)
    {
        ArgumentNullException.ThrowIfNull(value); Id(value.RunId); Revision(value.SampleTargetRevision); Utc(value.CompletedAtUtc);
        if (value.SampleTargetRevision > currentRevision || value.CompletedAtUtc < from || value.CompletedAtUtc >= to || value.CompletedAtUtc > cutoff ||
            value.Outcome is not ("succeeded" or "partial" or "timed_out" or "transient_failure" or "permanent_failure" or "permission_denied" or "unsupported" or "output_invalid" or "lease_lost" or "circuit_open") ||
            value.LossKind is not (null or "none" or "source_row_limit" or "response_byte_limit" or "output_validation_failure" or "ingestion_rejection") ||
            value.MinimumLostItems is < 0 or > int.MaxValue || value.ExpectedFileCount is < 0 or > MaximumFilesPerRun || value.StoredFileCount is < 0 or > MaximumFilesPerRun ||
            value.State is not ("available" or "partial" or "unavailable") || value.Reason is not ("observed_files" or "observed_empty" or "collection_failed" or "collection_loss" or "collection_gap" or "source_schema_unsupported" or "payload_missing" or "stored_count_mismatch" or "source_clock_skew" or "source_partial") ||
            value.SummaryPresent != (value.ObservedAtUtc is not null))
            throw new ArgumentException("Invalid retained TempDB receipt.");
        if (value.ObservedAtUtc is { } observed) Utc(observed);
        bool complete = value.State == "available";
        if (complete && (value.Outcome != "succeeded" || value.LossKind != "none" || value.MinimumLostItems != 0 || !value.SummaryPresent ||
            value.ExpectedFileCount != value.StoredFileCount || value.Reason != (value.StoredFileCount == 0 ? "observed_empty" : "observed_files") ||
            value.ObservedAtUtc > cutoff || (value.ObservedAtUtc!.Value - value.CompletedAtUtc).Duration() > TimeSpan.FromMinutes(5)))
            throw new ArgumentException("Incomplete evidence was presented as available.");
        if (!complete && value.Reason is "observed_files" or "observed_empty") throw new ArgumentException("Available reason requires complete evidence.");
    }
    public static void Summary(TempDbWindowHistorySummary value)
    {
        ArgumentNullException.ThrowIfNull(value); Utc(value.ObservedAtUtc);
        if (value.SourceState is < 1 or > 6) throw new ArgumentException("Invalid source state.");
        if (value.TotalBytes is not null) _ = Number(value.TotalBytes);
        if (value.UsedBytes is not null) _ = Number(value.UsedBytes);
        if (value.LogTotalBytes is not null) _ = Number(value.LogTotalBytes);
        if (value.LogUsedBytes is not null) _ = Number(value.LogUsedBytes);
    }
    public static void File(TempDbWindowHistoryFile value)
    {
        ArgumentNullException.ThrowIfNull(value); Utc(value.ObservedAtUtc);
        if (value.FileId is < 1 or > 65535 || value.SourceState is < 1 or > 4) throw new ArgumentException("Invalid TempDB file identity/state.");
        _ = Number(value.SizeBytes); _ = Number(value.UsedBytes); _ = Number(value.FreeBytes);
    }
}
