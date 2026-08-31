using SqlObserver.Analytics;
using SqlObserver.Domain.Analytics;
using SqlObserver.Infrastructure.Windows;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Authorization;
using System.Globalization;

namespace SqlObserver.UnitTests;

public sealed class M10AnalyticsContractTests
{
    private static readonly string[] CatalogMetricKeys = ["host.cpu.percent", "host.memory.available_bytes", "host.memory.committed_bytes", "host.volume.free_bytes", "host.volume.total_bytes", "host.volume.queue_length", "host.volume.read_latency_ms", "host.volume.write_latency_ms", "replication.pending_commands", "replication.latency_seconds"];
    private static readonly string[] HostMetricAssetNames = ["host.metrics.v1.schema.json", "host.metrics.v1.json"];
    private static readonly string[] VolumeDimensionKeys = ["volume"];

    [Fact]
    public void RetentionPreviewUsesTheEightColumnDistinctRowContract()
    {
        DateTimeOffset start = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var row = new RetentionPreviewEntry("m10_host_metrics", "telemetry", "host_metric_snapshot_v2", "host_metric_snapshot_v2_p20260825", start, start.AddDays(1), false, "retention_disabled");
        var page = new RetentionPreview(new[] { row }, false, null, start.AddDays(2));
        page.Validate();
        Assert.Equal("telemetry", page.Entries[0].ParentSchema);
        Assert.Throws<ArgumentException>(() => new RetentionPreviewEntry("m10_host_metrics", "telemetry", "host_metric_snapshot_v2", "host_metric_snapshot_v2_p20260825", start, start.AddDays(1), true, "retention_disabled").Validate());
    }

    [Fact]
    public void DimensionsAreCanonicalAndOrderIndependent()
    {
        string a = CanonicalDimensions.Sha256(new Dictionary<string, string> { ["z"] = "2", ["a"] = "1" });
        string b = CanonicalDimensions.Sha256(new Dictionary<string, string> { ["a"] = "1", ["z"] = "2" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void MutationDigestBindsPayloadActorScopeAndAuditFields()
    {
        Guid target = Guid.NewGuid(), operation = Guid.NewGuid(), correlation = Guid.NewGuid();
        DateTimeOffset from = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), to = from.AddDays(1);
        byte[] first = MutationDigestV1.Backfill(target, from, to, "host.cpu.percent", 3, operation, "S-1-5-21-1", target.ToString("D"), correlation, "reason");
        byte[] retry = MutationDigestV1.Backfill(target, from, to, "host.cpu.percent", 3, operation, "S-1-5-21-1", target.ToString("D"), correlation, "reason");
        byte[] changed = MutationDigestV1.Backfill(target, from, to, "host.memory.available_bytes", 3, operation, "S-1-5-21-1", target.ToString("D"), correlation, "reason");
        Assert.Equal(first, retry);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void MetricCatalogV1IsTheTenEntryOrderedContract()
    {
        Assert.Equal(10, MetricCatalogV1.All.Count);
        Assert.Equal("b67a7f7d8ee3af1228bfb8fbc485e0e61595586c88af4b9af3abefd524980dd4", MetricCatalogV1.Checksum);
        Assert.Equal(CatalogMetricKeys, MetricCatalogV1.All.Select(x => x.Key));
        Assert.Equal(VolumeDimensionKeys, MetricCatalogV1.Get("host.volume.free_bytes").DimensionAllowlist);
    }

    [Fact]
    public void HostEmbeddedBundleMatchesTheLFManifestDigest()
    {
        HostMetricsAssetCatalog catalog = HostMetricsAssetCatalog.LoadEmbedded();
        Assert.Equal("cf629310626827ea9b91baab7ef21427d20c230adfaeff472ddfd26d1ebfee26", catalog.BundleChecksum);
        Assert.Equal(HostMetricAssetNames, catalog.AssetNames);
    }

    [Fact]
    public void RollupUsesHalfOpenFiveMinuteBucketAndNullForMissing()
    {
        DateTimeOffset t = new(2026, 8, 25, 12, 4, 59, TimeSpan.Zero);
        RollupResult result = RollupV1.Compute(new[] { new MetricPoint(t, "host.cpu.percent", 10) }, RollupInterval.FiveMinutes, 2);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero), result.BucketStartUtc);
        Assert.Equal(10, result.Mean);
        Assert.Equal(.5, result.Coverage);
    }

    [Fact]
    public void LegacyMetricAliasesAreNotAccepted()
    {
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        Assert.False(MetricCatalogV1.TryGet("host.cpu.utilization", out _));
        Assert.Throws<InvalidDataException>(() => RollupV1.Compute(new[] { new MetricPoint(t, "host.disk.read_bytes_total", 100, MetricKind.Counter) }, RollupInterval.Hour));
    }

    [Fact]
    public void WindowComparisonWeightsUnevenRollupCountsInsteadOfAveragingMeans()
    {
        DateTimeOffset left = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset right = left.AddDays(2);
        RollupResult Rollup(DateTimeOffset start, double mean, int count) => new()
        {
            BucketStartUtc = start, BucketEndUtc = start.AddHours(1), MetricKey = "host.cpu.percent",
            DimensionsSha256 = CanonicalDimensions.Sha256(null), Count = count, Expected = count,
            Mean = mean, Min = mean, Max = mean, Sum = mean * count, Last = mean
        };
        WindowComparisonResult result = WindowComparisonV1.Compare(
            new[] { Rollup(left, 10, 1), Rollup(left.AddHours(1), 20, 3), Rollup(right, 30, 2), Rollup(right.AddHours(1), 50, 2) },
            left, left.AddHours(2), right, right.AddHours(2));

        Assert.Equal(17.5, result.LeftValue);
        Assert.Equal(40, result.RightValue);
        Assert.Equal(4, result.LeftSamples);
        Assert.Equal(4, result.RightSamples);
        Assert.Equal((40 - 17.5) / 17.5 * 100, result.Percent);
    }

    [Fact]
    public void EvidenceWindowIsFixedAndReferenceCapIsBounded()
    {
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        EvidencePacket packet = EvidenceV1.Build(Guid.NewGuid(), t, new[] { new EvidenceReference("metric", "m", t.AddMinutes(-15)), new EvidenceReference("metric", "late", t.AddMinutes(5)) });
        Assert.Single(packet.References);
        Assert.Equal(t.AddMinutes(-15), packet.WindowStartUtc);
        Assert.Equal(t.AddMinutes(5), packet.WindowEndUtc);
    }

    [Fact]
    public void ForecastRefusesUnknownCapacity()
    {
        DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); var points = Enumerable.Range(0, 10).Select(i => new MetricPoint(t.AddDays(i), "host.memory.available_bytes", i, MetricKind.Gauge));
        Assert.False(ForecastV1.Compute("host.memory.available_bytes", points, t.AddDays(10), TimeSpan.FromDays(1), null).Available);
    }

