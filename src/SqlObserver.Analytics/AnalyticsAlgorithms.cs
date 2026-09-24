using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Analytics;

namespace SqlObserver.Analytics;

public static class RollupV1
{
    public static DateTimeOffset BucketStart(DateTimeOffset timestampUtc, RollupInterval interval) => Floor(timestampUtc, interval);
    public static DateTimeOffset BucketStartUtc(DateTimeOffset timestampUtc, TimeSpan interval) => Floor(timestampUtc, interval == TimeSpan.FromMinutes(5) ? RollupInterval.FiveMinutes : interval == TimeSpan.FromHours(1) ? RollupInterval.Hour : interval == TimeSpan.FromDays(1) ? RollupInterval.Day : throw new ArgumentOutOfRangeException(nameof(interval)));
    public static DateTimeOffset Floor(DateTimeOffset timestampUtc, RollupInterval interval)
    {
        if (timestampUtc.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.", nameof(timestampUtc));
        int minutes = interval switch { RollupInterval.FiveMinutes => 5, RollupInterval.Hour => 60, RollupInterval.Day => 1440, _ => throw new ArgumentOutOfRangeException(nameof(interval)) };
        long ticks = timestampUtc.UtcTicks;
        long bucketTicks = TimeSpan.TicksPerMinute * minutes;
        return new DateTimeOffset(ticks - ticks % bucketTicks, TimeSpan.Zero);
    }

    public static RollupResult Compute(IEnumerable<MetricPoint> source, RollupInterval interval, int expectedPerBucket = 0,
        DateTimeOffset? sourceCutoffUtc = null, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceCutoffUtc is not null && sourceCutoffUtc.Value.Offset != TimeSpan.Zero) throw new ArgumentException("Source cutoff must be UTC.", nameof(sourceCutoffUtc));
        var points = source.OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.MetricKey, StringComparer.Ordinal).ToList();
        if (points.Count == 0) throw new ArgumentException("At least one point is required.", nameof(source));
        var first = points[0];
        if (!MetricCatalogV1.TryGet(first.MetricKey, out MetricCatalogEntry? catalog) || !catalog!.Enabled) throw new InvalidDataException("Metric is not enabled in catalog-v1.");
        if (!catalog.AllowsDimensions(first.Dimensions)) throw new InvalidDataException("Metric dimensions are not in the catalog allowlist.");
        if (points.Any(x => x.MetricKey != first.MetricKey || CanonicalDimensions.Sha256(x.Dimensions) != CanonicalDimensions.Sha256(first.Dimensions))) throw new ArgumentException("Rollup points must share metric and dimensions.", nameof(source));
        DateTimeOffset start = Floor(first.ObservedAtUtc, interval);
        DateTimeOffset end = interval switch { RollupInterval.FiveMinutes => start.AddMinutes(5), RollupInterval.Hour => start.AddHours(1), RollupInterval.Day => start.AddDays(1), _ => throw new ArgumentOutOfRangeException(nameof(interval)) };
        int resets = 0;
        int gaps = 0;
        var values = new List<double>();
        var deltas = new List<double>();
        var rates = new List<double>();
        double? previous = null; DateTimeOffset? previousTime = null;
        foreach (MetricPoint point in points)
        {
            if (point.ObservedAtUtc < start || point.ObservedAtUtc >= end || !point.Complete) continue;
            if (previousTime is not null)
            {
                var cadence = end - start;
                if (point.ObservedAtUtc - previousTime.Value > cadence) gaps++;
            }
            if (catalog.Kind == MetricKind.Counter)
            {
                if (previous is not null && !point.Reset && point.Value >= previous.Value)
                {
                    double delta = point.Value - previous.Value;
                    double rate = delta / Math.Max((point.ObservedAtUtc - previousTime!.Value).TotalSeconds, 1);
                    deltas.Add(delta); rates.Add(rate); values.Add(rate);
                }
                else if (previous is not null && (point.Reset || point.Value < previous.Value)) resets++;
            }
            else values.Add(point.Value);
            previous = point.Value; previousTime = point.ObservedAtUtc;
        }
        if (expectedPerBucket <= 0) expectedPerBucket = catalog.Kind == MetricKind.Counter ? Math.Max(1, points.Count - 1) : points.Count;
        return new RollupResult
        {
            Interval = interval, BucketStartUtc = start, BucketEndUtc = end, MetricKey = first.MetricKey,
            DimensionsSha256 = CanonicalDimensions.Sha256(first.Dimensions), Dimensions = first.Dimensions, Count = values.Count,
            Min = values.Count == 0 ? null : values.Min(), Max = values.Count == 0 ? null : values.Max(),
            Sum = values.Count == 0 ? null : catalog.Kind == MetricKind.Counter ? deltas.Sum() : values.Sum(), Mean = values.Count == 0 ? null : values.Average(),
            Last = values.Count == 0 ? null : values[^1], Expected = expectedPerBucket, ResetCount = resets,
            CounterDelta = deltas.Count == 0 ? null : deltas.Sum(), RatePerSecond = rates.Count == 0 ? null : rates.Average(),
            GapCount = gaps, Truncated = truncated, SourceCutoffUtc = sourceCutoffUtc
        };
    }
    public static IReadOnlyList<RollupResult> ComputeMany(IEnumerable<MetricPoint> points, RollupInterval interval, int expectedPerBucket = 0, DateTimeOffset? sourceCutoffUtc = null, bool truncated = false)
    {
        return points.GroupBy(x => (x.MetricKey, Dimensions: CanonicalDimensions.Sha256(x.Dimensions))).SelectMany(g => g.GroupBy(x => Floor(x.ObservedAtUtc, interval)).Select(bucket => Compute(bucket, interval, expectedPerBucket, sourceCutoffUtc, truncated))).OrderBy(x => x.BucketStartUtc).ThenBy(x => x.MetricKey, StringComparer.Ordinal).ToArray();
    }
}

