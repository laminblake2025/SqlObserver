using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class QueryStoreGroupCollectorTests
{
    [Fact]
    public void EmbeddedCatalogAndSqlSourceMatchReviewedPins()
    {
        SqlServerQueryStoreGroupAssetCatalog catalog =
            SqlServerQueryStoreGroupAssetCatalog.LoadEmbedded();
        Assert.Equal("2a026abedbde62a7c8ad101ef1716c15a31f82b576c2610ef03e60552803e7f9",
            catalog.ManifestChecksum);
        Assert.Equal("4d6c86e95950d0a60c72d7876283c505058b96ee25a102cf967aa8ffa220aa8a",
            SqlServerQueryStoreGroupAssetCatalog.ExpectedBundleChecksum);
        Assert.NotEmpty(QueryStoreGroupedSourceBundle.LoadEmbedded().RuntimeSql);
        Assert.NotEmpty(QueryStoreGroupedSourceBundle.LoadEmbedded().WaitSql);
    }

    [Theory]
    [InlineData(false, false, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed)]
    [InlineData(true, false, CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete)]
    [InlineData(false, true, CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit)]
    public async Task WindowAndSourceCoverageProduceHonestOutcomes(bool gap,
        bool capped, CollectorRunOutcome outcome, CollectorRunReason reason)
    {
        CollectorExecutionRequest request = Request();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var window = new QueryStoreGroupSourceWindow(request.TargetId,
            request.TargetRevision, now.AddHours(-2), now,
            gap ? now.AddDays(-8) : now.AddHours(-1), now, gap);
        var batch = new QueryStoreGroupOutputBatch(request.TargetId,
            request.TargetRevision, window.FromUtc, window.ToUtc,
            !capped, capped ? QueryStoreGroupWaitCoverage.Unavailable :
                QueryStoreGroupWaitCoverage.Complete, []);
        var sourceRead = new QueryStoreGroupedTargetRead(batch, 1,
            capped ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false)
                : CollectorLossEvidence.None);
        var collector = new SqlServerQueryStoreGroupCollector(new WindowPort(window),
            new SourcePort(sourceRead));

        CollectorExecutionResult result = await collector.CollectAsync(request,
            CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(gap || capped, result.Loss.HasLoss);
        Assert.Equal(!capped, result.Payload.QueryStoreGroups!.RuntimeComplete);
        new CollectorOutputValidator(SqlServerQueryStoreGroupCollector.OutputContract)
            .Validate(collector.Manifest, request, result);
    }

    [Fact]
    public async Task UnsupportedTargetNeverReadsWindowOrSource()
    {
        CollectorExecutionRequest request = Request(15);
        var window = new WindowPort((QueryStoreGroupSourceWindow?)null);
        var source = new SourcePort((QueryStoreGroupedTargetRead?)null);
        var collector = new SqlServerQueryStoreGroupCollector(window, source);

        CollectorExecutionResult result = await collector.CollectAsync(request,
            CancellationToken.None);

        Assert.Equal(CollectorRunOutcome.Unsupported, result.Outcome);
        Assert.Equal(0, window.Calls);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task WindowTimeoutAndInvalidSourceDoNotPublishGroups()
    {
        CollectorExecutionRequest request = Request();
        var timeout = new SqlServerQueryStoreGroupCollector(
            new WindowPort(new TimeoutException()),
            new SourcePort((QueryStoreGroupedTargetRead?)null));
        CollectorExecutionResult timedOut = await timeout.CollectAsync(request,
            CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.TimedOut, timedOut.Outcome);
        Assert.Null(timedOut.Payload.QueryStoreGroups);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var window = new QueryStoreGroupSourceWindow(request.TargetId,
            request.TargetRevision, now.AddHours(-2), now, null, now, false);
        var invalid = new SqlServerQueryStoreGroupCollector(
            new WindowPort(window), new SourcePort(new InvalidDataException()));
        CollectorExecutionResult rejected = await invalid.CollectAsync(request,
            CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, rejected.Outcome);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, rejected.Loss.Kind);
        Assert.Null(rejected.Payload.QueryStoreGroups);
    }

    private static CollectorExecutionRequest Request(int major = 16)
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var revision = new ObservationTargetRevision(1);
        var profile = new CapabilityProfile(target, revision,
            new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(major, 0, 1, 0),
                new SqlServerEditionName("Express"), SqlServerEngineEdition.Express,
                SqlServerPlatform.Windows), CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            true, false, [], [], TimeSpan.FromSeconds(1), 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));
        return new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()),
            target, revision, new SqlServerConnectionPolicy(new SqlServerEndpoint(
                new SqlServerHostName("test"), new SqlServerInstanceName("SQLEXPRESS")),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))), profile,
            new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromMinutes(2)));
    }

    private sealed class WindowPort : IQueryStoreGroupWindowPort
    {
        private readonly QueryStoreGroupSourceWindow? window;
        private readonly Exception? failure;
        internal int Calls { get; private set; }
        internal WindowPort(QueryStoreGroupSourceWindow? window) => this.window = window;
        internal WindowPort(Exception failure) => this.failure = failure;
        public ValueTask<QueryStoreGroupSourceWindow> ReadNextAsync(
            MonitoredInstanceId targetId, ObservationTargetRevision revision,
            RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            Calls++;
            if (failure is not null) throw failure;
            return ValueTask.FromResult(window ?? throw new InvalidDataException());
        }
    }

    private sealed class SourcePort : IQueryStoreGroupedTargetSource
    {
        private readonly QueryStoreGroupedTargetRead? read;
        private readonly Exception? failure;
        internal int Calls { get; private set; }
        internal SourcePort(QueryStoreGroupedTargetRead? read) => this.read = read;
        internal SourcePort(Exception failure) => this.failure = failure;
        public Task<QueryStoreGroupedTargetRead> ReadAsync(
            CollectorExecutionRequest request, QueryStoreGroupSourceWindow window,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (failure is not null) throw failure;
            return Task.FromResult(read ?? throw new InvalidDataException());
        }
    }
}
