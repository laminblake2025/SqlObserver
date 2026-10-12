using System.Globalization;

namespace SqlObserver.Domain.Collection;

public sealed record SqlVolumePlanningWindowCoverage(int RunCount, int CompleteRunCount, int GapRunCount,
    int FailedRunCount, int LostRunCount, int MissingPayloadRunCount, int EmptyRunCount,
    int RetainedVolumeCount, bool HasVisibilityGap);
public sealed record SqlVolumePlanningWindowReceipt(Guid RunId, long SampleTargetRevision,
    DateTimeOffset CompletedAtUtc, string Outcome, string? LossKind, int? MinimumLostItems,
    int ExpectedVolumeCount, int StoredVolumeCount, string State, string Reason);
public sealed record SqlVolumePlanningWindowDailySample(Guid RunId, DateTimeOffset CompletedAtUtc,
    string TotalBytes, string AvailableBytes);
public sealed record SqlVolumePlanningWindowForecast(string State, int SampleDays, double? DaysUntilFull,
    IReadOnlyList<SqlVolumePlanningWindowDailySample> DailySamples);
public sealed record SqlVolumePlanningWindowVolume(string VolumeKey, string IdentityKind, int MappedFileCount,
    DateTimeOffset ObservedAtUtc, DateTimeOffset CollectedAtUtc, string? TotalBytes, string? AvailableBytes,
    SqlVolumePlanningWindowForecast Forecast);
/// <summary>Inclusive completion-time lookback through the selected receipt, never through repository now.</summary>
public sealed record SqlVolumePlanningWindowLookback(DateTimeOffset FromUtc, DateTimeOffset AsOfUtc,
    string State, string Reason, SqlVolumePlanningWindowCoverage? Coverage);

