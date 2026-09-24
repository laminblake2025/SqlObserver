using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class DevelopmentAuthenticationHttpTests
{
    [Theory]
    [InlineData("127.0.0.1:5080")]
    [InlineData("127.0.0.1:5173")]
    public async Task LocalRequestsUseTheFixedIdentityAndRealScopedRoleResolver(string host)
    {
        using var factory = new DevelopmentAuthenticationApiFactory();
        HttpContext response = await SendAsync(factory, host: host,
            path: $"/api/v1/observation-targets/{TargetReadAuthorizationApiFactory.TargetA.Value:D}/deadlocks");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(DevelopmentAuthentication.SchemeName, response.User.Identity?.AuthenticationType);
        Assert.Equal(DevelopmentAuthentication.ActorSid, response.User.FindFirstValue(ClaimTypes.PrimarySid));
        Assert.Equal(DevelopmentAuthentication.GroupSid, response.User.FindFirstValue(ClaimTypes.GroupSid));
        WindowsGroupRoleResolver resolver = factory.Services.GetRequiredService<WindowsGroupRoleResolver>();
        var authorization = resolver.Resolve(response.User);
        Assert.True(authorization.CanAccess(ApplicationRole.Viewer, TargetReadAuthorizationApiFactory.TargetA));
        Assert.False(authorization.CanAccess(ApplicationRole.Viewer, TargetReadAuthorizationApiFactory.TargetB));

        HttpContext outsideScope = await SendAsync(factory, host: host,
            path: $"/api/v1/observation-targets/{TargetReadAuthorizationApiFactory.TargetB.Value:D}/deadlocks");
        Assert.Equal(StatusCodes.Status403Forbidden, outsideScope.Response.StatusCode);
        Assert.Single(factory.Repository.ReadTargets);
    }

    [Fact]
    public async Task CallerControlledIdentityAndRoleHeadersCannotChangeThePrincipal()
    {
        using var factory = new DevelopmentAuthenticationApiFactory();
        HttpContext response = await SendAsync(factory, headers: new Dictionary<string, string>
        {
            ["X-SqlObserver-Test-Identity"] = "admin",
            ["X-Actor-Sid"] = "S-1-5-18",
            ["X-Group-Sid"] = "S-1-5-32-544",
            ["X-Role"] = "SecurityAdministrator",
            ["Authorization"] = "Bearer client-controlled",
        });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Collection(response.User.Claims,
            claim => { Assert.Equal(ClaimTypes.PrimarySid, claim.Type); Assert.Equal(DevelopmentAuthentication.ActorSid, claim.Value); },
            claim => { Assert.Equal(ClaimTypes.GroupSid, claim.Type); Assert.Equal(DevelopmentAuthentication.GroupSid, claim.Value); });
        var authorization = factory.Services.GetRequiredService<WindowsGroupRoleResolver>().Resolve(response.User);
        Assert.False(authorization.HasRole(ApplicationRole.SecurityAdministrator));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("ContractTesting")]
    [InlineData("CustomEnvironment")]
    public void EnabledAuthenticationRefusesEveryNonDevelopmentEnvironment(string environment)
    {
        using var factory = new DevelopmentAuthenticationApiFactory(environment: environment);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("requires the Development environment", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task DisabledAuthenticationRetainsNegotiateAndRegistersNoDevelopmentScheme(string environment)
    {
        using var factory = new DevelopmentAuthenticationApiFactory(environment: environment, enabled: false);
        IAuthenticationSchemeProvider schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync(DevelopmentAuthentication.SchemeName));
        Assert.NotNull(await schemes.GetSchemeAsync(NegotiateDefaults.AuthenticationScheme));
        Assert.Equal(NegotiateDefaults.AuthenticationScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:5080")]
    [InlineData("urls", "http://127.0.0.1:5080;http://0.0.0.0:5081")]
    [InlineData("urls", "https://127.0.0.1:5080")]
    [InlineData("Kestrel:Endpoints:Other:Url", "http://0.0.0.0:5081")]
    [InlineData("ConnectionStrings:SqlObserverRepository", "Host=192.0.2.1;Database=sqlobserver_dev;Username=sqlobserver_dev_app;Password=private-test-value")]
    [InlineData("ConnectionStrings:SqlObserverRepository", "Host=localhost;Database=sqlobserver_dev;Username=sqlobserver_dev_app;Password=private-test-value")]
    [InlineData("ConnectionStrings:SqlObserverRepository", "Host=127.0.0.1,192.0.2.1;Database=sqlobserver_dev;Username=sqlobserver_dev_app;Password=private-test-value")]
    [InlineData("ConnectionStrings:SqlObserverRepository", "Host=127.0.0.1;Database=production;Username=sqlobserver_dev_app;Password=private-test-value")]
    [InlineData("ConnectionStrings:SqlObserverRepository", "Host=127.0.0.1;Database=sqlobserver_dev;Username=production_user;Password=private-test-value")]
    [InlineData("ConnectionStrings:SqlObserverRepository", "private-test-value")]
    public void UnsafeListenerOrRepositoryConfigurationIsRejectedWithoutConnectionDetails(string key, string value)
    {
        using var factory = new DevelopmentAuthenticationApiFactory(overrides: new Dictionary<string, string?> { [key] = value });
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.StartsWith("Development authentication requires", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-test-value", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("192.0.2.20", "127.0.0.1:5080")]
    [InlineData("::ffff:192.0.2.20", "127.0.0.1:5080")]
    [InlineData(null, "127.0.0.1:5080")]
    [InlineData("127.0.0.1", "attacker.example:5080")]
    [InlineData("127.0.0.1", "localhost:5080")]
    [InlineData("127.0.0.1", "127.0.0.1:9999")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public async Task RemoteOrUnrecognizedConnectionsNeverReceiveTheDevelopmentIdentity(string? peer, string host)
    {
        using var factory = new DevelopmentAuthenticationApiFactory();
        HttpContext response = await SendAsync(factory, peer: peer, host: host);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.False(response.User.Identity?.IsAuthenticated ?? false);
    }

    [Theory]
    [InlineData("Forwarded", "for=127.0.0.1")]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-Host", "127.0.0.1:5080")]
    [InlineData("X-Forwarded-Proto", "http")]
    [InlineData("Origin", "https://attacker.example")]
    [InlineData("Origin", "null")]
    [InlineData("Origin", "http://127.0.0.1:9999")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    public async Task ForwardedAndCrossOriginRequestsCannotAuthenticate(string header, string value)
    {
        using var factory = new DevelopmentAuthenticationApiFactory();
        HttpContext response = await SendAsync(factory, headers: new Dictionary<string, string> { [header] = value });
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.False(response.User.Identity?.IsAuthenticated ?? false);
    }

    [Theory]
    [InlineData(null, "application/json")]
    [InlineData("http://127.0.0.1:5080", "text/plain")]
    [InlineData("http://127.0.0.1:5080", "application/x-www-form-urlencoded")]
    [InlineData("https://attacker.example", "application/json")]
    public async Task UnsafeRequestsRequireSameOriginEvidenceAndJson(string? origin, string contentType)
    {
        using var factory = new DevelopmentAuthenticationApiFactory();
        var headers = new Dictionary<string, string> { ["Content-Type"] = contentType };
        if (origin is not null) headers["Origin"] = origin;
        HttpContext response = await SendAsync(factory, method: "POST", headers: headers);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.False(response.User.Identity?.IsAuthenticated ?? false);
    }

    [Theory]
    [InlineData("127.0.0.1:5080", "Origin", "http://127.0.0.1:5080")]
    [InlineData("127.0.0.1:5173", "Origin", "http://127.0.0.1:5173")]
    [InlineData("127.0.0.1:5173", "Sec-Fetch-Site", "same-origin")]
    public async Task SameOriginJsonRequestsPassAuthenticationWithoutAddingAMutationRoute(string host, string header, string value)
    {
        using var factory = new DevelopmentAuthenticationApiFactory();
        HttpContext response = await SendAsync(factory, host: host, method: "POST", headers: new Dictionary<string, string>
        {
            [header] = value,
            ["Content-Type"] = "application/json; charset=utf-8",
        });
        // The real service descriptor is GET-only. A method mismatch proves authentication
        // passed without executing an administrative mutation against a fixture repository.
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.Response.StatusCode);
        Assert.True(response.User.Identity?.IsAuthenticated);
    }

    private static Task<HttpContext> SendAsync(DevelopmentAuthenticationApiFactory factory,
        string? peer = "127.0.0.1", string host = "127.0.0.1:5080", string method = "GET",
        string path = "/api/v1/service", IReadOnlyDictionary<string, string>? headers = null) =>
        factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Host = HostString.FromUriComponent(host);
            context.Request.Method = method;
            context.Request.Path = path;
            if (headers is not null)
                foreach ((string key, string value) in headers) context.Request.Headers[key] = value;
        });
}

internal sealed class DevelopmentAuthenticationApiFactory(
    string environment = "Development", bool enabled = true,
    IReadOnlyDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    internal TargetReadRepository Repository { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        var settings = new Dictionary<string, string?>
        {
            [DevelopmentAuthentication.EnabledKey] = enabled.ToString(),
            [WebHostDefaults.ServerUrlsKey] = DevelopmentAuthentication.ServerUrl,
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Database=sqlobserver_dev;Username=sqlobserver_dev_app",
            ["SqlObserver:Authorization:Bindings:0:GroupSid"] = DevelopmentAuthentication.GroupSid,
            ["SqlObserver:Authorization:Bindings:0:Roles:0"] = "Viewer",
            ["SqlObserver:Authorization:Bindings:0:AllTargets"] = "false",
            ["SqlObserver:Authorization:Bindings:0:TargetIds:0"] = TargetReadAuthorizationApiFactory.TargetA.Value.ToString("D"),
        };
        if (overrides is not null)
            foreach ((string key, string? value) in overrides) settings[key] = value;
        foreach ((string key, string? value) in settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDeadlockProjectionRepositoryPort>();
            services.AddSingleton<IDeadlockProjectionRepositoryPort>(Repository);
        });
    }
}
