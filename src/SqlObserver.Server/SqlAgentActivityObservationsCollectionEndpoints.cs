using System.Globalization;
using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class SqlAgentActivityObservationsCollectionEndpoints
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    public static IEndpointRouteBuilder MapSqlAgentActivityObservationsCollectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        const string route = "/api/v1/observation-targets/{instanceId:guid}/sql-agent/activity-observations/collection";
        endpoints.MapGet(route, GetAsync).RequireAuthorization();
        endpoints.MapPut(route, SetAsync).RequireAuthorization()
            .RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid instanceId,
        ISqlAgentActivityObservationsCollectionAdministrationService service, WindowsGroupRoleResolver roles)
    {
        Begin(context);
        try
        {
            Route(context, instanceId);
            if (context.Request.Query.Count != 1 || !context.Request.Query.TryGetValue("targetRevision", out var values) || values.Count != 1)
                throw new ArgumentException("One target revision is required.");
            string text = values.ToString();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision.ToString(CultureInfo.InvariantCulture) != text)
                throw new ArgumentException("A canonical revision is required.");
            SqlAgentActivityObservationsCollectionValidation.Revision(revision);
            var state = await service.GetAsync(roles.Resolve(context.User), new MonitoredInstanceId(instanceId), revision, Timeout, context.RequestAborted).ConfigureAwait(false);
            if (state is null) return Failure(context, "target_not_found", "The observation target was not found.", 404);
            if (state.TargetRevision != revision) return Conflict(context);
            if (state.ScheduleRevision is < 1 or > SqlAgentActivityObservationsCollectionValidation.MaximumRevision) throw new InvalidDataException("Invalid schedule state.");
            return Results.Ok(new { targetId = instanceId, targetRevision = state.TargetRevision, scheduleRevision = state.ScheduleRevision, collectorEnabled = state.Enabled });
        }
        catch (UnauthorizedAccessException) { return Forbidden(context); }
        catch (SqlAgentActivityObservationsCollectionException error) { return Failure(context, error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Invalid(context); }
        catch (InvalidDataException) { return Failure(context, "request_failed", "The collection state could not be verified.", 500); }
    }

    private static async Task<IResult> SetAsync(HttpContext context, Guid instanceId,
        SqlAgentActivityObservationsCollectionBody body, ISqlAgentActivityObservationsCollectionAdministrationService service, WindowsGroupRoleResolver roles)
    {
        Begin(context);
        try
        {
            Route(context, instanceId);
            if (context.Request.Query.Count != 0 || body.ExpectedEnabled is null || body.Enabled is null || body.ExpectedScheduleRevision < 1)
                throw new ArgumentException("The complete expected state is required.");
            SqlAgentActivityObservationsCollectionValidation.Revision(body.ExpectedTargetRevision);
            var result = await service.SetAsync(roles.Resolve(context.User), new MonitoredInstanceId(instanceId),
                body.ExpectedTargetRevision, body.ExpectedScheduleRevision, body.ExpectedEnabled.Value, body.Enabled.Value,
                new AuditCorrelationId(Guid.Parse(context.Response.Headers["X-Correlation-ID"].ToString())), Timeout, context.RequestAborted).ConfigureAwait(false);
            if (result.Status == SqlAgentActivityObservationsCollectionMutationStatus.NotFound)
                return Failure(context, "target_not_found", "The observation target was not found.", 404);
            if (result.Status == SqlAgentActivityObservationsCollectionMutationStatus.EnrollmentUnavailable)
                return Failure(context, "agent_activity_enrollment_unavailable", "Active module enrollment is required. Read enrollment and collection state before enabling observations.", 409);
            if (result.Status != SqlAgentActivityObservationsCollectionMutationStatus.Applied) return Conflict(context);
            long expectedRevision = body.ExpectedEnabled == body.Enabled ? body.ExpectedScheduleRevision : checked(body.ExpectedScheduleRevision + 1);
            if (result.ScheduleRevision != expectedRevision || result.Enabled != body.Enabled)
                throw new InvalidDataException("The collection mutation receipt is invalid.");
            return Results.Ok(new { targetId = instanceId, targetRevision = body.ExpectedTargetRevision,
                scheduleRevision = result.ScheduleRevision, collectorEnabled = result.Enabled });
        }
        catch (UnauthorizedAccessException) { return Forbidden(context); }
        catch (SqlAgentActivityObservationsCollectionException error) { return Failure(context, error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Invalid(context); }
        catch (OverflowException) { return Invalid(context); }
        catch (InvalidDataException) { return Failure(context, "request_failed", "The collection change could not be verified. Read current state before another change.", 500); }
    }

    private static void Route(HttpContext context, Guid id)
    {
        if (id == Guid.Empty || context.Request.RouteValues["instanceId"] as string != id.ToString("D"))
            throw new ArgumentException("A canonical target ID is required.");
    }
    private static void Begin(HttpContext context)
    { context.Response.Headers.CacheControl = "private, no-store"; _ = ApiCorrelation.Begin(context); }
    private static IResult Forbidden(HttpContext context) => Failure(context, "forbidden", "A same-target role is required; collection changes require Target Administrator.", 403);
    private static IResult Invalid(HttpContext context) => Failure(context, "invalid_request", "The target or expected collection state is invalid.", 400);
    private static IResult Conflict(HttpContext context) => Failure(context, "agent_activity_collection_conflict", "Server or collection state changed. Read current state before another change.", 409);
    private static IResult Failure(HttpContext context, string code, string message, int status) => Results.Json(
        new SqlObserverProblemResponse(code, message, context.Response.Headers["X-Correlation-ID"].ToString()), statusCode: status);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SqlAgentActivityObservationsCollectionBody([property: JsonRequired] long ExpectedTargetRevision, [property: JsonRequired] long ExpectedScheduleRevision, [property: JsonRequired] bool? ExpectedEnabled, [property: JsonRequired] bool? Enabled);
