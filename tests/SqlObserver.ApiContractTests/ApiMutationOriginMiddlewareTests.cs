using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class ApiMutationOriginMiddlewareTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("https://localhost\t")]
    [InlineData("https://localhost\r\n")]
    [InlineData("https://localhost/")]
    [InlineData("https://localhost/path")]
    [InlineData("https://localhost?query")]
    [InlineData("https://localhost#fragment")]
    [InlineData("https://user@localhost")]
    [InlineData("https://user:password@localhost")]
    [InlineData("https://localhost\\")]
    [InlineData("https://localhost\\attacker.example")]
    [InlineData("https://%6cocalhost")]
    [InlineData("https://localhost%2f")]
    [InlineData("https://localhost,https://localhost")]
    [InlineData("https://localhost https://attacker.example")]
    [InlineData("localhost")]
    [InlineData("//localhost")]
    [InlineData("https:localhost")]
    [InlineData("https:///localhost")]
    [InlineData("ftp://localhost")]
    [InlineData("https://localhost:+443")]
    [InlineData("https://localhost:443x")]
    [InlineData("https://localhost:65536")]
    [InlineData("https://[::1")]
    public async Task RejectsMalformedOrNonAuthorityOriginsWithoutReadingTheBody(string origin)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Headers.Origin = origin;

        await harness.InvokeAsync();

        await harness.AssertRejectedAsync();
    }

    [Theory]
    [InlineData("https", "localhost", "https://LOCALHOST")]
    [InlineData("https", "LOCALHOST", "https://localhost")]
    [InlineData("https", "localhost", "HTTPS://localhost")]
    [InlineData("https", "localhost", "https://localhost:443")]
    [InlineData("https", "localhost:443", "https://localhost")]
    [InlineData("http", "localhost", "http://localhost:80")]
    [InlineData("http", "localhost:80", "http://localhost")]
    [InlineData("https", "[::1]", "https://[::1]")]
    [InlineData("https", "[::1]:443", "https://[::1]")]
    [InlineData("https", "[::1]", "https://[::1]:443")]
    [InlineData("https", "[::1]:5443", "https://[::1]:5443")]
    public async Task AcceptsMatchingAuthoritiesWithCaseDefaultPortsAndIpv6(string scheme, string host, string origin)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Scheme = scheme;
        harness.Context.Request.Host = new HostString(host);
        harness.Context.Request.Headers.Origin = origin;

        await harness.InvokeAsync();

        harness.AssertPassedThrough();
    }

    [Theory]
    [InlineData("https", "[::1]:5443", "https://[::1]")]
    [InlineData("https", "[::1]", "https://[::2]")]
    [InlineData("https", "localhost", "https://localhost.")]
    [InlineData("https", "localhost", "http://localhost:443")]
    public async Task RejectsDifferentAuthoritiesDespiteSimilarHostOrPort(string scheme, string host, string origin)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Scheme = scheme;
        harness.Context.Request.Host = new HostString(host);
        harness.Context.Request.Headers.Origin = origin;

        await harness.InvokeAsync();

        await harness.AssertRejectedAsync();
    }

    [Theory]
    [InlineData("Origin", "https://localhost")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    public async Task RejectsDuplicateBrowserMetadataEvenWhenBothValuesAgree(string header, string value)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Headers[header] = new StringValues([value, value]);

        await harness.InvokeAsync();

        await harness.AssertRejectedAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("same-origin, same-origin")]
    [InlineData("same-origin ")]
    [InlineData("SAME-ORIGIN")]
    [InlineData("invalid")]
    public async Task RejectsMalformedFetchMetadataEvenWithValidOrigin(string fetchSite)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Headers.Origin = "https://localhost";
        harness.Context.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        await harness.InvokeAsync();

        await harness.AssertRejectedAsync();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SafeMethodsAreNotInterceptedByTheMutationGuard(string method)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Method = method;
        harness.Context.Request.Headers.Origin = "https://attacker.example";
        harness.Context.Request.Headers["Sec-Fetch-Site"] = "cross-site";

        await harness.InvokeAsync();

        harness.AssertPassedThrough();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task EveryUnsafeMethodIsProtected(string method)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Method = method;
        harness.Context.Request.Headers.Origin = "https://attacker.example";

        await harness.InvokeAsync();

        await harness.AssertRejectedAsync();
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/api/v10/retention/drop")]
    [InlineData("/other")]
    public async Task OtherProtocolAndRouteBoundariesAreNotIntercepted(string path)
    {
        using var harness = new OriginHarness();
        harness.Context.Request.Path = path;
        harness.Context.Request.Headers.Origin = "https://attacker.example";

        await harness.InvokeAsync();

        harness.AssertPassedThrough();
    }

    private sealed class OriginHarness : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddOptions().BuildServiceProvider();
        private readonly MemoryStream requestBody = new(Encoding.UTF8.GetBytes("{malformed body"));
        private readonly MemoryStream responseBody = new();
        private bool nextCalled;

        internal OriginHarness()
        {
            Context.RequestServices = services;
            Context.Request.Method = "POST";
            Context.Request.Path = "/api/v1/retention/attestations";
            Context.Request.Scheme = "https";
            Context.Request.Host = new HostString("localhost");
            Context.Request.Body = requestBody;
            Context.Response.Body = responseBody;
        }

        internal DefaultHttpContext Context { get; } = new();

        internal Task InvokeAsync() => new ApiMutationOriginMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }).InvokeAsync(Context);

        internal async Task AssertRejectedAsync()
        {
            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status403Forbidden, Context.Response.StatusCode);
            Assert.Equal(0, requestBody.Position);
            responseBody.Position = 0;
            using JsonDocument response = await JsonDocument.ParseAsync(responseBody);
            Assert.Equal("cross_origin_mutation_denied", response.RootElement.GetProperty("code").GetString());
            Assert.Equal(Context.Response.Headers["X-Correlation-ID"].ToString(), response.RootElement.GetProperty("correlationId").GetString());
        }

        internal void AssertPassedThrough()
        {
            Assert.True(nextCalled);
            Assert.Equal(StatusCodes.Status200OK, Context.Response.StatusCode);
            Assert.Equal(0, requestBody.Position);
            Assert.Equal(0, responseBody.Length);
        }

        public void Dispose()
        {
            services.Dispose();
            requestBody.Dispose();
            responseBody.Dispose();
        }
    }
}
