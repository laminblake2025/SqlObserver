using System.Security.Claims;
using System.Text.Json;
using SqlObserver.Reporting;

namespace SqlObserver.Server;

/// <summary>Rejects declared oversized API bodies before model binding or service invocation.</summary>
public sealed class RequestBodyLimitMiddleware
{
    public const long MaximumRequestBytes = 65_536;
    // Kestrel must allow this one-byte lookahead to reach the route-aware
    // boundary, including for chunked requests without Content-Length.
    public const long KestrelMaximumRequestBytes = MaximumRequestBytes + 1;

    private readonly RequestDelegate _next;

    public RequestBodyLimitMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Guid targetId = Guid.Empty;
        bool reportCreate = context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && context.Request.Path.Value is string path
            && path.EndsWith("/reports", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(context.Request.RouteValues["instanceId"]?.ToString(), out targetId);
        if (!reportCreate)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering(bufferThreshold: (int)MaximumRequestBytes, bufferLimit: KestrelMaximumRequestBytes);
        byte[] buffer = new byte[8192]; int total = 0; int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaximumRequestBytes)
            {
                await RejectAsync(context, targetId, "oversize", StatusCodes.Status413PayloadTooLarge, "The request body exceeds its byte limit.").ConfigureAwait(false);
                return;
            }
        }
        context.Request.Body.Position = 0;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                await RejectAsync(context, targetId, "failure", StatusCodes.Status400BadRequest, "The report request body is invalid.").ConfigureAwait(false);
                return;
            }
        }
        catch (JsonException)
        {
            await RejectAsync(context, targetId, "failure", StatusCodes.Status400BadRequest, "The report request body is invalid.").ConfigureAwait(false);
            return;
        }
        context.Request.Body.Position = 0;
        await _next(context).ConfigureAwait(false);
    }

    private static async Task RejectAsync(HttpContext context, Guid targetId, string activityKind, int statusCode, string message)
    {
        string correlationId = Guid.NewGuid().ToString("D");
        try
        {
            IReportAuditPort? audit = context.RequestServices.GetService<IReportAuditPort>();
            if (audit is null) throw new InvalidOperationException("Report audit is unavailable.");
            string actor = context.User.FindFirstValue(ClaimTypes.PrimarySid) ?? "anonymous";
            await audit.AppendAsync(new ReportAuditEvent(targetId, null, actor, activityKind, "failed"), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            statusCode = StatusCodes.Status503ServiceUnavailable;
            message = "The report request could not be audited.";
        }
        context.Response.StatusCode = statusCode;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await context.Response.WriteAsJsonAsync(new SqlObserverProblemResponse(activityKind == "oversize" ? "payload_too_large" : "invalid_report_request", message, correlationId), cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }
}
