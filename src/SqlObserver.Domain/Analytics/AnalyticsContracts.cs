using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Domain.Analytics;

public enum MetricKind { Gauge = 1, Counter = 2 }
public enum RollupInterval { FiveMinutes = 1, Hour = 2, Day = 3 }

/// <summary>One immutable, privacy-neutral input to rollup-v1.</summary>
public sealed record MetricPoint
{
    public MetricPoint(DateTimeOffset observedAtUtc, string metricKey, double value,
        MetricKind kind = MetricKind.Gauge, IReadOnlyDictionary<string, string>? dimensions = null,
        bool reset = false, bool complete = true)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.", nameof(observedAtUtc));
        if (string.IsNullOrWhiteSpace(metricKey) || metricKey.Length > 128) throw new ArgumentException("Invalid metric key.", nameof(metricKey));
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        MetricKey = metricKey; ObservedAtUtc = observedAtUtc; Value = value; Kind = kind; Reset = reset; Complete = complete;
        Dimensions = CanonicalDimensions.Normalize(dimensions);
    }
    public DateTimeOffset ObservedAtUtc { get; }
    public string MetricKey { get; }
    public double Value { get; }
    public MetricKind Kind { get; }
    public bool Reset { get; }
    public bool Complete { get; }
    public IReadOnlyDictionary<string, string> Dimensions { get; }
}

public sealed record MetricCatalogEntry
{
    public MetricCatalogEntry(string key, string displayName, string unit, MetricKind kind,
        bool enabled = true, IReadOnlySet<string>? dimensionAllowlist = null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || !char.IsLetter(key[0]) || key.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Metric key is not a catalog token.", nameof(key));
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("Catalog labels are required.");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Key = key; DisplayName = displayName; Unit = unit; Kind = kind; Enabled = enabled;
        DimensionAllowlist = new HashSet<string>(dimensionAllowlist ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
    }
    public string Key { get; }
    public string DisplayName { get; }
    public string Unit { get; }
    public MetricKind Kind { get; }
    public bool Enabled { get; }
    public IReadOnlySet<string> DimensionAllowlist { get; }
    public bool AllowsDimensions(IReadOnlyDictionary<string, string> dimensions) => dimensions.Keys.All(DimensionAllowlist.Contains);
}

/// <summary>Version and digest are part of every computed result and must not drift silently.</summary>
public static class MetricCatalogV1
{
    public const int Version = 1;
    public const string ChecksumAlgorithm = "SHA-256";
    private static readonly MetricCatalogEntry[] Entries = new[]
    {
        new MetricCatalogEntry("host.cpu.percent", "CPU utilization", "percent", MetricKind.Gauge),
        new MetricCatalogEntry("host.memory.available_bytes", "Available memory", "bytes", MetricKind.Gauge),
        new MetricCatalogEntry("host.memory.committed_bytes", "Committed memory", "bytes", MetricKind.Gauge),
        new MetricCatalogEntry("host.volume.free_bytes", "Volume free space", "bytes", MetricKind.Gauge, dimensionAllowlist: new HashSet<string>(StringComparer.Ordinal) { "volume" }),
        new MetricCatalogEntry("host.volume.total_bytes", "Volume total space", "bytes", MetricKind.Gauge, dimensionAllowlist: new HashSet<string>(StringComparer.Ordinal) { "volume" }),
        new MetricCatalogEntry("host.volume.queue_length", "Volume disk queue length", "count", MetricKind.Gauge, dimensionAllowlist: new HashSet<string>(StringComparer.Ordinal) { "volume" }),
        new MetricCatalogEntry("host.volume.read_latency_ms", "Volume read latency", "milliseconds", MetricKind.Gauge, dimensionAllowlist: new HashSet<string>(StringComparer.Ordinal) { "volume" }),
        new MetricCatalogEntry("host.volume.write_latency_ms", "Volume write latency", "milliseconds", MetricKind.Gauge, dimensionAllowlist: new HashSet<string>(StringComparer.Ordinal) { "volume" }),
        new MetricCatalogEntry("replication.pending_commands", "Pending replication commands", "count", MetricKind.Gauge),
        new MetricCatalogEntry("replication.latency_seconds", "Replication latency", "seconds", MetricKind.Gauge),
    };
    public static IReadOnlyList<MetricCatalogEntry> All => Entries;
    public static string Checksum { get; } = ComputeChecksum(Entries);
    public static MetricCatalogEntry Get(string key) => TryGet(key, out MetricCatalogEntry? entry) ? entry! : throw new KeyNotFoundException("Metric is not in catalog-v1.");
    public static bool TryGet(string key, out MetricCatalogEntry? entry) { entry = Entries.SingleOrDefault(x => x.Key == key); return entry is not null; }
    public static bool IsGaugeDimensionAllowed(string metricKey, string dimensionKey) => TryGet(metricKey, out MetricCatalogEntry? entry) && entry!.Kind == MetricKind.Gauge && entry.AllowsDimensions(new Dictionary<string, string> { [dimensionKey] = "_" });
    private static string ComputeChecksum(IEnumerable<MetricCatalogEntry> entries)
    {
        // Keep this representation identical to the database seed: every
        // executable field (including source/aggregation and dimensions) is
        // covered by the catalog digest.
        string canonical = string.Join("\n", entries.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
            $"{x.Key}|{x.DisplayName}|{x.Unit}|{(x.Key.StartsWith("replication.", StringComparison.Ordinal) ? "replication" : "host")}|{(x.Kind == MetricKind.Counter ? "sum" : "gauge")}|{x.Enabled.ToString().ToLowerInvariant()}|{string.Join(",", x.DimensionAllowlist.OrderBy(d => d, StringComparer.Ordinal))}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public static class CanonicalDimensions
{
    public static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? dimensions)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in dimensions ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64 || pair.Key.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
                throw new ArgumentException("Dimension key is not allowlisted.", nameof(dimensions));
            if (pair.Value is null || pair.Value.Length > 256 || pair.Value.Any(char.IsControl)) throw new ArgumentException("Dimension value is invalid.", nameof(dimensions));
            if (!result.TryAdd(pair.Key, pair.Value)) throw new ArgumentException("Dimension keys must be unique.", nameof(dimensions));
        }
        return new ReadOnlyDictionary<string, string>(result);
    }
    public static string Json(IReadOnlyDictionary<string, string>? dimensions) => JsonSerializer.Serialize(Normalize(dimensions));
    public static string Sha256(IReadOnlyDictionary<string, string>? dimensions) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(dimensions)))).ToLowerInvariant();
}

