using System.Net;
using System.Text.Json;

namespace SqlObserver.ApiContractTests;

public sealed class PlanCacheIntervalRankingHttpTests(TargetReadAuthorizationApiFactory factory)
    : IClassFixture<TargetReadAuthorizationApiFactory>
{
    [Theory]
    [InlineData("mixed-auditor", false, false)]
    [InlineData("mixed-auditor", true, true)]
    [InlineData("viewer-b", false, true)]
    public async Task CacheRankingEnforcesTheOperationalRoleOnTheRequestedTarget(string identity, bool targetA, bool allowed)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TargetReadTestAuthenticationHandler.IdentityHeader, identity);
        var target = targetA ? TargetReadAuthorizationApiFactory.TargetA : TargetReadAuthorizationApiFactory.TargetB;
        int before = factory.Repository.ReadTargets.Count;
        using var response = await client.GetAsync($"/api/v1/observation-targets/{target.Value}/query-performance/cache/top?metric=cpu");
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before + (allowed ? 1 : 0), factory.Repository.ReadTargets.Count);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (allowed)
        {
            using var content = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("plan_cache_delta", content.RootElement.GetProperty("semantics").GetString());
            Assert.Equal("no_comparable_rows", content.RootElement.GetProperty("coverage").GetString());
            Assert.Empty(content.RootElement.GetProperty("items").EnumerateArray());
        }
    }

    [Theory]
    [InlineData("metric=unknown")]
    [InlineData("limit=101")]
    [InlineData("fromUtc=2026-09-01T00:00:00Z&toUtc=2026-09-09T00:00:00Z")]
    [InlineData("fromUtc=2026-09-28T00:00:00.0000001Z&toUtc=2026-09-29T00:00:00Z")]
    public async Task InvalidBoundsFailBeforeRepositoryReads(string query)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TargetReadTestAuthenticationHandler.IdentityHeader, "viewer-b");
        int before = factory.Repository.ReadTargets.Count;
        using var response = await client.GetAsync($"/api/v1/observation-targets/{TargetReadAuthorizationApiFactory.TargetB.Value}/query-performance/cache/top?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, factory.Repository.ReadTargets.Count);
    }

    [Theory]
    [InlineData("mixed-auditor", false, false)]
    [InlineData("mixed-auditor", true, true)]
    [InlineData("viewer-b", false, true)]
    public async Task CacheHistoryEnforcesExactTargetRoleAndRequiresExplicitScope(string identity, bool targetA, bool allowed)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TargetReadTestAuthenticationHandler.IdentityHeader, identity);
        var target = targetA ? TargetReadAuthorizationApiFactory.TargetA : TargetReadAuthorizationApiFactory.TargetB;
        string path = $"/api/v1/observation-targets/{target.Value}/query-performance/cache/databases/6/history/{new string('b',64)}/plans/{new string('c',64)}";
        string scope = $"databaseIncarnation={new string('a',64)}&fromUtc=2026-09-28T00:00:00Z&toUtc=2026-09-29T00:00:00Z";
        int before = factory.Repository.ReadTargets.Count;
        using var response = await client.GetAsync($"{path}?{scope}");
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(before + (allowed ? 1 : 0), factory.Repository.ReadTargets.Count);
        if (allowed)
        {
            using var content = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, content.RootElement.GetProperty("nextCursor").ValueKind);
            Assert.False(content.RootElement.TryGetProperty("snapshot", out _));
            foreach (string invalid in new[] { "", scope + "&limit=101", scope + "&cursor=tampered", scope.Replace(new string('a',64), "invalid") })
            {
                using var bad = await client.GetAsync($"{path}?{invalid}");
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            }
            Assert.Equal(before + 1, factory.Repository.ReadTargets.Count);
        }
    }
}
