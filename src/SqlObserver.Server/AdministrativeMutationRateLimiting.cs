using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.Server;

internal sealed class AdministrativeMutationRateLimitSettings
{
    internal const string RequestsPerWindowKey =
        "SqlObserver:AdministrativeMutationLimits:RequestsPerWindow";
    internal const string WindowSecondsKey =
        "SqlObserver:AdministrativeMutationLimits:WindowSeconds";
    internal const string ConcurrentRequestsKey =
        "SqlObserver:AdministrativeMutationLimits:ConcurrentRequests";

    private const int DefaultRequestsPerWindow = 30;
    private const int DefaultWindowSeconds = 60;
    private const int DefaultConcurrentRequests = 2;

    private AdministrativeMutationRateLimitSettings(
        int requestsPerWindow,
        TimeSpan window,
        int concurrentRequests)
    {
        RequestsPerWindow = requestsPerWindow;
        Window = window;
        ConcurrentRequests = concurrentRequests;
    }

    internal int RequestsPerWindow { get; }

    internal TimeSpan Window { get; }

    internal int ConcurrentRequests { get; }

    internal static AdministrativeMutationRateLimitSettings FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        int requestsPerWindow = configuration.GetValue<int?>(RequestsPerWindowKey) ??
            DefaultRequestsPerWindow;
        int windowSeconds = configuration.GetValue<int?>(WindowSecondsKey) ??
            DefaultWindowSeconds;
        int concurrentRequests = configuration.GetValue<int?>(ConcurrentRequestsKey) ??
            DefaultConcurrentRequests;

        if (requestsPerWindow is <= 0 or > 10_000)
        {
            throw new InvalidOperationException(
                $"{RequestsPerWindowKey} must be between 1 and 10000.");
        }

        if (windowSeconds is <= 0 or > 3_600)
        {
            throw new InvalidOperationException(
                $"{WindowSecondsKey} must be between 1 and 3600.");
        }

        if (concurrentRequests is <= 0 or > 64)
        {
            throw new InvalidOperationException(
                $"{ConcurrentRequestsKey} must be between 1 and 64.");
        }

        return new AdministrativeMutationRateLimitSettings(
            requestsPerWindow,
            TimeSpan.FromSeconds(windowSeconds),
            concurrentRequests);
    }
}

internal sealed class AdministrativeMutationRateLimitPolicy : IRateLimiterPolicy<string>
{
    internal const string PolicyName = "administrative-mutations";

    private readonly AdministrativeMutationRateLimitSettings _settings;

    internal AdministrativeMutationRateLimitPolicy(
        AdministrativeMutationRateLimitSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        WriteRejectionAsync;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        string partitionKey = GetPrincipalPartitionKey(httpContext.User);
        return RateLimitPartition.Get(
            partitionKey,
            _ => RateLimiter.CreateChained(
                new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                {
                    PermitLimit = _settings.ConcurrentRequests,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }),
                new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                {
                    PermitLimit = _settings.RequestsPerWindow,
                    Window = _settings.Window,
                    AutoReplenishment = true,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                })));
    }

    private static string GetPrincipalPartitionKey(ClaimsPrincipal principal)
    {
        string identity = principal.FindFirstValue(ClaimTypes.PrimarySid) ??
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
            "authenticated-principal-without-identifier";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static async ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        HttpResponse response = context.HttpContext.Response;
        if (response.HasStarted)
        {
            return;
        }

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            double seconds = Math.Ceiling(Math.Clamp(retryAfter.TotalSeconds, 1, 3_600));
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        AuditCorrelationId correlationId = ApiCorrelation.Begin(context.HttpContext);
        await response.WriteAsJsonAsync(
                new SqlObserverProblemResponse(
                    "rate_limited",
                    "Too many administrative requests. Wait briefly and retry.",
                    correlationId.Value.ToString("D", CultureInfo.InvariantCulture)),
                cancellationToken)
            .ConfigureAwait(false);
    }
}

internal static class ApiCorrelation
{
    private const string HeaderName = "X-Correlation-ID";

    internal static AuditCorrelationId Begin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Guid value = context.Request.Headers.TryGetValue(HeaderName, out var header) &&
            header.Count == 1 &&
            Guid.TryParseExact(header[0], "D", out Guid parsed) &&
            parsed != Guid.Empty
                ? parsed
                : Guid.NewGuid();
        context.Response.Headers[HeaderName] = value.ToString("D", CultureInfo.InvariantCulture);
        return new AuditCorrelationId(value);
    }
}
