using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Passive exact-value scan, staged until the fenced writer is qualified.</summary>
public sealed class SqlServerConfigurationsCollector : SqlServerHealthCollector
{
    private static readonly CollectorManifest ConfigurationManifest = CreateManifest();
    private static readonly CollectorOutputContract ConfigurationOutputContract = new(
        new CollectorOutputSchemaVersion(1), [],
        maxMetricSamples: 0, maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 0,
        maxServerConfigurationObservations: ServerConfigurationObservationBatch.MaximumItems);

    public static CollectorOutputContract OutputContract => ConfigurationOutputContract;

    public SqlServerConfigurationsCollector()
        : this(new SqlServerIntegratedConnectionFactory(
            SqlServerIntegratedConnectionFactory.CollectionApplicationName)) { }

    internal SqlServerConfigurationsCollector(ISqlServerConnectionFactory connectionFactory)
        : base(ConfigurationManifest, SqlServerConfigurationSource.LoadPinnedQuery,
            connectionFactory) { }

    private protected override async ValueTask<CollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request, SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        SqlServerConfigurationRead source = await SqlServerConfigurationSource.ReadAsync(
            reader, cancellationToken).ConfigureAwait(false);
        int major = request.CapabilityProfile.ServerIdentity!.Version.Major;
        ServerConfigurationObservation[] observations = source.Incomplete ? [] :
            source.Values.Select(value => new ServerConfigurationObservation(
                request.TargetId, request.TargetRevision, major,
                value.ConfigurationId, value.Name, value.SourceType,
                value.ConfiguredValue, value.InUseValue, value.IsDynamic,
                value.IsAdvanced, source.ObservedAtUtc)).ToArray();
        return new CollectorReadResult(
            new CollectorPayload(serverConfigurations:
                new ServerConfigurationObservationBatch(observations)),
            source.SourceRowsRead, source.ResponseBytes,
            CreateLossEvidence(source.ResponseByteLimitReached,
                source.SourceRowLimitReached));
    }

    private static CollectorManifest CreateManifest() => new(
        new CollectorId("engine.configurations"),
        new CollectorDisplayName("SQL Server configuration evidence"),
        new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds"),
         new CapabilityId("authentication.windows-integrated"),
         new CapabilityId("transport.tls-validated"),
         new CapabilityId("privilege.non-sysadmin"),
         new CapabilityId("platform.windows")],
        [new CollectorPermissionRequirement(
            new SqlServerPermissionId("server.view-performance-state"),
            PermissionEvidenceScope.Server,
            new SqlServerMajorVersionRange(16, 17))],
        new SqlServerMajorVersionRange(15, 17),
        [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            SqlServerConfigurationSource.MaximumConfigurations + 1,
            SqlServerConfigurationSource.MaximumResponseBytes, CollectorEstimatedCost.Low),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
        new CollectorOutputSchemaVersion(1), CollectorOperationalMode.Passive,
        dependsOn: [new CollectorId("capability.connection"),
                    new CollectorId("engine.core")],
        resilience: new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100),
            3, TimeSpan.FromMinutes(5)),
        outputKind: CollectorOutputKind.ServerConfigurations);
}
