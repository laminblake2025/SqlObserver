using SqlObserver.Collector.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerVolumeCollectorTests
{
    [Fact]
    public void ManifestDeclaresSlowerPassiveReadsAndBothRequiredServerGrants()
    {
        var collector = new SqlServerVolumeCapacityCollector(new IdentityFingerprintKey(new byte[32]));
        CollectorManifest manifest = collector.Manifest;

        Assert.Equal("storage.volume", manifest.Id.Value);
        Assert.Equal(CollectorOutputKind.SqlVolumes, manifest.OutputKind);
        Assert.Equal(CollectorOperationalMode.Passive, manifest.OperationalMode);
        Assert.Equal(TimeSpan.FromMinutes(5), manifest.Intervals.DefaultInterval);
        Assert.Equal(1_000, manifest.Limits.MaxRows);
        Assert.Equal(4_194_304, manifest.Limits.MaxResponseBytes);
        Assert.Contains(manifest.RequiredPermissions, permission =>
            permission.PermissionId.Value == "server.view-any-definition" &&
            permission.ApplicableVersions.MinimumMajor == 15 &&
            permission.ApplicableVersions.MaximumMajor == 17);
        Assert.Contains(manifest.RequiredPermissions, permission =>
            permission.PermissionId.Value == "server.view-state" &&
            permission.ApplicableVersions.MaximumMajor == 15);
        Assert.Contains(manifest.RequiredPermissions, permission =>
            permission.PermissionId.Value == "server.view-performance-state" &&
            permission.ApplicableVersions.MinimumMajor == 16);
        Assert.Equal(1_000, SqlServerVolumeCapacityCollector.OutputContract.MaxSqlVolumeObservations);
        _ = new CollectorRegistration(16, collector,
            new CollectorOutputValidator(SqlServerVolumeCapacityCollector.OutputContract),
            new CollectorSha256Digest(new string('a', 64)),
            new CollectorSha256Digest(new string('b', 64)));
    }

    [Fact]
    public void ValidatorAllowsOneLookaheadOnlyWhenNoRowsArePublished()
    {
        var collector = new SqlServerVolumeCapacityCollector(new IdentityFingerprintKey(new byte[32]));
        var validator = new CollectorOutputValidator(SqlServerVolumeCapacityCollector.OutputContract);
        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();

        CollectorExecutionResult Partial(int sourceRows) => new(
            request.TargetId, request.TargetRevision, collector.Manifest.Id,
            collector.Manifest.ManifestVersion.Value, collector.Manifest.OutputSchemaVersion.Value,
            CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit,
            CollectorPayload.Empty,
            new CollectorRunAccounting(sourceRows, 0, 0, 0),
            new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false));

        validator.Validate(collector.Manifest, request, Partial(1_001));
        Assert.Throws<InvalidDataException>(() =>
            validator.Validate(collector.Manifest, request, Partial(1_002)));
    }
}
