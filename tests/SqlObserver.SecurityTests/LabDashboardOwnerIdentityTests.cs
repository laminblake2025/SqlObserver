using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlObserver.LabDashboardHost;

namespace SqlObserver.SecurityTests;

public sealed class LabDashboardOwnerIdentityTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("POST")]
    public async Task AnonymousForgedBrowserMetadataCannotReachCredentialBroker(string method)
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity owner = WindowsIdentity.GetCurrent();
        await AssertAdmissionAsync(method, new ClaimsPrincipal(new ClaimsIdentity()), owner.User!, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthenticatedDifferentSidCannotReachCredentialBroker()
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        // Keep the authenticated Windows token but set the broker owner to a
        // distinct SID, exercising the same comparison as another local user.
        SecurityIdentifier otherOwner = new(WellKnownSidType.LocalSystemSid, null);
        if (caller.User == otherOwner) otherOwner = new(WellKnownSidType.LocalServiceSid, null);
        await AssertAdmissionAsync("GET", new WindowsPrincipal(caller), otherOwner, HttpStatusCode.Forbidden);
        await AssertAdmissionAsync("POST", new WindowsPrincipal(caller), otherOwner, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ClaimedOwnerSidWithoutWindowsTokenCannotReachCredentialBroker()
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity owner = WindowsIdentity.GetCurrent();
        ClaimsPrincipal spoofed = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, owner.User!.Value), new Claim(ClaimTypes.Name, owner.Name)], "Negotiate"));
        await AssertAdmissionAsync("GET", spoofed, owner.User, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("POST")]
    public async Task AuthenticatedOwnerCanReachCredentialBroker(string method)
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity owner = WindowsIdentity.GetCurrent();
        await AssertAdmissionAsync(method, new WindowsPrincipal(owner), owner.User!, HttpStatusCode.OK);
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertAdmissionAsync(string method, ClaimsPrincipal principal,
        SecurityIdentifier ownerSid, HttpStatusCode expected)
    {
        int brokerCalls = 0;
        using IHost host = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton(principal);
                services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddScheme<AuthenticationSchemeOptions, TestWindowsAuthenticationHandler>(NegotiateDefaults.AuthenticationScheme, _ => { });
            })
            .Configure(app =>
            {
                app.UseAuthentication();
                app.UseMiddleware<OwnerIdentityMiddleware>(ownerSid);
                app.Run(context =>
                {
                    brokerCalls++;
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return Task.CompletedTask;
                });
            })).StartAsync();
        using HttpClient client = host.GetTestClient();
        using HttpRequestMessage request = new(new HttpMethod(method), "/api/v1/observation-targets/");
        request.Headers.Host = "localhost:4322";
        request.Headers.Add("Origin", "http://localhost:4322");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("X-Owner-Sid", ownerSid.Value);
        if (method == "POST") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, brokerCalls);
        if (expected == HttpStatusCode.Unauthorized)
            Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Negotiate");
    }

    private sealed class TestWindowsAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, ClaimsPrincipal principal)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            principal.Identity?.IsAuthenticated == true
                ? AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name))
                : AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Negotiate";
            return Task.CompletedTask;
        }
    }
}