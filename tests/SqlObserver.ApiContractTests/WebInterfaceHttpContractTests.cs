using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class WebInterfaceHttpContractTests
{
    [Fact]
    public async Task ConfiguredWebBuildIsAuthenticatedSameOriginAndSecurityHardened()
    {
        string root = await CreateWebBuildAsync();
        try
        {
            using var factory = new WebInterfaceApiFactory(root);
            using HttpClient anonymous = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using HttpResponseMessage denied = await anonymous.GetAsync("/");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using HttpResponseMessage deniedAsset = await anonymous.GetAsync("/assets/entry-index-ABCDEFGH.js");
            Assert.Equal(HttpStatusCode.Unauthorized, deniedAsset.StatusCode);
            await EndpointAuthorizationAssertions.RequireAuthenticationAsync(factory.Services, webInterfaceConfigured: true);

            using HttpClient authenticated = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            authenticated.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");

            using HttpResponseMessage index = await authenticated.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", Assert.Single(index.Headers.GetValues("Cache-Control")));
            Assert.Contains("connect-src 'self'", Assert.Single(index.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
            Assert.Contains("/assets/entry-index-ABCDEFGH.js", await index.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using HttpResponseMessage asset = await authenticated.GetAsync("/assets/entry-index-ABCDEFGH.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal("text/javascript", asset.Content.Headers.ContentType?.MediaType);
            Assert.Equal("public, max-age=31536000, immutable", Assert.Single(asset.Headers.GetValues("Cache-Control")));
            Assert.Equal("export const ready = true;\n", await asset.Content.ReadAsStringAsync());

            using HttpResponseMessage missingAsset = await authenticated.GetAsync("/assets/missing-ABCDEFGH.js");
            Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);

            using HttpResponseMessage sourceMap = await authenticated.GetAsync("/assets/entry-index-ABCDEFGH.js.map");
            Assert.Equal(HttpStatusCode.NotFound, sourceMap.StatusCode);

            using HttpResponseMessage unknownApi = await authenticated.GetAsync("/api/v1/not-a-real-route");
            Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);

            using HttpResponseMessage descriptor = await authenticated.GetAsync("/api/v1/service");
            Assert.Equal(HttpStatusCode.OK, descriptor.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRejectsNonCanonicalOrIncompleteBuilds()
    {
        string root = await CreateWebBuildAsync();
        try
        {
            IConfiguration relativeConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [WebInterfaceAssetCatalog.ConfigurationKey] = "relative-web-root",
                })
                .Build();
            Assert.Throws<InvalidOperationException>(() => WebInterfaceAssetCatalog.Load(relativeConfiguration));

            await File.WriteAllTextAsync(
                Path.Combine(root, "index.html"),
                "<!doctype html>\r\n<script type=\"module\" src=\"/assets/entry-index-ABCDEFGH.js\"></script>\r\n",
                new UTF8Encoding(false));

            IConfiguration carriageReturnConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [WebInterfaceAssetCatalog.ConfigurationKey] = root,
                })
                .Build();
            Assert.Throws<InvalidOperationException>(() => WebInterfaceAssetCatalog.Load(carriageReturnConfiguration));

            await File.WriteAllTextAsync(
                Path.Combine(root, "index.html"),
                "<!doctype html>\n<script type=\"module\" src=\"/assets/entry-index-ABCDEFGH.js\"></script>\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(root, "assets", "plain.js"),
                "export {};\n",
                new UTF8Encoding(false));
            Assert.Throws<InvalidOperationException>(() => WebInterfaceAssetCatalog.Load(carriageReturnConfiguration));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CreateWebBuildAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"sqlobserver-web-{Guid.NewGuid():N}");
        string assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(
            Path.Combine(root, "index.html"),
            "<!doctype html>\n<html><head><script type=\"module\" src=\"/assets/entry-index-ABCDEFGH.js\"></script><link rel=\"stylesheet\" href=\"/assets/index-ABCDEFGH.css\"></head><body><div id=\"root\"></div></body></html>\n",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(assets, "entry-index-ABCDEFGH.js"),
            "export const ready = true;\n",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(assets, "index-ABCDEFGH.css"),
            "body { color: #111; }\n",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(assets, "entry-index-ABCDEFGH.js.map"),
            "{}\n",
            new UTF8Encoding(false));
        return root;
    }
}

internal sealed class WebInterfaceApiFactory(string webRoot) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.UseSetting(WebInterfaceAssetCatalog.ConfigurationKey, webRoot);
        builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    static _ => { });
        });
    }
}
