using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace SqlObserver.Server;

/// <summary>Explicit, isolated authentication for the synthetic local development repository.</summary>
internal static class DevelopmentAuthentication
{
    internal const string EnabledKey = "SqlObserver:DevelopmentAuthentication:Enabled";
    internal const string SchemeName = "SqlObserver.Development";
    internal const string ServerUrl = "http://127.0.0.1:5080";
    internal const string ActorSid = "S-1-5-21-104001-104002-104003-1001";
    internal const string GroupSid = "S-1-5-21-104001-104002-104003-2001";

    internal static bool ValidateConfiguration(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(EnabledKey)) return false;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Development authentication requires the Development environment.");
        if (!string.Equals(configuration[WebHostDefaults.ServerUrlsKey], ServerUrl, StringComparison.Ordinal)
            || configuration.GetSection("Kestrel:Endpoints").Exists())
            throw new InvalidOperationException("Development authentication requires its fixed loopback server URL without Kestrel endpoint overrides.");

        NpgsqlConnectionStringBuilder repository;
        try
        {
            repository = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("SqlObserverRepository"));
        }
        catch (ArgumentException)
        {
            // Never include a connection string or the provider's parsing exception.
            throw new InvalidOperationException("Development authentication requires the isolated synthetic repository configuration.");
        }
        if (!IPAddress.TryParse(repository.Host, out IPAddress? host) || !IsLoopback(host)
            || repository.Database != "sqlobserver_dev" || repository.Username != "sqlobserver_dev_app")
            throw new InvalidOperationException("Development authentication requires the isolated synthetic repository configuration.");
        return true;
    }

    internal static bool IsAllowedRequest(HttpRequest request)
    {
        IPAddress? peer = request.HttpContext.Connection.RemoteIpAddress;
        if (peer is null || !IsLoopback(peer) || request.Scheme != "http"
            || request.Host.Host != "127.0.0.1" || request.Host.Port is not (5080 or 5173)) return false;
        if (request.Headers.Keys.Any(static name => name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))) return false;

        bool safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method);
        bool sameOrigin = false;
        if (request.Headers.TryGetValue(HeaderNames.Origin, out var origins))
        {
            if (origins.Count != 1 || !string.Equals(origins[0], $"http://{request.Host.Value}", StringComparison.Ordinal)) return false;
            sameOrigin = true;
        }
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var sites))
        {
            if (sites.Count != 1 || sites[0] != "same-origin" && !(safe && sites[0] == "none")) return false;
            sameOrigin |= sites[0] == "same-origin";
        }
        if (safe) return true;
        return sameOrigin && MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? contentType)
            && contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}

internal sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!DevelopmentAuthentication.IsAllowedRequest(Request))
            return Task.FromResult(AuthenticateResult.Fail("The request is outside the local development authentication boundary."));

        Claim[] claims =
        [
            new(ClaimTypes.PrimarySid, DevelopmentAuthentication.ActorSid),
            new(ClaimTypes.GroupSid, DevelopmentAuthentication.GroupSid),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
