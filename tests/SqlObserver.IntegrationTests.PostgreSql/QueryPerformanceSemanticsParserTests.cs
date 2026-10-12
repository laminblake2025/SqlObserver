using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class QueryPerformanceSemanticsParserTests
{
    [Theory]
    [InlineData("query_store_interval", QueryMetricSemantics.QueryStoreInterval)]
    [InlineData("plan_cache_cumulative", QueryMetricSemantics.PlanCacheCumulative)]
    [InlineData("plan_cache_delta", QueryMetricSemantics.PlanCacheDelta)]
    [InlineData("plan_cache_baseline", QueryMetricSemantics.PlanCacheBaseline)]
    [InlineData("reset", QueryMetricSemantics.Reset)]
    public void KnownRepositorySemanticsKeepTheirExactMeaning(string value, QueryMetricSemantics expected) =>
        Assert.Equal(expected, PostgreSqlQueryPerformanceApiProjectionPort.ParseSemantics(value));

    [Theory]
    [InlineData("query_store_delta")]
    [InlineData("")]
    [InlineData("QUERY_STORE_INTERVAL")]
    public void UnknownRepositorySemanticsCannotBeRelabelledAsPlanCacheCumulative(string value) =>
        Assert.Throws<InvalidDataException>(() => PostgreSqlQueryPerformanceApiProjectionPort.ParseSemantics(value));
}
