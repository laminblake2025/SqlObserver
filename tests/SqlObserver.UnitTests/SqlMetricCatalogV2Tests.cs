using SqlObserver.Domain.Analytics;

namespace SqlObserver.UnitTests;

public sealed class SqlMetricCatalogV2Tests
{
    private static readonly string[] BatchSourceKeys = ["engine.batch_requests_total", "engine.start_time_key"];
    private static readonly string[] SchedulerCpuSourceKeys = ["engine.scheduler_cpu_milliseconds_total", "engine.visible_scheduler_count", "engine.start_time_key"];
    [Fact]
    public void ExistingVersionOneContractRetainsItsMeaning()
    {
        Assert.Equal(1, MetricCatalogV1.Version);
        Assert.Equal(10, MetricCatalogV1.All.Count);
        Assert.Equal("b67a7f7d8ee3af1228bfb8fbc485e0e61595586c88af4b9af3abefd524980dd4", MetricCatalogV1.Checksum);
        Assert.False(MetricCatalogV1.TryGet("engine.sql_scheduler_cpu_percent", out _));
        foreach (MetricCatalogEntry original in MetricCatalogV1.All)
        {
            AnalyticsMetricDefinition definition = MetricCatalogV2.Get(original.Key);
            Assert.Same(original, definition.Metric);
            Assert.Equal(AnalyticsMetricDerivation.DirectGauge, definition.Derivation);
            Assert.Equal(new[] { original.Key }, definition.SourceMetricKeys);
        }
    }

    [Fact]
    public void VersionTwoHasClosedUniqueOrderedSqlDefinitions()
    {
        Assert.Equal(2, MetricCatalogV2.Version);
        Assert.Equal(17, MetricCatalogV2.All.Count);
        string[] keys = MetricCatalogV2.All.Select(item => item.Metric.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal(7, MetricCatalogV2.All.Count(item => item.Source == AnalyticsMetricSource.SqlEngine));
        Assert.All(MetricCatalogV2.All, definition => Assert.Equal(MetricKind.Gauge, definition.Metric.Kind));
        Assert.False(MetricCatalogV2.TryGet("engine.batch_requests_total", out _));
        Assert.False(MetricCatalogV2.TryGet("engine.sql_process_cpu_percent", out _));
        Assert.Throws<KeyNotFoundException>(() => MetricCatalogV2.Get("caller-supplied-key"));
    }

    [Theory]
    [InlineData("engine.user_connections", "count")]
    [InlineData("engine.process_physical_memory_bytes", "bytes")]
    [InlineData("engine.os_available_memory_bytes", "bytes")]
    [InlineData("engine.memory_grants_pending", "count")]
    [InlineData("engine.scheduler_runnable_tasks", "count")]
    public void DirectSqlGaugesKeepExactSourceAndRejectDimensions(string key, string unit)
    {
        AnalyticsMetricDefinition definition = MetricCatalogV2.Get(key);
        Assert.Equal(AnalyticsMetricSource.SqlEngine, definition.Source);
        Assert.Equal(AnalyticsMetricDerivation.DirectGauge, definition.Derivation);
        Assert.Equal(unit, definition.Metric.Unit);
        Assert.Equal(new[] { key }, definition.SourceMetricKeys);
        Assert.False(definition.RequiresComparableEngineSamples);
        Assert.False(definition.RequiresSameEngineEpoch);
        Assert.Null(definition.MaximumSampleGap);
        Assert.False(definition.Metric.AllowsDimensions(new Dictionary<string, string> { ["database"] = "other" }));
    }

    [Fact]
    public void DerivedSqlRatesRequireComparableBoundedSamplesAndCorrectCounters()
    {
        AnalyticsMetricDefinition batch = MetricCatalogV2.Get("engine.batch_requests_per_second");
        Assert.Equal(AnalyticsMetricDerivation.BatchRate, batch.Derivation);
        Assert.Equal(BatchSourceKeys, batch.SourceMetricKeys);
        Assert.Equal("batches_per_second", batch.Metric.Unit);
        AnalyticsMetricDefinition cpu = MetricCatalogV2.Get("engine.sql_scheduler_cpu_percent");
        Assert.Equal(AnalyticsMetricDerivation.SchedulerCpuPercent, cpu.Derivation);
        Assert.Equal(SchedulerCpuSourceKeys, cpu.SourceMetricKeys);
        Assert.True(cpu.RequiresStableSchedulerCount);
        Assert.Equal("percent", cpu.Metric.Unit);
        foreach (AnalyticsMetricDefinition definition in new[] { batch, cpu })
        {
            Assert.True(definition.RequiresComparableEngineSamples);
            Assert.True(definition.RequiresSameEngineEpoch);
            Assert.Equal(TimeSpan.FromMinutes(2), definition.MaximumSampleGap);
            Assert.Empty(definition.Metric.DimensionAllowlist);
        }
    }

    [Fact]
    public void DefinitionCollectionsCannotBeMutatedThroughTheirPublicInterfaces()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<AnalyticsMetricDefinition>)MetricCatalogV2.All).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)MetricCatalogV2.Get("engine.user_connections").SourceMetricKeys).Add("other"));
    }

    [Fact]
    public void VersionTwoChecksumIsSeparateAndCoversItsClosedDefinition()
    {
        Assert.Equal("702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338", MetricCatalogV2.Checksum);
        Assert.NotEqual(MetricCatalogV1.Checksum, MetricCatalogV2.Checksum);
        Assert.Equal("SHA-256", MetricCatalogV2.ChecksumAlgorithm);
    }
}
