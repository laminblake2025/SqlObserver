using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class QueryStoreGroupedTargetAssemblerTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly ObservationTargetRevision Revision = new(3);
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);

    [Fact]
    public void CompleteDatabasesRetainTheirDistinctWaitSnapshots()
    {
        QueryStoreGroupedTargetRead result = Combine([
            Read(6, QueryStoreGroupWaitCoverage.Complete),
            Read(7, QueryStoreGroupWaitCoverage.Complete)], 2, true);

        Assert.True(result.Output.RuntimeComplete);
        Assert.Equal(QueryStoreGroupWaitCoverage.Complete, result.Output.WaitCoverage);
        Assert.Equal(2, result.Output.Items.Count);
        Assert.All(result.Output.Items, item => Assert.NotNull(item.Waits));
        Assert.False(result.Loss.HasLoss);
        Assert.Equal(6, result.SourceRowsRead);
    }

    [Fact]
    public void MixedNonemptyWaitModesKeepDatabaseScopedWaitCoverage()
    {
        QueryStoreGroupedTargetRead result = Combine([
            Read(6, QueryStoreGroupWaitCoverage.Complete),
            Read(7, QueryStoreGroupWaitCoverage.CaptureOff)], 2, true);

        Assert.True(result.Output.RuntimeComplete);
        Assert.Equal(2, result.Output.Items.Count);
        Assert.Equal(QueryStoreGroupWaitCoverage.Mixed, result.Output.WaitCoverage);
        Assert.Equal(QueryStoreGroupWaitCoverage.Complete, result.Output.DatabaseWaitCoverage![6]);
        Assert.Equal(QueryStoreGroupWaitCoverage.CaptureOff, result.Output.DatabaseWaitCoverage[7]);
        Assert.NotNull(result.Output.Items.Single(item => item.Runtime.Key.Query.DatabaseId == 6).Waits);
        Assert.Null(result.Output.Items.Single(item => item.Runtime.Key.Query.DatabaseId == 7).Waits);
        Assert.False(result.Loss.HasLoss);
    }

    [Fact]
    public void CompleteInventoryCannotOmitAReadDatabase()
    {
        Assert.Throws<InvalidDataException>(() => Combine(
            [Read(6, QueryStoreGroupWaitCoverage.Complete)], 2, true));
    }

    [Fact]
    public void EmptyRuntimeDoesNotHideTruncatedWaitRead()
    {
        var runtime = new QueryStoreRuntimeSourcePage([], 0, true);
        var waits = new QueryStoreWaitSourcePage(QueryStoreWaitCaptureMode.On,
            Start.AddMinutes(3), [], 1, false);
        var read = new QueryStoreGroupedDatabaseRead(6, runtime, waits,
            QueryStoreGroupedSourceBundle.MapOutput(runtime, waits, Target, Revision,
                Start, End));

        QueryStoreGroupedTargetRead result = Combine([read], 1, true);
        Assert.True(result.Output.RuntimeComplete);
        Assert.Equal(QueryStoreGroupWaitCoverage.Truncated, result.Output.WaitCoverage);
        Assert.Equal(CollectorLossKind.SourceRowLimit, result.Loss.Kind);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IncompleteInventoryOrRuntimeWithholdsTargetBatch(bool inventoryIncomplete, bool runtimeIncomplete)
    {
        QueryStoreGroupedDatabaseRead first = Read(6, QueryStoreGroupWaitCoverage.Complete);
        if (runtimeIncomplete)
            first = first with
            {
                Runtime = first.Runtime with { Complete = false },
                Output = new QueryStoreGroupOutputBatch(Target, Revision, Start, End,
                    false, QueryStoreGroupWaitCoverage.Unavailable, []),
            };
        QueryStoreGroupedTargetRead result = Combine([first], inventoryIncomplete ? 2 : 1,
            !inventoryIncomplete);

        Assert.False(result.Output.RuntimeComplete);
        Assert.Empty(result.Output.Items);
        Assert.Equal(CollectorLossKind.SourceRowLimit, result.Loss.Kind);
    }

    private static QueryStoreGroupedTargetRead Combine(
        IReadOnlyList<QueryStoreGroupedDatabaseRead> reads, int inventoryRows, bool complete) =>
        QueryStoreGroupedTargetAssembler.Combine(Target, Revision, Start, End,
            reads, inventoryRows, complete);

    private static QueryStoreGroupedDatabaseRead Read(int databaseId,
        QueryStoreGroupWaitCoverage coverage)
    {
        var query = new QueryOpaqueIdentity(databaseId, new string('a', 64));
        var plan = new PlanOpaqueIdentity(query, new string('c', 64));
        var key = new QueryStoreRuntimeWatermarkKey(Target, Revision,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), query, plan,
            Start.AddMinutes(-1), 1, Start, End, 0);
        var runtime = new QueryStoreRuntimeWatermark(key,
            new QueryStoreRuntimeCounters(1, 2, 3, 4, 5, 6),
            Start.AddMinutes(1), Start.AddMinutes(2), Start.AddMinutes(3));
        var runtimePage = new QueryStoreRuntimeSourcePage(
            [new QueryStoreRuntimeSourceRow(runtime, 11, 17)], 1, true);
        QueryStoreWaitSourcePage waits = coverage == QueryStoreGroupWaitCoverage.Complete
            ? new QueryStoreWaitSourcePage(QueryStoreWaitCaptureMode.On, Start.AddMinutes(3),
                [new QueryStoreWaitSourceRow(key, 3, 20)], 1, true)
            : new QueryStoreWaitSourcePage(QueryStoreWaitCaptureMode.Off,
                Start.AddMinutes(3), [], 0, false);
        QueryStoreGroupOutputBatch output = QueryStoreGroupedSourceBundle.MapOutput(
            runtimePage, waits, Target, Revision, Start, End);
        return new QueryStoreGroupedDatabaseRead(databaseId, runtimePage, waits, output);
    }
}