public static class SqlVolumePlanningWindowValidation
{
    public const int MaximumRuns = 10_000;
    public const int MaximumVolumesPerRun = 1_000;
    public const int MaximumRetainedVolumes = 250_000;
    public const int MaximumPageSize = 100;
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(31);
    public static readonly TimeSpan LookbackDuration = TimeSpan.FromDays(14);
    public static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(15);
    public static void Id(Guid value) => SqlVolumeWindowHistoryValidation.Id(value);
    public static void Revision(long value) => SqlVolumeWindowHistoryValidation.Revision(value);
    public static void Utc(DateTimeOffset value) => SqlVolumeWindowHistoryValidation.Utc(value);
    public static void Digest(string value) => SqlVolumeWindowHistoryValidation.Key(value);
    public static void Window(DateTimeOffset from, DateTimeOffset to) => SqlVolumeWindowHistoryValidation.Window(from, to);
    public static void Coverage(SqlVolumePlanningWindowCoverage c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.RunCount is < 0 or > MaximumRuns || c.CompleteRunCount < 0 || c.GapRunCount < 0 ||
            c.CompleteRunCount + c.GapRunCount != c.RunCount || c.FailedRunCount < 0 || c.FailedRunCount > c.GapRunCount ||
            c.LostRunCount < 0 || c.LostRunCount > c.RunCount || c.MissingPayloadRunCount < 0 || c.MissingPayloadRunCount > c.GapRunCount ||
            c.EmptyRunCount < 0 || c.EmptyRunCount > c.CompleteRunCount || c.RetainedVolumeCount is < 0 or > MaximumRetainedVolumes ||
            c.GapRunCount > 0 && !c.HasVisibilityGap) throw new ArgumentException("Invalid volume planning coverage.");
    }
    public static void Receipt(SqlVolumePlanningWindowReceipt r)
    {
        ArgumentNullException.ThrowIfNull(r); Id(r.RunId); Revision(r.SampleTargetRevision); Utc(r.CompletedAtUtc);
        if (r.Outcome is not ("succeeded" or "partial" or "timed_out" or "transient_failure" or "permanent_failure" or
                "permission_denied" or "unsupported" or "output_invalid" or "lease_lost" or "circuit_open") ||
            r.LossKind is not (null or "none" or "source_row_limit" or "response_byte_limit" or "visibility_incomplete" or "output_validation_failure") ||
            r.MinimumLostItems is < 0 || r.ExpectedVolumeCount is < 0 or > MaximumVolumesPerRun || r.StoredVolumeCount is < 0 or > MaximumVolumesPerRun ||
            r.State is not ("available" or "partial" or "unavailable") ||
            r.Reason is not ("observed_volumes" or "observed_empty" or "collection_failed" or "collection_loss" or
                "payload_missing" or "stored_count_mismatch" or "source_clock_skew" or "source_schema_unsupported" or "capacity_unknown" or "collection_gap"))
            throw new ArgumentException("Invalid volume receipt.");
        if (r.State == "available" && (r.Outcome != "succeeded" || r.LossKind != "none" || r.MinimumLostItems != 0 ||
                r.ExpectedVolumeCount != r.StoredVolumeCount || r.Reason is not ("observed_empty" or "observed_volumes")) ||
            (r.Reason == "observed_empty") != (r.State == "available" && r.StoredVolumeCount == 0) ||
            r.Reason == "observed_volumes" && (r.State != "available" || r.StoredVolumeCount == 0))
            throw new ArgumentException("Volume receipt makes inconsistent coverage claims.");
    }
    public static void Volume(SqlVolumePlanningWindowVolume v, DateTimeOffset asOf, bool native = false)
    {
        ArgumentNullException.ThrowIfNull(v); Digest(v.VolumeKey); SqlVolumeWindowHistoryValidation.IdentityKind(v.IdentityKind);
        Utc(v.ObservedAtUtc); Utc(v.CollectedAtUtc); SqlVolumeWindowHistoryValidation.Capacity(v.TotalBytes, v.AvailableBytes);
        if (v.MappedFileCount is < 1 or > MaximumVolumesPerRun) throw new ArgumentException("Invalid mapped-file count.");
        var f = v.Forecast ?? throw new ArgumentException("Forecast evidence is missing.");
        if (f.State is not ("eligible" or "estimated" or "full" or "insufficient_history" or "not_depleting" or "unstable_trend" or
            "beyond_horizon" or "capacity_changed" or "capacity_unknown" or "identity_not_comparable" or "visibility_gaps" or "revision_boundary" or
            "lookback_overflow" or "selected_receipt_unavailable") || !native && f.State == "eligible" ||
            f.SampleDays is < 0 or > 15 || f.DailySamples is null || f.SampleDays != f.DailySamples.Count ||
            f.DaysUntilFull.HasValue && (!double.IsFinite(f.DaysUntilFull.Value) || f.DaysUntilFull is < 0 or > 3650) ||
            (f.State is "estimated" or "full") != f.DaysUntilFull.HasValue || f.State == "full" && f.DaysUntilFull != 0)
            throw new ArgumentException("Invalid planning estimate.");
        DateTimeOffset? previous = null;
        var runs = new HashSet<Guid>();
        foreach (var sample in f.DailySamples)
        {
            Id(sample.RunId); Utc(sample.CompletedAtUtc); SqlVolumeWindowHistoryValidation.Capacity(sample.TotalBytes, sample.AvailableBytes);
            if (sample.TotalBytes is null || sample.AvailableBytes is null) throw new ArgumentException("Daily capacity is unknown.");
            if (!runs.Add(sample.RunId) || sample.CompletedAtUtc < asOf - LookbackDuration || sample.CompletedAtUtc > asOf ||
                previous.HasValue && sample.CompletedAtUtc.UtcDateTime.Date <= previous.Value.UtcDateTime.Date ||
                sample.TotalBytes != v.TotalBytes) throw new ArgumentException("Invalid daily forecast sample.");
            previous = sample.CompletedAtUtc;
        }
        if (f.DailySamples.Count > 0 && (v.IdentityKind != "volume_id" || v.TotalBytes is null || v.AvailableBytes is null) ||
            f.State is "estimated" or "full" && v.IdentityKind != "volume_id") throw new ArgumentException("Unqualified volume identity estimate.");
    }
    public static string FormatBytes(long value) => value.ToString(CultureInfo.InvariantCulture);
}
