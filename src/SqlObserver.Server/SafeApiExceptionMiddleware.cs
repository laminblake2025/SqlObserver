using System.Globalization;
using SqlObserver.Application.Ports;

namespace SqlObserver.Server;

/// <summary>Converts unhandled failures to a fixed response without serializing provider details.</summary>
public sealed class SafeApiExceptionMiddleware
{
    private readonly RequestDelegate _next;

    public SafeApiExceptionMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (BadHttpRequestException)
        {
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "The request is invalid.")
                .ConfigureAwait(false);
        }
        catch (AlertRepositoryOperationException exception)
        {
            await WriteFailureAsync(context, exception.StatusCode, exception.Code, exception.Message).ConfigureAwait(false);
        }
        catch (AlertDestinationValidationException exception)
        {
            await WriteFailureAsync(context, exception.StatusCode, exception.StatusCode == StatusCodes.Status503ServiceUnavailable ? "destination_provider_unavailable" : "invalid_request", exception.StatusCode == StatusCodes.Status503ServiceUnavailable ? "The destination provider is unavailable." : "The destination configuration is invalid.").ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "The request is invalid.")
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "The request is invalid.")
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "operation_conflict",
                    "The operation conflicts with current state.")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "request_timed_out",
                    "The request exceeded its execution limit.")
                .ConfigureAwait(false);
        }
        catch
        {
            await WriteFailureAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "request_failed",
                    "The request could not be completed.")
                .ConfigureAwait(false);
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
