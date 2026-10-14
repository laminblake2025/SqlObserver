using System.Net;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication.Negotiate;
using SqlObserver.LabDashboardHost;
using Microsoft.Extensions.FileProviders;

// Workstation-only lab adapter: current local UI, existing authenticated lab API.
// No API payload translation, database credentials, or synthetic responses.
const string upstreamOrigin = "https://WIN-QNGOV5GDM24:5443";
const long maximumBodyBytes = 2 * 1024 * 1024;
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The lab dashboard requires Windows authentication.");
using WindowsIdentity processIdentity = WindowsIdentity.GetCurrent();
SecurityIdentifier ownerSid = processIdentity.User
    ?? throw new InvalidOperationException("The lab dashboard requires a process owner SID.");
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
int port = builder.Configuration.GetValue("LabDashboard:Port", 4322);
if (port is < 1024 or > 65535) throw new InvalidOperationException("Invalid lab dashboard port.");
string webRoot = Path.GetFullPath(builder.Configuration["LabDashboard:WebRoot"]
    ?? throw new InvalidOperationException("Specify the verified local web/dist directory."));
if (!File.Exists(Path.Join(webRoot, "index.html"))) throw new InvalidOperationException("Build the local dashboard first.");
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maximumBodyBytes);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
using HttpClientHandler handler = new()
{
    UseDefaultCredentials = true,
    UseCookies = false,
    AllowAutoRedirect = false,
};
using HttpClient upstream = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
string[] requestHeaders = ["Accept", "If-Match", "If-None-Match", "X-Correlation-ID"];
string[] responseHeaders = ["Content-Type", "Content-Disposition", "ETag", "Last-Modified", "X-Correlation-ID", "Retry-After"];
WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    HttpRequest request = context.Request;
    IPAddress? peer = context.Connection.RemoteIpAddress;
    string origin = $"http://{request.Host.Value}";
    bool safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
    bool allowed = peer is not null && IPAddress.IsLoopback(peer)
        && request.Host.Port == port && request.Host.Host is "localhost" or "127.0.0.1"
        && !request.Headers.Keys.Any(name => name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
        && (!request.Headers.TryGetValue("Origin", out var origins)
            || origins.Count == 1 && origins[0] == origin)
        && (!request.Headers.TryGetValue("Sec-Fetch-Site", out var sites)
            || sites.Count == 1 && (sites[0] == "same-origin" || safe && sites[0] == "none"));
    if (!safe)
        allowed &= request.Headers.TryGetValue("Origin", out var writeOrigins)
            && writeOrigins.Count == 1 && writeOrigins[0] == origin
            && request.ContentType is not null
            && request.ContentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase);
    if (!allowed)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next(context).ConfigureAwait(false);
});
app.UseAuthentication();
app.UseMiddleware<OwnerIdentityMiddleware>(ownerSid);
app.Use(async (context, next) =>
{
    HttpRequest request = context.Request;
    bool safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
    if (request.Path.StartsWithSegments("/api/v1", StringComparison.Ordinal))
    {
        // Resolve against a fixed origin; never accept an absolute or caller-selected destination.
        string resource = request.Path.ToUriComponent() + request.QueryString.ToUriComponent();
        using HttpRequestMessage outgoing = new(new HttpMethod(request.Method), upstreamOrigin + resource);
        foreach (string name in requestHeaders)
            if (request.Headers.TryGetValue(name, out var values)) outgoing.Headers.TryAddWithoutValidation(name, values.ToArray());
        if (!safe)
        {
            outgoing.Headers.Add("Origin", upstreamOrigin);
            outgoing.Headers.Add("Sec-Fetch-Site", "same-origin");
            // Windows negotiation can replay an HTTP request. Buffer the bounded
            // JSON body so a challenge cannot silently consume a mutation body.
            using MemoryStream body = new();
            byte[] buffer = new byte[16 * 1024];
            int count;
            while ((count = await request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > maximumBodyBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                await body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted).ConfigureAwait(false);
            }
            outgoing.Content = new ByteArrayContent(body.ToArray());
            outgoing.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        }
        try
        {
            using HttpResponseMessage response = await upstream.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            foreach (string name in responseHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    context.Response.Headers[name] = values.ToArray();
            }
            if (!HttpMethods.IsHead(request.Method))
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(new { code = "lab_unavailable", message = "The authenticated lab API is unavailable." }, context.RequestAborted).ConfigureAwait(false);
            }
        }
        return;
    }
    await next(context).ConfigureAwait(false);
});
app.MapGet("/__lab/status", () => Results.Json(new
{
    upstream = upstreamOrigin,
    authentication = "current_process_windows_identity",
    tls = "validated",
    syntheticData = false,
}));
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot) });
app.MapGet("/", () => Results.File(Path.Join(webRoot, "index.html"), "text/html"));
await app.RunAsync().ConfigureAwait(false);
