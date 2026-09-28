using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>
/// Produces one dimensioned PLE metric per SQL Server Buffer Node. Production
/// registration awaits a versioned catalog entry for the dedicated fenced writer.
/// </summary>
public sealed class SqlServerBufferNodePleCollector : SqlServerHealthCollector
{
    private static readonly string PinnedQuery = SqlServerBufferNodePleSource.LoadPinnedQuery();
    private static readonly MetricId PleMetric = new("engine.buffer_node_page_life_expectancy_seconds");
    private static readonly CollectorManifest NodeManifest = CreateManifest();
    private static readonly CollectorOutputContract NodeOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        [new CollectorMetricOutputContract(PleMetric, ["node_id"])],
        maxMetricSamples: 128, maxDatabaseObservations: 0, maxDatabaseFileObservations: 0);

    public static CollectorOutputContract OutputContract => NodeOutputContract;

    public SqlServerBufferNodePleCollector()
        : this(new SqlServerIntegratedConnectionFactory(
            SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerBufferNodePleCollector(ISqlServerConnectionFactory connectionFactory)
        : base(NodeManifest, static major => major is >= 15 and <= 17
            ? PinnedQuery : throw new ArgumentOutOfRangeException(nameof(major)), connectionFactory)
    {
    }

    private protected override async ValueTask<CollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request, SqlDataReader reader, CancellationToken cancellationToken)
    {
        BufferNodePleRead source = await SqlServerBufferNodePleSource.ReadAsync(
            reader, maximumNodes: Manifest.Limits.MaxRows - 1,
            Manifest.Limits.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        MetricSample[] metrics = source.Incomplete ? [] : source.Nodes.Select(node =>
            new MetricSample(SampleId(request.RunId, node.NodeId), request.TargetId,
                PleMetric, source.ObservedAtUtc, node.PageLifeExpectancySeconds,
                [new MetricDimension("node_id", node.NodeId.ToString(CultureInfo.InvariantCulture))])).ToArray();
        return new CollectorReadResult(new CollectorPayload(metrics), source.SourceRowsRead,
            source.ResponseBytes, CreateLossEvidence(source.ResponseByteLimitReached,
                source.SourceRowLimitReached));
    }

    private static MetricSampleId SampleId(CollectorRunId runId, int nodeId)
    {
        byte[] identity = Encoding.UTF8.GetBytes(string.Concat(runId.Value.ToString("D"),
            "\n", PleMetric.Value, "\n", nodeId.ToString(CultureInfo.InvariantCulture)));
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        return new MetricSampleId(new Guid(digest[..16]));
    }

    private static CollectorManifest CreateManifest() => new(
        new CollectorId("engine.buffer-nodes"),
        new CollectorDisplayName("SQL Server Buffer Node page life expectancy"),
        new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds"),
         new CapabilityId("authentication.windows-integrated"),
         new CapabilityId("transport.tls-validated"),
         new CapabilityId("privilege.non-sysadmin"),
         new CapabilityId("platform.windows")],
        [new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"),
             PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"),
             PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17))],
        new SqlServerMajorVersionRange(15, 17),
        [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            129, 65_536, CollectorEstimatedCost.Low),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
        new CollectorOutputSchemaVersion(1), CollectorOperationalMode.Passive,
        dependsOn: [new CollectorId("capability.connection"), new CollectorId("engine.core")],
        resilience: new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100),
            3, TimeSpan.FromMinutes(5)),
        outputKind: CollectorOutputKind.Metrics);
}