public sealed record RollupResult
{
    /// <summary>The physical interval represented by this row.  It is part of
    /// the identity so a five-minute sample can never be relabelled as hourly.
    /// </summary>
    public RollupInterval Interval { get; init; } = RollupInterval.FiveMinutes;
    public required DateTimeOffset BucketStartUtc { get; init; }
    public required DateTimeOffset BucketEndUtc { get; init; }
    public required string MetricKey { get; init; }
    public required string DimensionsSha256 { get; init; }
    public required int Count { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Sum { get; init; }
    public double? Mean { get; init; }
    public double? Last { get; init; }
    public double? CounterDelta { get; init; }
    public double? RatePerSecond { get; init; }
    public int Expected { get; init; }
    /// <summary>Computation generation is part of the immutable rollup identity.</summary>
    public long Generation { get; init; } = 1;
    public double Coverage => Expected <= 0 ? 0 : Math.Min(1, (double)Count / Expected);
    public int ResetCount { get; init; }
    public int GapCount { get; init; }
    public bool Truncated { get; init; }
    public DateTimeOffset? SourceCutoffUtc { get; init; }
    public IReadOnlyDictionary<string, string> Dimensions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public string VisibilityState { get; init; } = "complete";
    public static int CatalogVersion => MetricCatalogV1.Version;
    public static string AlgorithmVersion => "rollup-v1";

    /// <summary>Validates the persistence envelope before it crosses the database boundary.</summary>
    public void Validate()
    {
        if (BucketStartUtc.Offset != TimeSpan.Zero || BucketEndUtc.Offset != TimeSpan.Zero || BucketEndUtc <= BucketStartUtc)
            throw new ArgumentException("Rollup buckets must be non-empty UTC intervals.");
        if (string.IsNullOrWhiteSpace(MetricKey) || MetricKey.Length > 128 || !char.IsLower(MetricKey[0]) || !MetricKey.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("Invalid rollup metric key.");
        if (DimensionsSha256 is null || DimensionsSha256.Length != 64 || DimensionsSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A SHA-256 dimension hash is required.");
        // The dimension payload and its identity are one contract.  Accepting
        // a mismatched pair would allow a volume's values to be relabelled as
        // another volume at the persistence boundary.
        if (!string.Equals(CanonicalDimensions.Sha256(Dimensions), DimensionsSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Rollup dimensions do not match their SHA-256 identity.", nameof(Dimensions));
        if (Count < 0 || Expected < 0 || Generation < 1 || ResetCount < 0 || GapCount < 0 || (Expected == 0 && Coverage != 0) || !IsVisibility(VisibilityState))
            throw new ArgumentOutOfRangeException(nameof(Count));
        foreach (double? value in new[] { Min, Max, Sum, Mean, Last, CounterDelta, RatePerSecond })
            if (value is not null && !double.IsFinite(value.Value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (Min is not null && Max is not null && Min > Max) throw new ArgumentException("Rollup minimum exceeds maximum.");
        if (SourceCutoffUtc is not null && SourceCutoffUtc.Value.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.");
    }
    private static bool IsVisibility(string value) => value is "complete" or "partial" or "unavailable" or "unsupported";
}

public sealed record BaselineResult
{
    public required string MetricKey { get; init; }
    /// <summary>Dimension identity is retained so baselines never mix volumes.</summary>
    public string DimensionsSha256 { get; init; } = CanonicalDimensions.Sha256(null);
    public IReadOnlyDictionary<string, string> Dimensions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public required int HourOfWeek { get; init; }
    public required int CompleteDays { get; init; }
    public required int SampleCount { get; init; }
    public double? Mean { get; init; }
    public double? Stddev { get; init; }
    public double? Median { get; init; }
    public double? Mad { get; init; }
    public double? P10 { get; init; }
    public double? P90 { get; init; }
    public double Coverage { get; init; }
    public double Confidence { get; init; }
    public double? LowerBound { get; init; }
    public double? UpperBound { get; init; }
    /// <summary>Exact persisted baseline window. It is optional for algorithm output but required when persisted.</summary>
    public DateTimeOffset? WindowStartUtc { get; init; }
    public DateTimeOffset? WindowEndUtc { get; init; }
    public long Generation { get; init; } = 1;
    public string VisibilityState { get; init; } = "complete";
    public static string AlgorithmVersion => "baseline-v1";
}

public sealed record ForecastResult
{
    public required string MetricKey { get; init; }
    /// <summary>Opaque dimension identity for dimension-scoped forecasts.</summary>
    public string DimensionsSha256 { get; init; } = CanonicalDimensions.Sha256(null);
    public IReadOnlyDictionary<string, string> Dimensions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public required DateTimeOffset HorizonStartUtc { get; init; }
    public required DateTimeOffset HorizonEndUtc { get; init; }
    public double? Estimate { get; init; }
    public double? LowerBound { get; init; }
    public double? UpperBound { get; init; }
    public double? SlopePerDay { get; init; }
    public double Confidence { get; init; }
    public double Residual { get; init; }
    public Guid? ForecastId { get; init; }
    public string Model { get; init; } = "forecast-v1";
    public long SourceGeneration { get; init; } = 1;
    public string VisibilityState { get; init; } = "complete";
    public static string AlgorithmVersion => "forecast-v1";
    public bool Available => Estimate is not null;
}

public sealed record WindowComparisonResult
{
    public double? LeftValue { get; init; }
    public double? RightValue { get; init; }
    public double? Delta { get; init; }
    public double? Percent { get; init; }
    public long LeftSamples { get; init; }
    public long RightSamples { get; init; }
    public bool Complete { get; init; }
}

public sealed record EvidenceReference
{
    public EvidenceReference(string type, string identity, DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 64 || string.IsNullOrWhiteSpace(identity) || identity.Length > 256) throw new ArgumentException("Evidence reference is invalid.");
        if (occurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.", nameof(occurredAtUtc));
        Type = type; Identity = identity; OccurredAtUtc = occurredAtUtc;
    }
    public string Type { get; }
    public string Identity { get; }
    public DateTimeOffset OccurredAtUtc { get; }
}

public sealed record EvidencePacket
{
    public required Guid PacketId { get; init; }
    /// <summary>Target identity is carried by persisted packets, while older in-memory producers may leave it unset.</summary>
    public Guid? TargetId { get; init; }
    public long TargetRevision { get; init; }
    public required DateTimeOffset WindowStartUtc { get; init; }
    public required DateTimeOffset WindowEndUtc { get; init; }
    public required IReadOnlyList<EvidenceReference> References { get; init; }
    public required string IdentitySha256 { get; init; }
    public required string SourceCutoffSha256 { get; init; }
    public string SourceDigest { get; init; } = "";
    public DateTimeOffset? SourceCutoffUtc { get; init; }
    public IReadOnlyList<string> Tombstones { get; init; } = Array.Empty<string>();
    public required double Confidence { get; init; }
    public Guid? SourceRunId { get; init; }
    public string EvidenceKind { get; init; } = "metric";
    public string Trigger { get; init; } = "metric";
    public string Algorithm { get; init; } = "evidence-v1";
    public long Generation { get; init; } = 1;
    public string State { get; init; } = "complete";
    public bool Truncated { get; init; }
    public IReadOnlyDictionary<string, object?> Evidence { get; init; } = new Dictionary<string, object?>();
    public static string AlgorithmVersion => "evidence-v1";
}

public sealed record IncidentGeneration(Guid ThreadId, long Generation, DateTimeOffset ObservedAtUtc, string CorrelationSha256, bool SupersedesPrevious)
{
    public Guid? EvidencePacketId { get; init; }
    public string State { get; init; } = "open";
    public IReadOnlyDictionary<string, object?> Details { get; init; } = new Dictionary<string, object?>();
}
public sealed record IncidentThread(Guid ThreadId, DateTimeOffset OpenedAtUtc, DateTimeOffset? ClosedAtUtc, IReadOnlyList<EvidencePacket> Packets, long CurrentGeneration)
{
    /// <summary>Append-only generation links associated with this thread.</summary>
    public IReadOnlyList<IncidentGeneration> Generations { get; init; } = Array.Empty<IncidentGeneration>();
}
