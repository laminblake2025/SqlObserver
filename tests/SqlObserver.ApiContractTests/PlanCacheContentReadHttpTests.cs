using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.ApiContractTests;

public sealed class PlanCacheContentReadHttpTests(TargetReadAuthorizationApiFactory factory)
    : IClassFixture<TargetReadAuthorizationApiFactory>
{
    [Theory]
    [InlineData("viewer-reader-b", false, true)]
    [InlineData("mixed-reader", false, false)]
    [InlineData("mixed-reader", true, false)]
    [InlineData("viewer-b", false, false)]
    public async Task PipelineRequiresBothRolesOnTheRequestedTargetAndAuditsOnce(string identity, bool targetA, bool allowed)
    {
        var repository = new Repository();
        using var app = App(repository);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TargetReadTestAuthenticationHandler.IdentityHeader, identity);
        string path = Path(targetA);
        using var response = await client.GetAsync(path);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(allowed ? 1 : 0, repository.Reads);
        Assert.Equal([allowed ? "opened" : "denied"], repository.Outcomes);
        if (allowed)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("<script>inert</script>", json.RootElement.GetProperty("text").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("xml").ValueKind);
            Assert.Equal("available", json.RootElement.GetProperty("status").GetString());
            foreach (var field in new[] { "payloadId", "fingerprint", "keyIdentifier", "nonce", "ciphertext" })
                Assert.False(json.RootElement.TryGetProperty(field, out _));
            using var plan = await client.GetAsync(path.Replace("/content/text", "/content/plan"));
            Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
            using var planJson = JsonDocument.Parse(await plan.Content.ReadAsStringAsync());
            Assert.Equal("<ShowPlanXML />", planJson.RootElement.GetProperty("xml").GetString());
            Assert.Equal(JsonValueKind.Null, planJson.RootElement.GetProperty("text").ValueKind);
            Assert.Equal(["opened", "opened"], repository.Outcomes);
        }
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("incarnation")]
    [InlineData("run")]
    [InlineData("entry")]
    public async Task InvalidExactIdentityFailsBeforeReadingOrAuditing(string fault)
    {
        var repository = new Repository();
        using var app = App(repository);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TargetReadTestAuthenticationHandler.IdentityHeader, "viewer-reader-b");
        string path = Path(false);
        path = fault switch
        {
            "kind" => path.Replace("/content/text", "/content/deadlock"),
            "incarnation" => path.Split('?')[0],
            "run" => path.Replace("cccccccc-cccc-4ccc-8ccc-cccccccccccc", Guid.Empty.ToString()),
            _ => path.Replace(new string('d', 64), "invalid"),
        };
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, repository.Reads);
        Assert.Empty(repository.Outcomes);
    }

    [Fact]
    public async Task AnonymousRequestIsChallengedBeforeReadingOrWritingAudit()
    {
        var repository = new Repository();
        using var app = App(repository);
        using var client = app.CreateClient();
        using var response = await client.GetAsync(Path(false));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, repository.Reads);
        Assert.Empty(repository.Outcomes);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App(Repository repository) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPlanCacheContentReadRepositoryPort>();
            services.RemoveAll<IQuerySensitiveContentProtector>();
            services.AddSingleton<IPlanCacheContentReadRepositoryPort>(repository);
            services.AddSingleton<IQuerySensitiveContentProtector>(new Protector());
        }));
    private static string Path(bool targetA) =>
        $"/api/v1/observation-targets/{(targetA ? TargetReadAuthorizationApiFactory.TargetA : TargetReadAuthorizationApiFactory.TargetB).Value}"
        + $"/query-performance/cache/databases/6/history/{new string('b', 64)}/plans/{new string('c', 64)}"
        + $"/runs/cccccccc-cccc-4ccc-8ccc-cccccccccccc/entries/{new string('d', 64)}/content/text?databaseIncarnation={new string('a', 64)}";
    private sealed class Repository : IPlanCacheContentReadRepositoryPort
    {
        internal int Reads { get; private set; }
        internal List<string> Outcomes { get; } = [];
        public ValueTask<PlanCacheContentStoredResult> ReadAsync(PlanCacheContentReadRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(new PlanCacheContentStoredResult("available", DateTimeOffset.UtcNow,
                new(request.Kind, new(new byte[32]), "AES-256-GCM", "test", new byte[12], new byte[16], new byte[1])));
        }
        public ValueTask AuditAsync(PlanCacheContentReadRequest request, string actorSid, string outcome, CancellationToken cancellationToken)
        { Outcomes.Add(outcome); return ValueTask.CompletedTask; }
    }
    private sealed class Protector : IQuerySensitiveContentProtector
    {
        public bool IsAvailable => true;
        public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Encoding.UTF8.GetBytes(payload.Kind == SensitivePayloadKind.QueryText ? "<script>inert</script>" : "<ShowPlanXML />"));
    }
}
