using SqlObserver.Collector.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerBufferNodePleCollectorTests
{
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
