using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class TargetDeadlockEndpoints
{
    private const int DefaultLimit = 25;
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    public static IEndpointRouteBuilder MapTargetDeadlockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/deadlocks").RequireAuthorization();
        group.MapGet("", ListAsync); group.MapGet("/{eventId:guid}", DetailAsync); return endpoints;
    }
    private static async Task<IResult> ListAsync(HttpContext http, Guid instanceId, IDeadlockProjectionQueryService service, WindowsGroupRoleResolver resolver, int limit = DefaultLimit, string? fromUtc = null, string? toUtc = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        try
        {
            DeadlockPageCursor? decoded = cursor is null ? null : DecodeCursor(cursor);
            // A continuation belongs to the first page's immutable investigation window.
            DateTimeOffset to = ParseUtc(toUtc) ?? decoded?.ToUtc ?? DateTimeOffset.UtcNow;
            DateTimeOffset from = ParseUtc(fromUtc) ?? decoded?.FromUtc ?? to.AddHours(-24);
            DeadlockPage? page = await service.ListDeadlocksAsync(new ListDeadlocksQuery(resolver.Resolve(http.User), new MonitoredInstanceId(instanceId), from, to, limit, decoded, Timeout), cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(new DeadlockPageResponse(instanceId, page.RepositoryTimeUtc, page.Items.Select(x => new DeadlockSummaryResponse(x.EventId, x.OccurredAtUtc, x.Fingerprint, x.ParticipantCount, x.RelationCount, x.ParseTruncated, x.CollectedAtUtc)).ToArray(), page.NextCursor is null ? null : EncodeCursor(page.NextCursor)));
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> DetailAsync(HttpContext http, Guid instanceId, Guid eventId, IDeadlockProjectionQueryService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken = default)
    {
        try { DeadlockDetailDto? detail = await service.GetDeadlockAsync(resolver.Resolve(http.User), new MonitoredInstanceId(instanceId), eventId, Timeout, cancellationToken); return detail is null ? Results.NotFound() : Results.Ok(new DeadlockDetailResponse(Map(detail.Summary), detail.Participants.Select(x => new DeadlockParticipantResponse(x.SessionId, x.IsVictim)).ToArray(), detail.Relations.Select(x => new DeadlockRelationResponse(x.BlockerSessionId, x.WaiterSessionId, x.ResourceCategory, x.LockMode)).ToArray())); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static DeadlockSummaryResponse Map(DeadlockSummaryDto x) => new(x.EventId, x.OccurredAtUtc, x.Fingerprint, x.ParticipantCount, x.RelationCount, x.ParseTruncated, x.CollectedAtUtc);
    private static DateTimeOffset? ParseUtc(string? value) => value is null ? null : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) && parsed.Offset == TimeSpan.Zero ? parsed : throw new ArgumentException("UTC timestamp required.");
    private static string EncodeCursor(DeadlockPageCursor value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] { value.TargetId.Value, value.OccurredAtUtc, value.EventId, value.SnapshotCollectedAtUtc, value.FromUtc, value.ToUtc })));
    private static DeadlockPageCursor DecodeCursor(string value)
    { if (value.Length > 512) throw new ArgumentException("Cursor is too large."); try { using JsonDocument d = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))); JsonElement a = d.RootElement; if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 6) throw new ArgumentException("Cursor is invalid."); return new DeadlockPageCursor(new MonitoredInstanceId(a[0].GetGuid()), a[1].GetDateTimeOffset(), a[2].GetGuid(), a[3].GetDateTimeOffset(), a[4].GetDateTimeOffset(), a[5].GetDateTimeOffset()); } catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { throw new ArgumentException("Cursor is invalid.", e); } }
}
