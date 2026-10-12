using SqlObserver.Collector.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using System.Text.Json;

namespace SqlObserver.UnitTests;

public sealed class ServerConfigurationObservationTests
{
    [Fact]
    public void PinnedManifestMatchesDisabledRuntimeContract()
    {
        SqlServerConfigurationAssetCatalog assets = SqlServerConfigurationAssetCatalog.LoadEmbedded();
        CollectorManifest runtime = new SqlServerConfigurationsCollector().Manifest;
        using JsonDocument document = JsonDocument.Parse(assets.ManifestJson);
        JsonElement manifest = document.RootElement;
        Assert.Equal(SqlServerConfigurationAssetCatalog.ExpectedBundleChecksum, assets.BundleChecksum);
        Assert.Equal(runtime.Id.Value, manifest.GetProperty("collectorId").GetString());
        Assert.Equal(runtime.ManifestVersion.Value, manifest.GetProperty("collectorVersion").GetInt32());
        Assert.Equal(runtime.OutputSchemaVersion.Value, manifest.GetProperty("outputSchemaVersion").GetInt32());
        Assert.Equal(runtime.Limits.MaxRows,
            manifest.GetProperty("executionBounds").GetProperty("maximumRows").GetInt32());
        Assert.Equal(runtime.Limits.MaxResponseBytes,
            manifest.GetProperty("executionBounds").GetProperty("maximumResponseBytes").GetInt32());
        Assert.Equal((int)runtime.Intervals.DefaultInterval.TotalSeconds,
            manifest.GetProperty("cadence").GetProperty("defaultIntervalSeconds").GetInt32());
        Assert.False(manifest.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal("server-configurations", manifest.GetProperty("outputKind").GetString());
    }

    [Fact]
    public void ExactValuesUseADedicatedBoundedOutputContract()
    {
        var collector = new SqlServerConfigurationsCollector();
        CollectorManifest manifest = collector.Manifest;
        Assert.Equal("engine.configurations", manifest.Id.Value);
        Assert.Equal(CollectorOutputKind.ServerConfigurations, manifest.OutputKind);
        Assert.Equal(TimeSpan.FromMinutes(5), manifest.Intervals.DefaultInterval);
        Assert.Equal(513, manifest.Limits.MaxRows);
        _ = new CollectorRegistration(19, collector,
            new CollectorOutputValidator(SqlServerConfigurationsCollector.OutputContract),
            new CollectorSha256Digest(new string('a', 64)),
            new CollectorSha256Digest(new string('b', 64)));

        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        var observation = new ServerConfigurationObservation(request.TargetId,
            request.TargetRevision, 16, 1544, "max server memory (MB)", "bigint",
            "9223372036854775807", "8192", true, true,
            M4TestData.RepositoryTime);
        var payload = new CollectorPayload(serverConfigurations:
            new ServerConfigurationObservationBatch([observation]));
        Assert.Empty(payload.Metrics);
        Assert.Equal(1, payload.ItemCount);
        var result = new CollectorExecutionResult(request.TargetId, request.TargetRevision,
            manifest.Id, 1, 1, CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed, payload,
            new CollectorRunAccounting(1, 1, payload.EstimatedSizeBytes,
                payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        new CollectorOutputValidator(SqlServerConfigurationsCollector.OutputContract)
            .Validate(manifest, request, result);
    }

    [Fact]
    public void DuplicateIdsAndMalformedValuesAreRejected()
    {
        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        var value = new ServerConfigurationObservation(request.TargetId,
            request.TargetRevision, 16, 1, "option", "bigint", "1", "2",
            true, false, M4TestData.RepositoryTime);
        Assert.Throws<ArgumentException>(() =>
            new ServerConfigurationObservationBatch([value, value]));
        Assert.Throws<ArgumentException>(() =>
            new ServerConfigurationObservation(request.TargetId,
                request.TargetRevision, 16, 1, "option", "bigint", "1.5", "2",
                true, false, M4TestData.RepositoryTime));
        Assert.Throws<ArgumentException>(() =>
            new ServerConfigurationObservation(request.TargetId,
                request.TargetRevision, 16, 1, "option\uD800", "bigint", "1", "2",
                true, false, M4TestData.RepositoryTime));
    }
}