public static class BaselineV1
{
    public static IReadOnlyList<BaselineResult> Compute(IEnumerable<RollupResult> hourlyRollups, DateTimeOffset asOfUtc, double minimumCoverage = .8)
    {
        ArgumentNullException.ThrowIfNull(hourlyRollups);
        if (asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.", nameof(asOfUtc));
        if (minimumCoverage is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumCoverage));
        // DateTimeOffset.Date returns an Unspecified DateTime.  Calling
        // ToUniversalTime on that value consults the host's local timezone,
        // which would make the same input produce different windows on hosts
        // in different zones.  Construct the UTC cutoff explicitly.
        // The cutoff is the current UTC calendar-day boundary.  Filtering
        // hourly buckets strictly before it includes the immediately previous
        // complete day while excluding the in-progress current day.
        DateTimeOffset cutoff = new(asOfUtc.UtcDateTime.Date, TimeSpan.Zero);
        DateTimeOffset earliest = cutoff.AddDays(-28);
        var points = hourlyRollups.Where(x =>
            // A legacy in-memory row may omit Interval while still carrying
            // an hour-sized bucket. Production rows always carry the physical
            // interval, so five-minute rows cannot enter a baseline.
            (x.Interval == RollupInterval.Hour || x.BucketEndUtc - x.BucketStartUtc == TimeSpan.FromHours(1)) &&
            x.BucketStartUtc >= earliest && x.BucketStartUtc < cutoff && x.Mean is not null && !x.Truncated && x.Coverage >= minimumCoverage).ToArray();
        return points.GroupBy(x => (x.MetricKey, x.DimensionsSha256)).SelectMany(metric =>
        {
            var completeDays = metric.GroupBy(x => x.BucketStartUtc.UtcDateTime.Date).Where(day => day.Select(x => x.BucketStartUtc.UtcDateTime.Hour).Distinct().Count() == 24).Select(x => x.Key).ToHashSet();
            return metric.Where(x => completeDays.Contains(x.BucketStartUtc.UtcDateTime.Date)).GroupBy(x => (x.MetricKey, x.DimensionsSha256, Hour: (int)x.BucketStartUtc.UtcDateTime.DayOfWeek * 24 + x.BucketStartUtc.UtcDateTime.Hour)).Select(g =>
            {
            double[] values = g.Select(x => x.Mean!.Value).OrderBy(x => x).ToArray();
            double median = Percentile(values, .5); double mad = Percentile(values.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToArray(), .5);
            double coverage = Math.Min(1, completeDays.Count / 28d); double confidence = Math.Min(1, coverage * Math.Min(1, values.Length / 8d));
            RollupResult exemplar = g.First();
            return new BaselineResult { MetricKey = g.Key.MetricKey, DimensionsSha256 = g.Key.DimensionsSha256, Dimensions = exemplar.Dimensions, HourOfWeek = g.Key.Hour, CompleteDays = completeDays.Count, SampleCount = values.Length, Median = median, Mad = mad, P10 = Percentile(values, .1), P90 = Percentile(values, .9), Coverage = coverage, Confidence = confidence, WindowStartUtc = earliest, WindowEndUtc = cutoff };
            });
        }).OrderBy(x => x.MetricKey, StringComparer.Ordinal).ThenBy(x => x.DimensionsSha256, StringComparer.Ordinal).ThenBy(x => x.HourOfWeek).ToArray();
    }
    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return double.NaN; if (values.Length == 1) return values[0];
        double position = (values.Length - 1) * p; int low = (int)Math.Floor(position), high = (int)Math.Ceiling(position); return values[low] + (values[high] - values[low]) * (position - low);
    }
}

