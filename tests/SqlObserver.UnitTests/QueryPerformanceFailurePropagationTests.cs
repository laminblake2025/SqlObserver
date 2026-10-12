using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class QueryPerformanceFailurePropagationTests
{
    [Theory]
    [InlineData(false, false, QueryPerformanceReadStatus.QueryStorePermissionDenied)]
    [InlineData(true, false, QueryPerformanceReadStatus.PlanCacheEmpty)]
    [InlineData(true, true, QueryPerformanceReadStatus.PlanCachePermissionDenied)]
    public async Task Sql916RetainsPermissionTriggerAcrossPermittedAndDeniedFallback(bool planCachePermission, bool planCacheDenied, QueryPerformanceReadStatus expected)
    {
        var database = new SqlServerDatabaseIdentity(5, "inventory");
        var reader = new ThrowingReader(() => TestSqlErrors.Create(916), planCacheDenied ? () => TestSqlErrors.Create(229) : null);
        QueryPerformanceReadResult result = await QueryPerformanceFallbackCoordinator.ReadAsync(database, reader, planCachePermission, CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Equal(QueryStoreState.PermissionDenied, result.SourceState);
        Assert.Equal(planCachePermission, result.FallbackAttempted);
        Assert.Equal(planCachePermission ? 1 : 0, reader.PlanCacheCalls);
    }

    [Theory]
    [InlineData(-2, QueryStoreState.TimedOut)]
    [InlineData(229, QueryStoreState.PermissionDenied)]
    [InlineData(297, QueryStoreState.PermissionDenied)]
    [InlineData(300, QueryStoreState.PermissionDenied)]
    public async Task ThrownQueryStoreFailuresKeepTriggerState(int number, QueryStoreState expectedState)
    {
        var result = await QueryPerformanceFallbackCoordinator.ReadAsync(new SqlServerDatabaseIdentity(5, "inventory"), new ThrowingReader(() => TestSqlErrors.Create(number)), true, CancellationToken.None);
        Assert.Equal(QueryPerformanceReadStatus.PlanCacheEmpty, result.Status);
        Assert.Equal(expectedState, result.SourceState);
    }

    [Theory]
    [InlineData(11001, false)]
    [InlineData(1205, false)]
    [InlineData(1222, false)]
    [InlineData(53, false)]
    [InlineData(10061, true)]
    [InlineData(1205, true)]
    public async Task TransientSqlFailuresEscapeFallbackToBoundedCollectorRetry(int number, bool inPlanCache)
    {
        SqlException failure = TestSqlErrors.Create(number);
        var reader = inPlanCache ? new ThrowingReader(() => TestSqlErrors.Create(916), () => failure) : new ThrowingReader(() => failure);
        SqlException actual = await Assert.ThrowsAsync<SqlException>(async () => await QueryPerformanceFallbackCoordinator.ReadAsync(new SqlServerDatabaseIdentity(5, "inventory"), reader, true, CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Equal(inPlanCache ? 1 : 0, reader.PlanCacheCalls);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ThrowingReader(() => { cancellation.Cancel(); return new OperationCanceledException(cancellation.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await QueryPerformanceFallbackCoordinator.ReadAsync(new SqlServerDatabaseIdentity(5, "inventory"), reader, true, cancellation.Token));
        Assert.Equal(0, reader.PlanCacheCalls);
    }

    [Theory]
    [InlineData(false, QueryPerformanceReadStatus.QueryStoreTimedOut)]
    [InlineData(true, QueryPerformanceReadStatus.PlanCacheEmpty)]
    public async Task TimeoutExceptionRetainsDeadlineTrigger(bool allowFallback, QueryPerformanceReadStatus expected)
    {
        var result = await QueryPerformanceFallbackCoordinator.ReadAsync(new SqlServerDatabaseIdentity(5, "inventory"), new ThrowingReader(() => new TimeoutException()), allowFallback, CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Equal(QueryStoreState.TimedOut, result.SourceState);
    }

    private sealed class ThrowingReader(Func<Exception> queryStoreFailure, Func<Exception>? cacheFailure = null) : IQueryPerformanceSourceReader
    {
        internal int PlanCacheCalls { get; private set; }
        public ValueTask<QueryPerformanceReadResult> ReadQueryStoreAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken) => ValueTask.FromException<QueryPerformanceReadResult>(queryStoreFailure());
        public ValueTask<QueryPerformanceReadResult> ReadPlanCacheAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken)
        {
            PlanCacheCalls++;
            return cacheFailure is null
                ? ValueTask.FromResult(new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.PlanCacheEmpty, [], "plan_cache_empty", true, false, 0, 0, QueryStoreState.ReadFailure))
                : ValueTask.FromException<QueryPerformanceReadResult>(cacheFailure());
        }
    }
}
