using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class QueryPerformanceCancellationPrecedenceTests
{
    private static readonly SqlServerDatabaseIdentity[] Databases = [new(5, "first"), new(6, "second")];
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CallerCancellationWinsOverConcurrentTransientAndRetainsCallerToken()
    {
        using var caller = new CancellationTokenSource();
        var reader = new GatedDatabaseReader(waitForCancellation: true);
        Task<IReadOnlyList<QueryPerformanceReadResult>> operation = SqlServerQueryPerformanceDatabaseOrchestrator.ReadAsync(Databases, reader, TestWait, caller.Token).AsTask();
        await reader.ReleaseFailureAfterBothReadersStartAsync();
        caller.Cancel();

        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation.WaitAsync(TestWait));
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.True(reader.SiblingWasCancelled);
    }

    [Fact]
    public async Task InternalDeadlineWinsOverConcurrentTransient()
    {
        var reader = new GatedDatabaseReader(waitForCancellation: true);
        Task<IReadOnlyList<QueryPerformanceReadResult>> operation = SqlServerQueryPerformanceDatabaseOrchestrator.ReadAsync(Databases, reader, TimeSpan.FromSeconds(1), CancellationToken.None).AsTask();
        await reader.ReleaseFailureAfterBothReadersStartAsync();

        await Assert.ThrowsAsync<TimeoutException>(async () => await operation.WaitAsync(TestWait));
        Assert.True(operation.IsCompleted, "The orchestrator must time out; the test wait guard must not supply the exception.");
        Assert.True(reader.SiblingWasCancelled);
    }

    [Fact]
    public async Task ConcurrentTransientStillPropagatesWhenNoCancellationOccurs()
    {
        var reader = new GatedDatabaseReader(waitForCancellation: false);
        Task<IReadOnlyList<QueryPerformanceReadResult>> operation = SqlServerQueryPerformanceDatabaseOrchestrator.ReadAsync(Databases, reader, TestWait, CancellationToken.None).AsTask();
        await reader.ReleaseFailureAfterBothReadersStartAsync();

        SqlException actual = await Assert.ThrowsAsync<SqlException>(async () => await operation.WaitAsync(TestWait));
        Assert.Same(reader.Failure, actual);
        Assert.False(reader.SiblingWasCancelled);
    }

    [Fact]
    public async Task CollectorPropagatesCallerCancellationAfterConcurrentTransient()
    {
        using var caller = new CancellationTokenSource();
        var reader = new GatedDatabaseReader(waitForCancellation: true);
        SqlServerQueryPerformanceCollector collector = CreateCollector(reader);
        Task<CollectorExecutionResult> operation = collector.CollectAsync(CreateRequest(TestWait), caller.Token).AsTask();
        await reader.ReleaseFailureAfterBothReadersStartAsync();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation.WaitAsync(TestWait));
        Assert.True(reader.SiblingWasCancelled);
    }

    [Fact]
    public async Task CollectorReportsDeadlineWhenSiblingTimesOutAfterTransient()
    {
        var reader = new GatedDatabaseReader(waitForCancellation: true);
        SqlServerQueryPerformanceCollector collector = CreateCollector(reader);
        Task<CollectorExecutionResult> operation = collector.CollectAsync(CreateRequest(TimeSpan.FromSeconds(1)), CancellationToken.None).AsTask();
        await reader.ReleaseFailureAfterBothReadersStartAsync();

        CollectorExecutionResult result = await operation.WaitAsync(TestWait);
        Assert.True(reader.SiblingWasCancelled);
        Assert.Equal(CollectorRunOutcome.TimedOut, result.Outcome);
        Assert.Equal(CollectorRunReason.DeadlineExceeded, result.Reason);
        QueryPerformanceTargetStatus status = Assert.IsType<QueryPerformanceTargetStatus>(result.Payload.QueryPerformanceTargetStatus);
        Assert.Equal("deadline_exceeded", status.Status);
        Assert.Equal("deadline_exceeded", status.Reason);
    }

    [Fact]
    public async Task CollectorRetainsTransientOutcomeWithoutCancellation()
    {
        var reader = new GatedDatabaseReader(waitForCancellation: false);
        SqlServerQueryPerformanceCollector collector = CreateCollector(reader);
        Task<CollectorExecutionResult> operation = collector.CollectAsync(CreateRequest(TestWait), CancellationToken.None).AsTask();
        await reader.ReleaseFailureAfterBothReadersStartAsync();

        CollectorExecutionResult result = await operation.WaitAsync(TestWait);
        Assert.Equal(CollectorRunOutcome.TransientFailure, result.Outcome);
        Assert.Equal(CollectorRunReason.TransientTargetFailure, result.Reason);
        Assert.False(reader.SiblingWasCancelled);
    }

    private static SqlServerQueryPerformanceCollector CreateCollector(GatedDatabaseReader reader) =>
        new(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new OrchestratedExecutionPort(reader));

    private static CollectorExecutionRequest CreateRequest(TimeSpan timeout)
    {
        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        return new CollectorExecutionRequest(request.RunId, request.TargetId, request.TargetRevision, request.ConnectionPolicy,
            request.CapabilityProfile, request.Attempt, new CollectorExecutionTimeout(timeout));
    }

    private sealed class OrchestratedExecutionPort(GatedDatabaseReader reader) : ISqlServerQueryPerformanceExecutionPort
    {
        public ValueTask<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SqlServerDatabaseIdentity>>(Databases);

        public async ValueTask<QueryPerformanceExecutionBatch> ReadDatabasesAsync(IReadOnlyList<SqlServerDatabaseIdentity> databases, CollectorExecutionRequest request, SharedResponseBudget responseBudget, CancellationToken cancellationToken) =>
            new(await SqlServerQueryPerformanceDatabaseOrchestrator.ReadAsync(databases, reader, request.Timeout.Value, cancellationToken));
    }

    private sealed class GatedDatabaseReader(bool waitForCancellation) : IQueryPerformanceDatabaseReader
    {
        private readonly TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource failureIssued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;
        internal SqlException Failure { get; } = TestSqlErrors.Create(1205);
        internal bool SiblingWasCancelled { get; private set; }

        internal async Task ReleaseFailureAfterBothReadersStartAsync()
        {
            await bothStarted.Task.WaitAsync(TestWait);
            releaseFailure.SetResult();
            await failureIssued.Task.WaitAsync(TestWait);
        }

        public async ValueTask<QueryPerformanceReadResult> ReadDatabaseAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
            if (database.DatabaseId == 5)
            {
                // This reader supplies the fault even if cancellation races
                // with its completion. WhenAll must observe both final states.
                await releaseFailure.Task;
                failureIssued.SetResult();
                throw Failure;
            }

            if (waitForCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { SiblingWasCancelled = true; throw; }
            }
            else
            {
                await failureIssued.Task;
            }
            return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreEmpty, [], "query_store_empty", false, false, 0, 0, QueryStoreState.ReadWrite);
        }
    }
}
