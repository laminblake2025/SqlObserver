using System.Globalization;
using System.Diagnostics;
using SqlObserver.Application.Ports;
using SqlObserver.Observability;

namespace SqlObserver.Server;

/// <summary>Converts unhandled failures to a fixed response without serializing provider details.</summary>
public sealed class SafeApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SafeApiExceptionMiddleware> _logger;
    private static readonly Action<ILogger, string, string, string, int, string, Exception?> RequestFailed = LoggerMessage.Define<string, string, string, int, string>(
        LogLevel.Warning, new EventId(4001, "RequestFailed"),
        "Request failed. CorrelationId={CorrelationId} Method={Method} Route={Route} StatusCode={StatusCode} FailureCategory={FailureCategory}");

    public SafeApiExceptionMiddleware(RequestDelegate next, ILogger<SafeApiExceptionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string correlationId = ReadCorrelationId(context);
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        context.Response.OnStarting(() =>
        {
            // Timeout middleware clears the response before writing its 504.
            // Keep the reference available even when an inner component resets headers.
            if (!context.Response.Headers.ContainsKey("X-Correlation-ID"))
                context.Response.Headers["X-Correlation-ID"] = correlationId;
            return Task.CompletedTask;
        });
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = RuntimeDiagnostics.StartRequest();
        string category = "http_status";
        bool aborted = false;
        int? failureStatus = null;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            category = "cancelled";
            aborted = true;
            context.Abort();
        }
        catch (BadHttpRequestException)
        {
            category = "invalid_request";
            failureStatus = StatusCodes.Status400BadRequest;
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "The request is invalid.")
                .ConfigureAwait(false);
        }
        catch (AlertRepositoryOperationException exception)
        {
            category = "repository_operation";
            failureStatus = exception.StatusCode;
            await WriteFailureAsync(context, exception.StatusCode, exception.Code, exception.Message).ConfigureAwait(false);
        }
        catch (AlertDestinationValidationException exception)
        {
            category = "destination_validation";
            failureStatus = exception.StatusCode;
            await WriteFailureAsync(context, exception.StatusCode, exception.StatusCode == StatusCodes.Status503ServiceUnavailable ? "destination_provider_unavailable" : "invalid_request", exception.StatusCode == StatusCodes.Status503ServiceUnavailable ? "The destination provider is unavailable." : "The destination configuration is invalid.").ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            category = "invalid_contract";
            failureStatus = StatusCodes.Status400BadRequest;
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "The request is invalid.")
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            category = "invalid_request";
            failureStatus = StatusCodes.Status400BadRequest;
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "The request is invalid.")
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            category = "state_conflict";
            failureStatus = StatusCodes.Status409Conflict;
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "operation_conflict",
                    "The operation conflicts with current state.")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            category = "timeout";
            failureStatus = StatusCodes.Status503ServiceUnavailable;
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "request_timed_out",
                    "The request exceeded its execution limit.")
                .ConfigureAwait(false);
        }
        catch
        {
            category = "dependency_failure";
            failureStatus = StatusCodes.Status500InternalServerError;
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "request_failed",
                    "The request could not be completed.")
                .ConfigureAwait(false);
        }
        finally
        {
            string method = context.Request.Method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS"
                ? context.Request.Method : "OTHER";
            string route = context.GetEndpoint() is RouteEndpoint endpoint && endpoint.RoutePattern.RawText is { Length: <= 256 } template
                ? template : "unmatched";
            // Some administrative endpoints supply the audit correlation in their response.
            if (context.Response.Headers.ContainsKey("X-Correlation-ID"))
                correlationId = ReadCorrelationId(context);
            int status = aborted ? 499 : failureStatus ?? context.Response.StatusCode;
            if (category == "http_status" && status == StatusCodes.Status504GatewayTimeout)
                category = "timeout";
            if (status >= 400)
                RequestFailed(_logger, correlationId, method, route, status, category, null);
            RuntimeDiagnostics.RecordRequest(activity, method, route, status, category, correlationId,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static async Task WriteFailureAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        string correlationId = ReadCorrelationId(context);
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await context.Response.WriteAsJsonAsync(
                new SqlObserverProblemResponse(code, message, correlationId),
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static string ReadCorrelationId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue("X-Correlation-ID", out var responseValue) &&
            responseValue.Count == 1 &&
            Guid.TryParseExact(responseValue[0], "D", out Guid responseCorrelation) &&
            responseCorrelation != Guid.Empty)
        {
            return responseCorrelation.ToString("D", CultureInfo.InvariantCulture);
        }

        if (context.Request.Headers.TryGetValue("X-Correlation-ID", out var requestValue) &&
            requestValue.Count == 1 &&
            Guid.TryParseExact(requestValue[0], "D", out Guid requestCorrelation) &&
            requestCorrelation != Guid.Empty)
        {
            return requestCorrelation.ToString("D", CultureInfo.InvariantCulture);
        }

        return Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
    }
}
