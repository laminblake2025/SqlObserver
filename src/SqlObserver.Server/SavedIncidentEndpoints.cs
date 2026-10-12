using System.Text.Json;
using System.Globalization;
using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class SavedIncidentEndpoints
{
    private const int MaximumRequestBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static IEndpointRouteBuilder MapSavedIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/incidents/saved").RequireAuthorization();
        group.MapPost("/", SaveAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapGet("/{threadId:guid}", ReadAsync);
        group.MapGet("/", ListAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext context, Guid instanceId, ISavedIncidentService service,
        WindowsGroupRoleResolver roles, SavedIncidentListCursorCodec codec, string? limit = null, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ApiCorrelation.Begin(context); context.Response.Headers.CacheControl = "no-store";
        try
        {
            SavedIncidentListCursor? continuation = cursor is null ? null : codec.Decode(cursor);
            int count = limit is null ? continuation?.Limit ?? 20
                : int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed
                : throw new ArgumentException("The page limit is invalid.");
            SavedIncidentListPage page = await service.ListAsync(roles.Resolve(context.User), new(instanceId, count, continuation), cancellationToken).ConfigureAwait(false);
            SavedIncidentSummary? last = page.HasMore ? page.Items[^1] : null;
            string? next = last is null ? null : codec.Encode(new(instanceId, page.PublicationRevision, page.SnapshotUtc, last.SavedAtUtc, last.ThreadId, count));
            return Results.Ok(new { page.TargetId, page.SnapshotUtc, page.Items, page.HasMore, nextCursor = next });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (SavedIncidentConflictException) { return Results.Conflict(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (InvalidDataException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
        catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> SaveAsync(HttpContext context, Guid instanceId, ISavedIncidentService service,
        WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        var correlation = ApiCorrelation.Begin(context);
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            AuthorizationContext authorization = roles.Resolve(context.User);
            authorization.RequireAny(new MonitoredInstanceId(instanceId), ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (context.Request.ContentLength > MaximumRequestBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var body = new MemoryStream();
            var buffer = new byte[2048];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > MaximumRequestBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body.Write(buffer, 0, count);
            }
            body.Position = 0;
            SaveIncidentBody? parsed = await JsonSerializer.DeserializeAsync<SaveIncidentBody>(body, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (parsed is null) return Results.BadRequest();
            SavedIncident result = await service.SaveAsync(authorization,
                new(instanceId, parsed.ExpectedTargetRevision, parsed.OperationId, parsed.FromUtc, parsed.ToUtc,
                    parsed.Title, parsed.Notes), correlation.Value, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (SavedIncidentConflictException) { return Results.Conflict(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId, Guid threadId,
        ISavedIncidentService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        ApiCorrelation.Begin(context);
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            SavedIncident? result = await service.ReadAsync(roles.Resolve(context.User), instanceId, threadId, cancellationToken).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}

public sealed record SaveIncidentBody(long ExpectedTargetRevision, Guid OperationId, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, string Title, string Notes);
