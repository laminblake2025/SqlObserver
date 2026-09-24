using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class BlockingChainBuilderRegressionTests
{
    private static readonly MonitoredInstanceId TargetId = new(Guid.Parse("51515151-5151-5151-5151-515151515151"));
    private static readonly ObservationTargetRevision Revision = new(5);
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelfSessionWaitIsExcludedWithoutReportingGraphLoss()
    {
        BlockingChainBuildResult result = Build([Session(51, 51)]);

        Assert.Empty(result.Observations);
        Assert.Equal(0, result.LostEdges);
    }

    [Fact]
    public void IncomingChainResolvesAtSessionWhoseOnlyWaitIsOnItself()
    {
        BlockingChainBuildResult result = Build([Session(51, 51), Session(52, 51)]);

        BlockingEdgeObservation edge = Assert.Single(result.Observations);
        Assert.Equal(52, edge.BlockedSessionId);
        Assert.Equal(51, edge.RootBlockerSessionId);
        Assert.Equal(1, edge.ChainDepth);
        Assert.Equal(BlockingChainState.Resolved, edge.ChainState);
        Assert.Equal(0, result.LostEdges);
    }

    [Fact]
    public void SelfSessionWaitCannotReplaceRealPrimaryEdgeForIncomingChain()
    {
        BlockingChainBuildResult result = Build([Session(51, 51), Session(51, 61), Session(71, 51)]);

        Assert.Equal(2, result.Observations.Count);
        BlockingEdgeObservation incoming = Assert.Single(result.Observations, edge => edge.BlockedSessionId == 71);
        Assert.Equal(61, incoming.RootBlockerSessionId);
        Assert.Equal(2, incoming.ChainDepth);
        Assert.All(result.Observations, edge => Assert.Equal(BlockingChainState.Resolved, edge.ChainState));
        Assert.Equal(0, result.LostEdges);
    }

    [Fact]
    public void SelfSessionWaitsDoNotConsumeGraphNodeBudget()
    {
        BlockingSourceEdge[] sources =
        [
            .. Enumerable.Range(1, BlockingChainLimits.MaximumNodes).Select(session => Session(session, session)),
            Session(500, 501),
        ];

        BlockingChainBuildResult result = Build(sources);

        BlockingEdgeObservation edge = Assert.Single(result.Observations);
        Assert.Equal(500, edge.BlockedSessionId);
        Assert.Equal(501, edge.RootBlockerSessionId);
        Assert.Equal(BlockingChainState.Resolved, edge.ChainState);
        Assert.Equal(0, result.LostEdges);
    }

    [Theory]
    [InlineData(BlockingBlockerKind.OrphanedDistributedTransaction)]
    [InlineData(BlockingBlockerKind.DeferredRecovery)]
    [InlineData(BlockingBlockerKind.Undetermined)]
    [InlineData(BlockingBlockerKind.AsyncLatch)]
    [InlineData(BlockingBlockerKind.Other)]
    public void SpecialBlockersRemainVisibleAfterSelfSessionFiltering(BlockingBlockerKind kind)
    {
        var external = new BlockingSourceEdge(51, kind, null, new SqlServerWaitType("LCK_M_S"), 1, 25, ObservedAt);

        BlockingChainBuildResult result = Build([Session(51, 51), external, Session(52, 51)]);

        Assert.Equal(2, result.Observations.Count);
        BlockingEdgeObservation root = Assert.Single(result.Observations, edge => edge.BlockedSessionId == 51);
        Assert.Equal(kind, root.BlockerKind);
        Assert.Null(root.BlockerSessionId);
        Assert.Null(root.RootBlockerSessionId);
        BlockingEdgeObservation incoming = Assert.Single(result.Observations, edge => edge.BlockedSessionId == 52);
        Assert.Equal(51, incoming.RootBlockerSessionId);
        Assert.All(result.Observations, edge => Assert.Equal(BlockingChainState.ExternalBlocker, edge.ChainState));
        Assert.Equal(0, result.LostEdges);
    }

    [Fact]
    public void RealMultiSessionCyclesRemainVisible()
    {
        BlockingChainBuildResult result = Build([Session(51, 51), Session(51, 52), Session(52, 51)]);

        Assert.Equal(2, result.Observations.Count);
        Assert.All(result.Observations, edge =>
        {
            Assert.Equal(BlockingChainState.Cycle, edge.ChainState);
            Assert.Equal(2, edge.ChainDepth);
            Assert.Equal(edge.BlockedSessionId, edge.RootBlockerSessionId);
        });
        Assert.Equal(0, result.LostEdges);
    }

    [Fact]
    public void GenuineGraphNodeLimitStillReportsLostEdges()
    {
        BlockingSourceEdge[] sources = Enumerable.Range(1, BlockingChainLimits.MaximumNodes)
            .Select(session => Session(session, session + 1)).ToArray();

        BlockingChainBuildResult result = Build(sources);

        Assert.Equal(BlockingChainLimits.MaximumNodes - 1, result.Observations.Count);
        Assert.Equal(1, result.LostEdges);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void BlockingQueryExcludesSelfSessionWaitsBeforeRowLimit(int major)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string sql = File.ReadAllText(Path.Combine(root, $"collectors/sql/blocking.current.sqlserver{major}-windows.v1.sql"));

        Assert.Contains("AND waiting.blocking_session_id <> waiting.session_id", sql, StringComparison.Ordinal);
    }

    private static BlockingChainBuildResult Build(BlockingSourceEdge[] sources) =>
        BlockingChainBuilder.Build(TargetId, Revision, sources);

    private static BlockingSourceEdge Session(int blocked, int blocker) =>
        new(blocked, BlockingBlockerKind.Session, blocker, new SqlServerWaitType("LCK_M_S"), 1, 25, ObservedAt);
}
