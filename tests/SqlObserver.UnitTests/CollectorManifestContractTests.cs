using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.UnitTests;

public sealed class CollectorManifestContractTests
{
    [Fact]
    public void ManifestCarriesEveryAdr0005FieldAndDefensivelyCopiesLists()
    {
        CapabilityId[] capabilities = [new CapabilityId("connection")];
        CollectorPermissionRequirement[] permissions =
        [
            new(
                new SqlServerPermissionId("server.view-state"),
                PermissionEvidenceScope.Server,
                new SqlServerMajorVersionRange(15, 15)),
            new(
                new SqlServerPermissionId("server.view-performance-state"),
                PermissionEvidenceScope.Server,
                new SqlServerMajorVersionRange(16, 17)),
        ];
        SqlServerPlatform[] platforms = [SqlServerPlatform.Windows];
        CollectorManifest manifest = CreateManifest(capabilities, permissions, platforms);
        capabilities[0] = new CapabilityId("changed");
        permissions[0] = new CollectorPermissionRequirement(
            new SqlServerPermissionId("changed"),
            PermissionEvidenceScope.Server,
            new SqlServerMajorVersionRange(15, 15));
        platforms[0] = SqlServerPlatform.Linux;

        Assert.Equal("capability.connection", manifest.Id.Value);
        Assert.Equal("Connection and capabilities", manifest.DisplayName.Value);
        Assert.Equal(1, manifest.ManifestVersion.Value);
        Assert.Equal("connection", manifest.RequiredCapabilities[0].Value);
        Assert.Equal("server.view-state", manifest.RequiredPermissions[0].PermissionId.Value);
        Assert.Equal(SqlServerPlatform.Windows, manifest.SupportedPlatforms[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), manifest.Intervals.DefaultInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), manifest.Limits.Timeout);
        Assert.Equal(100, manifest.Limits.MaxRows);
        Assert.Equal(65_536, manifest.Limits.MaxResponseBytes);
        Assert.Equal(CollectorFallbackMode.Unsupported, manifest.Fallback.Mode);
        Assert.Equal(1, manifest.OutputSchemaVersion.Value);
        Assert.Equal(CollectorOperationalMode.Passive, manifest.OperationalMode);
    }

    [Fact]
    public void IntervalAndExecutionLimitsEnforceHardBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorIntervalPolicy(
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorIntervalPolicy(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorExecutionLimits(
            CollectorExecutionLimits.MinimumTimeout - TimeSpan.FromTicks(1),
            1,
            1,
            CollectorEstimatedCost.Low));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorExecutionLimits(
            TimeSpan.FromSeconds(1),
            CollectorExecutionLimits.MaximumRows + 1,
            1,
            CollectorEstimatedCost.Low));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorExecutionLimits(
            TimeSpan.FromSeconds(1),
            1,
            CollectorExecutionLimits.MaximumResponseBytes + 1,
            CollectorEstimatedCost.Low));
    }

    [Fact]
    public void PermissionRequirementsAreCatalogIdsWithVersionScope()
    {
        var supported = new SqlServerMajorVersionRange(15, 17);
        var invalid = new CollectorPermissionRequirement(
            new SqlServerPermissionId("server.future-permission"),
            PermissionEvidenceScope.Server,
            new SqlServerMajorVersionRange(18, 18));

        Assert.Throws<ArgumentException>(() => CreateManifest(
            [new CapabilityId("connection")],
            [invalid],
            [SqlServerPlatform.Windows],
            supported));
        Assert.Throws<ArgumentException>(() => new SqlServerPermissionId("VIEW SERVER STATE"));
        Assert.Throws<ArgumentException>(() => new SqlServerPermissionId("server.permission;drop"));
    }

    [Fact]
    public void FallbackIsExplicitAndCannotCycleDirectlyToSelf()
    {
        var alternate = new CollectorFallbackPolicy(
            CollectorFallbackMode.AlternateCollector,
            new CollectorId("capability.connection-fallback"));

        Assert.Equal("capability.connection-fallback", alternate.AlternateCollectorId?.Value);
        Assert.Throws<ArgumentException>(() => new CollectorFallbackPolicy(
            CollectorFallbackMode.Unsupported,
            new CollectorId("unexpected")));
        Assert.Throws<ArgumentException>(() => new CollectorFallbackPolicy(
            CollectorFallbackMode.AlternateCollector));
        Assert.Throws<ArgumentException>(() => CreateManifest(
            [new CapabilityId("connection")],
            [],
            [SqlServerPlatform.Windows],
            fallback: new CollectorFallbackPolicy(
                CollectorFallbackMode.AlternateCollector,
                new CollectorId("capability.connection"))));
    }

    [Fact]
    public void ManifestRejectsDuplicateAndUnboundedDeclarations()
    {
        var capability = new CapabilityId("connection");

        Assert.Throws<ArgumentException>(() => CreateManifest(
            [capability, capability],
            [],
            [SqlServerPlatform.Windows]));
        Assert.Throws<ArgumentException>(() => CreateManifest(
            [capability],
            [],
            [SqlServerPlatform.Windows, SqlServerPlatform.Windows]));
        Assert.Throws<ArgumentException>(() => new CollectorDisplayName("bad\nname"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorManifestVersion(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorOutputSchemaVersion(10_000));
    }

    private static CollectorManifest CreateManifest(
        IReadOnlyList<CapabilityId> capabilities,
        IReadOnlyList<CollectorPermissionRequirement> permissions,
        IReadOnlyList<SqlServerPlatform> platforms,
        SqlServerMajorVersionRange? supported = null,
        CollectorFallbackPolicy? fallback = null) =>
        new(
            new CollectorId("capability.connection"),
            new CollectorDisplayName("Connection and capabilities"),
            new CollectorManifestVersion(1),
            capabilities,
            permissions,
            supported ?? new SqlServerMajorVersionRange(15, 17),
            platforms,
            new CollectorIntervalPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
            new CollectorExecutionLimits(
                TimeSpan.FromSeconds(30),
                maxRows: 100,
                maxResponseBytes: 65_536,
                CollectorEstimatedCost.Low),
            fallback ?? new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
            new CollectorOutputSchemaVersion(1),
            CollectorOperationalMode.Passive);
}
