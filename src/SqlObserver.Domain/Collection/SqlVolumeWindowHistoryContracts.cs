using System.Globalization;

namespace SqlObserver.Domain.Collection;

public sealed record SqlVolumeWindowHistoryCoverage(int RunCount, int CompleteRunCount,
    int GapRunCount, int RetainedObservationCount, int? RetentionDays, bool HasVisibilityGap);

public sealed record SqlVolumeWindowHistoryCatalogItem(long SampleTargetRevision, string VolumeKey,
    string IdentityKind, int ObservationCount, DateTimeOffset FirstCompletedAtUtc,
    DateTimeOffset LastCompletedAtUtc, string? FirstTotalBytes, string? FirstAvailableBytes,
    string? LastTotalBytes, string? LastAvailableBytes, string ComparisonState,
    string? AvailableBytesDelta, string? TotalBytesDelta);

public sealed record SqlVolumeWindowHistorySample(Guid RunId, DateTimeOffset CompletedAtUtc,
    DateTimeOffset? ObservedAtUtc, DateTimeOffset? CollectedAtUtc, string Outcome,
    string? LossKind, long? MinimumLostItems, string State, string Reason,
    int? MappedFileCount, string? TotalBytes, string? AvailableBytes);

/// <summary>Bounds and exact identity rules shared by read requests and native evidence validation.</summary>
public static class SqlVolumeWindowHistoryValidation
{
    public const long MaximumRevision = 9_007_199_254_740_991;
    public const int MaximumRuns = 10_000;
    public const int MaximumIdentities = 1_000;
    public const int MaximumRetainedObservations = 250_000;
    public static void Id(Guid value)
    { if (value == Guid.Empty) throw new ArgumentException("A nonempty target or run UUID is required.", nameof(value)); }
    public static void Revision(long value)
    { if (value is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Utc(DateTimeOffset value)
    { _ = DomainValidation.RequireUtcMicrosecondAligned(value, nameof(value)); }
    public static void Window(DateTimeOffset from, DateTimeOffset to)
    {
        Utc(from); Utc(to);
        if (from >= to || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("A nonempty UTC window of at most 31 days is required.");
    }
    public static void Key(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("An opaque lowercase SHA-256 identity or digest is required.", nameof(value));
    }
    public static void Token(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 128 || value.Any(static c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("A bounded safe evidence token is required.", nameof(value));
    }
    public static void IdentityKind(string value)
    { if (value is not ("volume_id" or "mount_point" or "file_scoped_unknown")) throw new ArgumentException("Invalid volume identity kind.", nameof(value)); }
    public static long? Bytes(string? value, bool allowNegative = false)
    {
        if (value is null) return null;
        if (!long.TryParse(value, allowNegative ? NumberStyles.AllowLeadingSign : NumberStyles.None,
                CultureInfo.InvariantCulture, out long parsed) ||
            parsed.ToString(CultureInfo.InvariantCulture) != value || !allowNegative && parsed < 0)
            throw new ArgumentException("Capacity must be a canonical Int64 decimal string.", nameof(value));
        return parsed;
    }
    public static void Capacity(string? total, string? available)
    {
        long? t = Bytes(total), a = Bytes(available);
        if (t.HasValue != a.HasValue || t is <= 0 || a > t)
            throw new ArgumentException("Capacity must be an available pair or an unknown pair.");
    }
    public static void Coverage(SqlVolumeWindowHistoryCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.RunCount is < 0 or > MaximumRuns || coverage.CompleteRunCount < 0 || coverage.CompleteRunCount > coverage.RunCount || coverage.GapRunCount < 0 || coverage.GapRunCount > coverage.RunCount ||
            coverage.CompleteRunCount + coverage.GapRunCount != coverage.RunCount ||
            coverage.RetainedObservationCount is < 0 or > MaximumRetainedObservations ||
            coverage.RetentionDays is <= 0 || coverage.GapRunCount > 0 && !coverage.HasVisibilityGap)
            throw new ArgumentException("History coverage counts are inconsistent.", nameof(coverage));
    }
}
