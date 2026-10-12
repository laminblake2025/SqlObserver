using Microsoft.Extensions.Primitives;

namespace SqlObserver.Server;

/// <summary>Rejects cross-origin browser mutations before API body parsing or repository access.</summary>
public sealed class ApiMutationOriginMiddleware
{
    private readonly RequestDelegate _next;

    public ApiMutationOriginMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HttpRequest request = context.Request;
        if (!request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase)
            || HttpMethods.IsGet(request.Method)
            || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method)
            || HasSameOriginBrowserContext(request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        string correlationId = ApiCorrelation.Begin(context).Value.ToString("D");
        await context.Response.WriteAsJsonAsync(
            new SqlObserverProblemResponse(
                "cross_origin_mutation_denied",
                "API mutations must originate from this application.",
                correlationId),
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    private static bool HasSameOriginBrowserContext(HttpRequest request)
    {
        // Native clients may omit browser metadata. A supplied header must be
        // valid and same-origin; one header cannot override the other.
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out StringValues fetchSite)
            && (fetchSite.Count != 1 || !string.Equals(fetchSite[0], "same-origin", StringComparison.Ordinal)))
        {
            return false;
        }

        if (!request.Headers.TryGetValue("Origin", out StringValues origins)) return true;
        if (origins.Count != 1 || origins[0] is not { Length: > 0 and <= 2048 } origin
            || origin.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? source)
            || source.Scheme is not ("http" or "https"))
        {
            return false;
        }

        // An Origin is only scheme and authority, never a URL with a path,
        // user information, escapes, query, or fragment (nor the literal null).
        string prefix = source.Scheme + "://";
        if (!origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || origin.AsSpan(prefix.Length).IndexOfAny("/\\?#@%".AsSpan()) >= 0
            || !Uri.TryCreate(request.Scheme + "://" + request.Host.ToUriComponent(), UriKind.Absolute, out Uri? destination))
        {
            return false;
        }

        return string.Equals(source.Scheme, destination.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.IdnHost, destination.IdnHost, StringComparison.OrdinalIgnoreCase)
            && source.Port == destination.Port;
    }
}
