using SqlObserver.Collector.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using System.Text.Json;

namespace SqlObserver.UnitTests;

public sealed class SqlServerBufferNodePleCollectorTests
{
    [Fact]
    public void PinnedManifestMatchesRuntimeCollectorBoundsAndPermissions()
    {
        SqlServerBufferNodePleAssetCatalog assets = SqlServerBufferNodePleAssetCatalog.LoadEmbedded();
        CollectorManifest runtime = new SqlServerBufferNodePleCollector().Manifest;
        using JsonDocument document = JsonDocument.Parse(assets.ManifestJson);
        JsonElement manifest = document.RootElement;
        Assert.Equal(SqlServerBufferNodePleAssetCatalog.ExpectedBundleChecksum, assets.BundleChecksum);
        Assert.Equal(runtime.Id.Value, manifest.GetProperty("collectorId").GetString());
        Assert.Equal(runtime.ManifestVersion.Value, manifest.GetProperty("collectorVersion").GetInt32());
        Assert.Equal(runtime.OutputSchemaVersion.Value, manifest.GetProperty("outputSchemaVersion").GetInt32());
        Assert.Equal(runtime.Limits.MaxRows, manifest.GetProperty("executionBounds").GetProperty("maximumRows").GetInt32());
        Assert.Equal(runtime.Limits.MaxResponseBytes, manifest.GetProperty("executionBounds").GetProperty("maximumResponseBytes").GetInt32());
        Assert.Equal((int)runtime.Intervals.DefaultInterval.TotalSeconds,
            manifest.GetProperty("cadence").GetProperty("defaultIntervalSeconds").GetInt32());
        Assert.False(manifest.GetProperty("defaultEnabled").GetBoolean());
        Assert.Contains("sys.dm_os_performance_counters", assets.QuerySql, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestAndValidatorPreserveOneDimensionedMetricPerNode()
    {
        var collector = new SqlServerBufferNodePleCollector();
        CollectorManifest manifest = collector.Manifest;
        Assert.Equal("engine.buffer-nodes", manifest.Id.Value);
        Assert.Equal(CollectorOutputKind.Metrics, manifest.OutputKind);
        Assert.Equal(CollectorOperationalMode.Passive, manifest.OperationalMode);
        Assert.Equal(129, manifest.Limits.MaxRows);
        Assert.Equal(128, SqlServerBufferNodePleCollector.OutputContract.MaxMetricSamples);
        Assert.Equal(["node_id"], Assert.Single(
            SqlServerBufferNodePleCollector.OutputContract.Metrics).AllowedDimensionKeys);
        _ = new CollectorRegistration(18, collector,
            new CollectorOutputValidator(SqlServerBufferNodePleCollector.OutputContract),
            new CollectorSha256Digest(new string('a', 64)),
            new CollectorSha256Digest(new string('b', 64)));

        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        DateTimeOffset at = M4TestData.RepositoryTime;
        var key = new MetricId("engine.buffer_node_page_life_expectancy_seconds");
        MetricSample[] samples =
        [
            new(new MetricSampleId(Guid.NewGuid()), request.TargetId, key, at,
                800, [new MetricDimension("node_id", "0")]),
            new(new MetricSampleId(Guid.NewGuid()), request.TargetId, key, at,
                175, [new MetricDimension("node_id", "1")]),
        ];
        var payload = new CollectorPayload(samples);
        var result = new CollectorExecutionResult(request.TargetId, request.TargetRevision,
            manifest.Id, manifest.ManifestVersion.Value, manifest.OutputSchemaVersion.Value,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, payload,
            new CollectorRunAccounting(2, 2, payload.EstimatedSizeBytes,
                payload.EstimatedSizeBytes), CollectorLossEvidence.None);

        new CollectorOutputValidator(SqlServerBufferNodePleCollector.OutputContract)
            .Validate(manifest, request, result);
    }
}
