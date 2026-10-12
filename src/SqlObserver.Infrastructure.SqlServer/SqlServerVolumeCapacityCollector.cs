using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Bounded SQL-reported target-host volume capacity; not scheduled until catalog qualification.</summary>
public sealed class SqlServerVolumeCapacityCollector : SqlServerHealthCollector
{
    private static readonly string PinnedQuery = SqlServerVolumeCapacitySource.LoadPinnedQuery();
    private static readonly CollectorManifest VolumeManifest = CreateManifest();
    private static readonly CollectorOutputContract VolumeOutputContract = new(
        new CollectorOutputSchemaVersion(1), [], 0, 0, 0,
        maxSqlVolumeObservations: 1_000);

    private readonly IdentityFingerprintKey _key;

    public static CollectorOutputContract OutputContract => VolumeOutputContract;

    public SqlServerVolumeCapacityCollector(IdentityFingerprintKey key)
        : this(key, new SqlServerIntegratedConnectionFactory(
            SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerVolumeCapacityCollector(IdentityFingerprintKey key,
        ISqlServerConnectionFactory connectionFactory)
        : base(VolumeManifest, static major => major is >= 15 and <= 17
            ? PinnedQuery : throw new ArgumentOutOfRangeException(nameof(major)), connectionFactory)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    private protected override async ValueTask<CollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request, SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        SqlVolumeSourceRead source = await SqlServerVolumeCapacitySource.ReadAsync(
            reader, request.TargetId, request.TargetRevision, request.RunId, _key,
            Manifest.Limits.MaxRows, Manifest.Limits.MaxResponseBytes,
            cancellationToken).ConfigureAwait(false);
        return new CollectorReadResult(
            new CollectorPayload(sqlVolumes: source.Volumes), source.SourceRowsRead,
            source.ResponseBytes,
            CreateLossEvidence(source.ResponseByteLimitReached, source.SourceRowLimitReached));
    }

    private static CollectorManifest CreateManifest() => new(
        new CollectorId("storage.volume"),
        new CollectorDisplayName("SQL Server target-host volume capacity"),
        new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds"),
         new CapabilityId("authentication.windows-integrated"),
         new CapabilityId("transport.tls-validated"),
         new CapabilityId("privilege.non-sysadmin"),
         new CapabilityId("platform.windows")],
        [new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"),
             PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"),
             PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-any-definition"),
             PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 17))],
        new SqlServerMajorVersionRange(15, 17),
        [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            1_000, 4_194_304, CollectorEstimatedCost.Moderate),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
        new CollectorOutputSchemaVersion(1),
        CollectorOperationalMode.Passive,
        dependsOn: [new CollectorId("capability.connection"), new CollectorId("engine.core"),
            new CollectorId("database.files")],
        resilience: new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100),
            3, TimeSpan.FromMinutes(5)),
        outputKind: CollectorOutputKind.SqlVolumes);
}