public static class WindowComparisonV1
{
    public static WindowComparisonResult Compare(IEnumerable<RollupResult> rollups, DateTimeOffset leftStartUtc, DateTimeOffset leftEndUtc, DateTimeOffset rightStartUtc, DateTimeOffset rightEndUtc)
    {
        if (leftStartUtc.Offset != TimeSpan.Zero || leftEndUtc.Offset != TimeSpan.Zero || rightStartUtc.Offset != TimeSpan.Zero || rightEndUtc.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.");
        if (leftEndUtc <= leftStartUtc || rightEndUtc <= rightStartUtc || leftEndUtc - leftStartUtc != rightEndUtc - rightStartUtc || rightEndUtc - rightStartUtc > TimeSpan.FromDays(31) || leftStartUtc < rightEndUtc && rightStartUtc < leftEndUtc) throw new ArgumentException("Windows must be equal, non-overlapping, and at most 31 days.");
        ArgumentNullException.ThrowIfNull(rollups);
        var all = rollups.ToArray();
        if (all.Length > 0 && all.Any(x => !MetricCatalogV1.TryGet(x.MetricKey, out MetricCatalogEntry? entry) || !entry!.Enabled))
            throw new InvalidDataException("Comparison metric is not enabled in catalog-v1.");
        var left = all.Where(x => x.BucketStartUtc >= leftStartUtc && x.BucketStartUtc < leftEndUtc).ToArray();
        var right = all.Where(x => x.BucketStartUtc >= rightStartUtc && x.BucketStartUtc < rightEndUtc).ToArray();
        double? l = Aggregate(left), r = Aggregate(right);
        return new WindowComparisonResult
        {
            LeftValue = l,
            RightValue = r,
            Delta = l is null || r is null ? null : r - l,
            // A zero reference has no meaningful relative change. Keep it null
            // instead of manufacturing an infinity/100% value.
            Percent = l is null || r is null || l == 0 ? null : (r.Value - l.Value) / Math.Abs(l.Value) * 100,
            LeftSamples = left.Sum(x => (long)Math.Max(0, x.Count)),
            RightSamples = right.Sum(x => (long)Math.Max(0, x.Count)),
            Complete = IsComplete(left) && IsComplete(right)
        };
    }

    /// <summary>Rollup means are observations, not buckets: combine them by sample count.</summary>
    private static double? Aggregate(IReadOnlyList<RollupResult> rows)
    {
        var usable = rows.Where(x => x.Mean is not null && x.Count > 0 && x.Coverage > 0 && !string.Equals(x.VisibilityState, "unavailable", StringComparison.Ordinal) && !string.Equals(x.VisibilityState, "unsupported", StringComparison.Ordinal)).ToArray();
        long samples = usable.Sum(x => (long)x.Count);
        return samples == 0 ? null : usable.Sum(x => x.Mean!.Value * x.Count) / samples;
    }

    private static bool IsComplete(IEnumerable<RollupResult> rows) => rows.Any() && rows.All(x => x.Mean is not null && x.Count > 0 && x.Coverage >= 1 && !x.Truncated && string.Equals(x.VisibilityState, "complete", StringComparison.Ordinal));
}

public static class ForecastV1
{
    /// <summary>
    /// Computes a storage forecast from repository-produced daily rollups.
    /// Raw telemetry is intentionally not accepted on this production path:
    /// rollups provide a stable, dimension-scoped input and bounded volume.
    /// </summary>
    public static ForecastResult Compute(string metricKey, IEnumerable<RollupResult> dailyRollups,
        DateTimeOffset horizonStartUtc, TimeSpan horizon, double? capacity,
        string? dimensionsSha256 = null, double minimumConfidence = .5)
    {
        ArgumentNullException.ThrowIfNull(dailyRollups);
        RollupResult[] rows = dailyRollups.Where(x => x.MetricKey == metricKey &&
            (x.Interval == RollupInterval.Day || x.BucketEndUtc - x.BucketStartUtc == TimeSpan.FromDays(1)) &&
            x.Mean is not null && x.Count > 0 && x.Coverage > 0 && !x.Truncated && x.VisibilityState == "complete").ToArray();
        string? hash = dimensionsSha256;
        if (hash is not null && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new ArgumentException("Forecast dimension identity is invalid.", nameof(dimensionsSha256));
        if (rows.Length > 0)
        {
            hash ??= rows[0].DimensionsSha256;
            rows = rows.Where(x => string.Equals(x.DimensionsSha256, hash, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (rows.Any(x => !string.Equals(CanonicalDimensions.Sha256(x.Dimensions), x.DimensionsSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Forecast rollup dimensions do not match their identity.");
        }
        // A job without a dimension fence must never combine two volumes.
        if (hash is null || rows.Any(x => !string.Equals(x.DimensionsSha256, hash, StringComparison.OrdinalIgnoreCase)))
            return Unavailable(metricKey, horizonStartUtc, horizon, hash);
        IReadOnlyDictionary<string, string> dimensions = rows.Length == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : rows[0].Dimensions;
        if (rows.Any(x => !string.Equals(CanonicalDimensions.Sha256(x.Dimensions), hash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Forecast rollup dimension identity is inconsistent.");
        ForecastResult result = Compute(metricKey, rows.Select(x => new MetricPoint(x.BucketStartUtc, metricKey, x.Mean!.Value, dimensions: dimensions)), horizonStartUtc, horizon, capacity, minimumConfidence);
        return result with { Dimensions = dimensions, DimensionsSha256 = hash };
    }

    public static ForecastResult Compute(string metricKey, IEnumerable<MetricPoint> dailyPoints, DateTimeOffset horizonStartUtc, TimeSpan horizon, double? capacity, double minimumConfidence = .5)
    {
        if (horizonStartUtc.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.", nameof(horizonStartUtc));
        if (horizon <= TimeSpan.Zero || horizon > TimeSpan.FromDays(90)) throw new ArgumentOutOfRangeException(nameof(horizon));
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        var result = new ForecastResult { MetricKey = metricKey, HorizonStartUtc = horizonStartUtc, HorizonEndUtc = horizonStartUtc.Add(horizon), Confidence = 0, Residual = double.NaN };
        var points = dailyPoints.Where(x => x.MetricKey == metricKey && x.Complete).OrderBy(x => x.ObservedAtUtc).TakeLast(90).ToArray();
        if (capacity is null || !double.IsFinite(capacity.Value) || capacity.Value <= 0 || points.Length < 8 || points[^1].ObservedAtUtc - points[0].ObservedAtUtc < TimeSpan.FromDays(7)) return result;
        var segment = new List<MetricPoint>();
        foreach (var p in points) { if (segment.Count > 0 && (p.Reset || (p.Kind == MetricKind.Counter && p.Value < segment[^1].Value * .5))) segment.Clear(); segment.Add(p); }
        if (segment.Count < 8 || segment[^1].ObservedAtUtc - segment[0].ObservedAtUtc < TimeSpan.FromDays(7)) return result;
        double origin = segment[0].ObservedAtUtc.UtcDateTime.Ticks / (double)TimeSpan.TicksPerDay; var slopes = new List<double>();
        for (int i = 0; i < segment.Count; i++) for (int j = i + 1; j < segment.Count; j++) { double dt = segment[j].ObservedAtUtc.UtcDateTime.Ticks / (double)TimeSpan.TicksPerDay - segment[i].ObservedAtUtc.UtcDateTime.Ticks / (double)TimeSpan.TicksPerDay; slopes.Add((segment[j].Value - segment[i].Value) / dt); }
        slopes.Sort(); double slope = slopes[slopes.Count / 2], intercept = segment.Select(p => p.Value - slope * (p.ObservedAtUtc.UtcDateTime.Ticks / (double)TimeSpan.TicksPerDay)).OrderBy(x => x).ElementAt(segment.Count / 2); double estimate = intercept + slope * (horizonStartUtc.UtcDateTime.Add(horizon).Ticks / (double)TimeSpan.TicksPerDay); double residual = segment.Select(p => Math.Abs(p.Value - (intercept + slope * (p.ObservedAtUtc.UtcDateTime.Ticks / (double)TimeSpan.TicksPerDay)))).Average(); double confidence = Math.Min(1, segment.Count / 30d) * Math.Exp(-residual / Math.Max(Math.Abs(segment.Average(x => x.Value)), 1));
        if (!double.IsFinite(slope) || !double.IsFinite(intercept) || !double.IsFinite(estimate) || !double.IsFinite(residual) || residual < 0 || !double.IsFinite(confidence) || confidence is < 0 or > 1)
            return result;
        // Clamp the point estimate before deriving bounds.  This keeps the
        // result internally ordered when a trend projects beyond capacity.
        double clampedEstimate = Math.Clamp(estimate, 0, capacity.Value);
        double lower = Math.Max(0, clampedEstimate - residual * 2);
        double upper = Math.Min(capacity.Value, clampedEstimate + residual * 2);
        if (confidence < minimumConfidence || !double.IsFinite(clampedEstimate) || !double.IsFinite(lower) || !double.IsFinite(upper) || lower > clampedEstimate || clampedEstimate > upper || lower < 0 || upper > capacity.Value || result.HorizonEndUtc <= result.HorizonStartUtc)
            return result;
        IReadOnlyDictionary<string, string> dimensions = points.Length == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : points[0].Dimensions;
        return result with { Estimate = clampedEstimate, LowerBound = lower, UpperBound = upper, SlopePerDay = slope, Confidence = confidence, Residual = residual, Dimensions = dimensions, DimensionsSha256 = CanonicalDimensions.Sha256(dimensions) };
    }

    private static ForecastResult Unavailable(string metricKey, DateTimeOffset start, TimeSpan horizon, string? dimensionsSha256) =>
        new() { MetricKey = metricKey, HorizonStartUtc = start, HorizonEndUtc = start.Add(horizon), Confidence = 0, Residual = double.NaN, DimensionsSha256 = dimensionsSha256 ?? CanonicalDimensions.Sha256(null), VisibilityState = "unavailable" };
}

public static class EvidenceV1
{
    private static readonly ImmutableHashSet<string> AllowedTypes = ImmutableHashSet.Create(StringComparer.Ordinal, "metric", "alert", "activity", "deadlock", "replication", "host", "health");
    public static EvidencePacket Build(DateTimeOffset occurredAtUtc, IEnumerable<EvidenceReference> references, IEnumerable<string>? tombstones = null, DateTimeOffset? sourceCutoffUtc = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        EvidenceReference[] materialized = references.ToArray();
        string seed = string.Join("\n", materialized.OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Identity, StringComparer.Ordinal).Select(x => $"{x.Type}|{x.Identity}|{x.OccurredAtUtc:O}"));
        return Build(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16)), occurredAtUtc, materialized, tombstones, sourceCutoffUtc);
    }
    public static EvidencePacket Build(Guid packetId, DateTimeOffset occurredAtUtc, IEnumerable<EvidenceReference> references, IEnumerable<string>? tombstones = null, DateTimeOffset? sourceCutoffUtc = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        EvidenceReference[] materialized = references.ToArray();
        if (packetId == Guid.Empty || occurredAtUtc.Offset != TimeSpan.Zero || (sourceCutoffUtc is not null && sourceCutoffUtc.Value.Offset != TimeSpan.Zero)) throw new ArgumentException("Packet identity, source cutoff, and UTC occurrence are required.");
        var blocked = (tombstones ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal); var refs = materialized.Where(x => AllowedTypes.Contains(x.Type) && !blocked.Contains(x.Identity) && x.OccurredAtUtc >= occurredAtUtc.AddMinutes(-15) && x.OccurredAtUtc < occurredAtUtc.AddMinutes(5)).OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Identity, StringComparer.Ordinal).Take(256).ToArray();
        if (blocked.Count > 256 || blocked.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256)) throw new ArgumentException("Evidence tombstone bounds exceeded.", nameof(tombstones));
        var normalizedTombstones = (tombstones ?? Array.Empty<string>()).Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).Take(256).ToArray();
        string identity = Hash(string.Join("\n", refs.Select(x => $"{x.Type}|{x.Identity}|{x.OccurredAtUtc:O}"))); string cutoff = Hash(sourceCutoffUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "none"); string sourceDigest = Hash(identity + "|" + cutoff);
        return new EvidencePacket { PacketId = packetId, WindowStartUtc = occurredAtUtc.AddMinutes(-15), WindowEndUtc = occurredAtUtc.AddMinutes(5), References = refs, IdentitySha256 = identity, SourceCutoffSha256 = cutoff, SourceDigest = sourceDigest, SourceCutoffUtc = sourceCutoffUtc, Tombstones = normalizedTombstones, Confidence = refs.Length == 0 ? 0 : Math.Min(1, refs.Length / 8d), Trigger = "metric", Algorithm = EvidencePacket.AlgorithmVersion, Generation = 1, State = "complete", Truncated = materialized.Length > refs.Length };
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class IncidentV1
{
    public static IReadOnlyList<IncidentThread> Sessionize(IEnumerable<EvidencePacket> packets)
    {
        var ordered = packets.OrderBy(x => x.WindowStartUtc).ThenBy(x => x.PacketId).ToArray(); var threads = new List<IncidentThread>();
        foreach (var packet in ordered)
        {
            var current = threads.LastOrDefault();
            if (current is null || packet.WindowStartUtc - (current.ClosedAtUtc ?? current.OpenedAtUtc) > TimeSpan.FromMinutes(15) || packet.WindowStartUtc - current.OpenedAtUtc > TimeSpan.FromHours(6) || current.Packets.Count >= 256)
            {
                Guid threadId = DeterministicId(packet.IdentitySha256);
                var generation = BuildGeneration(threadId, 1, packet, false);
                threads.Add(new IncidentThread(threadId, packet.WindowStartUtc, packet.WindowEndUtc, new[] { packet }, 1) { Generations = new[] { generation } });
                continue;
            }
            long generationNumber = current.CurrentGeneration + 1;
            var nextGeneration = BuildGeneration(current.ThreadId, generationNumber, packet, true);
            threads[^1] = current with { ClosedAtUtc = packet.WindowEndUtc, Packets = current.Packets.Append(packet).ToArray(), CurrentGeneration = generationNumber, Generations = current.Generations.Append(nextGeneration).ToArray() };
        }
        return threads;
    }
    private static IncidentGeneration BuildGeneration(Guid threadId, long generation, EvidencePacket packet, bool supersedesPrevious)
    {
        string canonical = string.Join("|", "incident-v1", threadId.ToString("D"), generation.ToString(CultureInfo.InvariantCulture), packet.PacketId.ToString("D"), packet.IdentitySha256, packet.SourceDigest, packet.SourceCutoffSha256);
        string correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new IncidentGeneration(threadId, generation, packet.WindowEndUtc, correlation, supersedesPrevious) { EvidencePacketId = packet.PacketId };
    }
    private static Guid DeterministicId(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