    [Fact]
    public void BaselineWindowUsesExplicitUtcReferenceAndIsCultureIndependent()
    {
        DateTimeOffset asOf = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var points = Enumerable.Range(0, 28 * 24).Select(i =>
            {
                DateTimeOffset at = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero).AddHours(i);
                return new RollupResult
                {
                    BucketStartUtc = at, BucketEndUtc = at.AddHours(1), MetricKey = "host.cpu.percent",
                    DimensionsSha256 = CanonicalDimensions.Sha256(null), Count = 1, Expected = 1,
                    Mean = 10, Min = 10, Max = 10, Sum = 10, Last = 10,
                };
            });
            var baselines = BaselineV1.Compute(points, asOf);
            Assert.NotEmpty(baselines);
            BaselineResult result = baselines[0];
            Assert.Equal(new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero), result.WindowStartUtc);
            Assert.Equal(new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero), result.WindowEndUtc);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void ForecastClampsCapacityThenReturnsOrderedFiniteBounds()
    {
        DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 30).Select(i => new MetricPoint(t.AddDays(i), "host.memory.available_bytes", i * 100, MetricKind.Gauge));
        ForecastResult result = ForecastV1.Compute("host.memory.available_bytes", points, t.AddDays(30), TimeSpan.FromDays(1), 50);
        Assert.True(result.Available);
        Assert.Equal(50, result.Estimate);
        Assert.True(result.LowerBound >= 0 && result.LowerBound <= result.Estimate && result.Estimate <= result.UpperBound && result.UpperBound <= 50);
        Assert.False(ForecastV1.Compute("host.memory.available_bytes", points, t.AddDays(30), TimeSpan.FromDays(1), double.NaN).Available);
    }

    [Fact]
    public void LiveRollupDerivationSupportsEveryIntervalAndPreservesDimensionIdentity()
    {
        DateTimeOffset at = new(2026, 8, 25, 12, 3, 0, TimeSpan.Zero);
        var dimensions = new Dictionary<string, string> { ["volume"] = "opaque-volume-a" };
        foreach (RollupInterval interval in Enum.GetValues<RollupInterval>())
        {
            RollupResult result = RollupV1.Compute(new[] { new MetricPoint(at, "host.volume.free_bytes", 100, dimensions: dimensions) }, interval, 1);
            Assert.Equal(dimensions, result.Dimensions);
            Assert.Equal(CanonicalDimensions.Sha256(dimensions), result.DimensionsSha256);
            result.Validate();
        }
    }

    [Fact]
    public void ForecastDailyRollupsRemainIsolatedByOpaqueVolumeFingerprint()
    {
        DateTimeOffset start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var a = new Dictionary<string, string> { ["volume"] = "opaque-a" };
        var b = new Dictionary<string, string> { ["volume"] = "opaque-b" };
        string hashA = CanonicalDimensions.Sha256(a);
        var rows = Enumerable.Range(0, 30).SelectMany(i => new[]
        {
            new RollupResult { Interval = RollupInterval.Day, BucketStartUtc = start.AddDays(i), BucketEndUtc = start.AddDays(i + 1), MetricKey = "host.volume.free_bytes", Dimensions = a, DimensionsSha256 = hashA, Count = 1, Expected = 1, Mean = 100 + i },
            new RollupResult { Interval = RollupInterval.Day, BucketStartUtc = start.AddDays(i), BucketEndUtc = start.AddDays(i + 1), MetricKey = "host.volume.free_bytes", Dimensions = b, DimensionsSha256 = CanonicalDimensions.Sha256(b), Count = 1, Expected = 1, Mean = 900 + i }
        }).ToArray();
        ForecastResult result = ForecastV1.Compute("host.volume.free_bytes", rows, start.AddDays(30), TimeSpan.FromDays(1), 10_000, hashA, minimumConfidence: 0);
        Assert.True(result.Available);
        Assert.Equal(hashA, result.DimensionsSha256);
        Assert.Equal(a, result.Dimensions);
    }
}
