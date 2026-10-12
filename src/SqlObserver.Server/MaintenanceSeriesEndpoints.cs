using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class MaintenanceSeriesEndpoints
{
    public sealed record DefinitionBody(string Frequency, int Interval, string StartDate, string EndDate,
        string LocalStartTime, int DurationMinutes, string TimeZoneId, int[] Weekdays, int? DayOfMonth,
        string GapPolicy, string FoldPolicy, string Reason);
    public sealed record CreateBody(string OperationId, string SeriesId, DefinitionBody Definition);
    public sealed record UpdateBody(string OperationId, long ExpectedRevision, string EffectiveFromDate, DefinitionBody Definition);
    public sealed record CancelBody(string OperationId, long ExpectedRevision, string EffectiveFromDate, string Reason);
    public sealed record PreviewBody(string SeriesId, DefinitionBody Definition, string? EffectiveFromDate);

    public static IEndpointRouteBuilder MapMaintenanceSeriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/alerts/maintenance/series").RequireAuthorization();
        group.MapGet("", ListAsync); group.MapGet("/{seriesId:guid}", GetAsync);
        group.MapGet("/{seriesId:guid}/occurrences", OccurrencesAsync);
        group.MapPost("/preview", Preview).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("", CreateAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPut("/{seriesId:guid}", UpdateAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/{seriesId:guid}/cancel", CancelAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }
    private static DateOnly Date(string value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            throw new ArgumentException("A canonical civil date is required.", nameof(value));
        return date;
    }
    private static Guid Id(string value) => AlertIdentifier.TryParseRfc4122(value, out Guid id) ? id : throw new ArgumentException("A canonical nonempty UUID is required.", nameof(value));
    private static MaintenanceSeriesDefinition Definition(DefinitionBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        MaintenanceSeriesFrequency frequency = body.Frequency switch { "daily" => MaintenanceSeriesFrequency.Daily, "weekly" => MaintenanceSeriesFrequency.Weekly, "monthly" => MaintenanceSeriesFrequency.Monthly, _ => throw new ArgumentException("Unknown recurrence frequency.") };
        MaintenanceFoldPolicy fold = body.FoldPolicy switch { "earlier" => MaintenanceFoldPolicy.Earlier, "later" => MaintenanceFoldPolicy.Later, _ => throw new ArgumentException("An explicit earlier/later fold policy is required.") };
        if (!TimeOnly.TryParseExact(body.LocalStartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time)) throw new ArgumentException("Local start must use HH:mm.");
        return new(body.TimeZoneId, Date(body.StartDate), Date(body.EndDate), time, body.DurationMinutes, frequency,
            body.Interval, body.Weekdays, body.DayOfMonth, body.GapPolicy, fold, body.Reason);
    }
    private static object DefinitionWire(MaintenanceSeriesDefinition value) => new { frequency = value.Frequency.ToString().ToLowerInvariant(),
        interval = value.Interval, startDate = value.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = value.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        localStartTime = value.LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture), durationMinutes = value.DurationMinutes,
        timeZoneId = value.TimeZoneId, weekdays = value.Weekdays, dayOfMonth = value.DayOfMonth, gapPolicy = value.GapPolicy,
        foldPolicy = value.FoldPolicy.ToString().ToLowerInvariant(), reason = value.Reason };
    private static object StatusWire(MaintenanceSeriesStatus value) => new { targetId = value.TargetId.Value, seriesId = value.SeriesId,
        revision = value.Revision, definition = DefinitionWire(value.Definition), cancelledFromDate = value.CancelledFromDate,
        createdAtUtc = value.CreatedAtUtc, updatedAtUtc = value.UpdatedAtUtc, expansionVersion = value.ExpansionVersion,
        serverNowUtc = value.ServerNowUtc, occurrenceCount = value.OccurrenceCount };
    private static async Task<IResult> GetAsync(HttpContext context, Guid instanceId, Guid seriesId, IMaintenanceSeriesService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { var value = await service.GetAsync(roles.Resolve(context.User), new(instanceId), seriesId, cancellationToken).ConfigureAwait(false); return value is null ? Results.NotFound() : Results.Ok(StatusWire(value)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> ListAsync(HttpContext context, Guid instanceId, IMaintenanceSeriesService service, WindowsGroupRoleResolver roles, int limit = 50, string? beforeSeriesId = null, CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { var page = await service.ListAsync(roles.Resolve(context.User), new(instanceId), limit, beforeSeriesId is null ? null : Id(beforeSeriesId), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { targetId = page.TargetId.Value, serverNowUtc = page.ServerNowUtc, items = page.Items.Select(StatusWire), nextBeforeSeriesId = page.NextBeforeSeriesId }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> OccurrencesAsync(HttpContext context, Guid instanceId, Guid seriesId, IMaintenanceSeriesService service, WindowsGroupRoleResolver roles, int limit = 50, string? beforeWindowId = null, CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { var page = await service.ListOccurrencesAsync(roles.Resolve(context.User), new(instanceId), seriesId, limit, beforeWindowId is null ? null : Id(beforeWindowId), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { targetId = page.TargetId.Value, seriesId = page.SeriesId, serverNowUtc = page.ServerNowUtc, items = page.Items, nextBeforeWindowId = page.NextBeforeWindowId }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static IResult Preview(HttpContext context, Guid instanceId, PreviewBody body, IMaintenanceSeriesService service, WindowsGroupRoleResolver roles)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { var value = service.Preview(roles.Resolve(context.User), new(instanceId), Id(body.SeriesId), Definition(body.Definition), body.EffectiveFromDate is null ? null : Date(body.EffectiveFromDate));
            return Results.Ok(new { targetId = value.TargetId.Value, seriesId = value.SeriesId, serverNowUtc = value.ServerNowUtc,
                definitionDigest = value.DefinitionDigest, expansionVersion = value.ExpansionVersion, occurrences = value.Occurrences.Select(static row => new
                { windowId = row.WindowId, localDate = row.LocalDate, startsAtUtc = row.StartsAtUtc, endsAtUtc = row.EndsAtUtc, utcOffsetMinutes = row.UtcOffsetMinutes, ambiguous = row.Ambiguous }), skippedDates = value.SkippedDates }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static Task<IResult> CreateAsync(HttpContext context, Guid instanceId, CreateBody body, IMaintenanceSeriesService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) =>
        WriteAsync(context, instanceId, () => (Id(body.SeriesId), null, null, Definition(body.Definition), body.Definition.Reason, body.OperationId, AdministrativeAuditAction.CreateMaintenanceSeries), service, roles, cancellationToken);
    private static Task<IResult> UpdateAsync(HttpContext context, Guid instanceId, Guid seriesId, UpdateBody body, IMaintenanceSeriesService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) =>
        WriteAsync(context, instanceId, () => (seriesId, body.ExpectedRevision, Date(body.EffectiveFromDate), Definition(body.Definition), body.Definition.Reason, body.OperationId, AdministrativeAuditAction.UpdateMaintenanceSeries), service, roles, cancellationToken);
    private static Task<IResult> CancelAsync(HttpContext context, Guid instanceId, Guid seriesId, CancelBody body, IMaintenanceSeriesService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) =>
        WriteAsync(context, instanceId, () => (seriesId, body.ExpectedRevision, Date(body.EffectiveFromDate), null, body.Reason, body.OperationId, AdministrativeAuditAction.CancelMaintenanceSeries), service, roles, cancellationToken);
    private static async Task<IResult> WriteAsync(HttpContext context, Guid instanceId,
        Func<(Guid SeriesId, long? Revision, DateOnly? Effective, MaintenanceSeriesDefinition? Definition, string Reason, string Operation, AdministrativeAuditAction Action)> payload,
        IMaintenanceSeriesService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var value = payload(); Guid operation = Id(value.Operation); var authorization = roles.Resolve(context.User);
            var request = new MaintenanceSeriesWriteRequest(value.SeriesId, value.Revision, value.Effective, value.Definition,
                value.Reason, value.Operation, new(authorization.ActorSid, new(Guid.NewGuid()), value.Action, new(instanceId)), new(TimeSpan.FromSeconds(5)));
            var receipt = await service.WriteAsync(authorization, request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { operationId = operation, auditId = receipt.Audit.AuditId.Value, recordedAtUtc = receipt.Audit.RecordedAtUtc,
                seriesId = receipt.SeriesId, revision = receipt.Revision, appliedAtUtc = receipt.AppliedAtUtc,
                createdOccurrences = receipt.CreatedOccurrences, updatedOccurrences = receipt.UpdatedOccurrences, cancelledOccurrences = receipt.CancelledOccurrences, preservedExceptions = receipt.PreservedExceptions });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }
}
