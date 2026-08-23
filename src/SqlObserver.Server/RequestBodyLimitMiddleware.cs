namespace SqlObserver.Server;

/// <summary>Rejects declared oversized API bodies before model binding or service invocation.</summary>
public sealed class RequestBodyLimitMiddleware
{
    public const long MaximumRequestBytes = 65_536;

    private readonly RequestDelegate _next;

    public RequestBodyLimitMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.ContentLength is > MaximumRequestBytes)
        {
            string correlationId = Guid.NewGuid().ToString("D");
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            await context.Response.WriteAsJsonAsync(
                    new SqlObserverProblemResponse(
                        "payload_too_large",
                        "The request body exceeds its byte limit.",
                        correlationId),
                    cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
