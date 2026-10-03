using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class AlertAssignmentEndpoints
{
    public static IEndpointRouteBuilder MapAlertAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/alerts/{alertId:guid}/assignment").RequireAuthorization();
        group.MapGet("", GetAsync);
        group.MapGet("/history", HistoryAsync);
        group.MapPost("", WriteAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }
    public sealed record AssignmentBody(string OperationId, string ExpectedEpisodeId, long ExpectedRevision,
        [property: JsonRequired] string? AssigneeSid, string Reason);

    private static async Task<IResult> GetAsync(HttpContext context, Guid instanceId, Guid alertId,
        IAlertQueryService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty || alertId == Guid.Empty) return Results.BadRequest();
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            AlertAssignmentView? view = await service.GetAssignmentAsync(roles.Resolve(context.User),
                new MonitoredInstanceId(instanceId), alertId, cancellationToken).ConfigureAwait(false);
            if (view is null) return Results.NotFound();
            var value = view.Status;
            return Results.Ok(new { targetId = value.TargetId.Value, alertId = value.AlertId, episodeId = value.EpisodeId,
                revision = value.Revision, state = value.State.ToString().ToLowerInvariant(), serverNowUtc = value.ServerNowUtc,
                assigneeSid = value.AssigneeSid, assigneeName = value.AssigneeName, assignmentReason = value.AssignmentReason,
                assignedBy = value.AssignedBy, assignedAtUtc = value.AssignedAtUtc,
                eligibleAssignees = view.EligibleAssignees.Select(static candidate => new { sid = candidate.Sid, displayName = candidate.DisplayName }),
                canAssign = view.CanAssign });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> HistoryAsync(HttpContext context, Guid instanceId, Guid alertId,
        IAlertQueryService service, WindowsGroupRoleResolver roles, int limit = 50, string? beforeOperationId = null,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty || alertId == Guid.Empty || limit is < 1 or > 50) return Results.BadRequest();
        Guid? anchor = null;
        if (beforeOperationId is not null)
        {
            if (!SqlObserver.Domain.Alerting.AlertIdentifier.TryParseRfc4122(beforeOperationId, out Guid parsed)) return Results.BadRequest();
            anchor = parsed;
        }
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var page = await service.ListAssignmentHistoryAsync(roles.Resolve(context.User), new MonitoredInstanceId(instanceId),
                alertId, limit, anchor, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { targetId = instanceId, alertId, items = page.Items.Select(static item => new
                { operationId = item.OperationId, episodeId = item.EpisodeId, revision = item.Revision,
                    assigneeSid = item.AssigneeSid, assigneeName = item.AssigneeName,
                    previousAssigneeSid = item.PreviousAssigneeSid, previousAssigneeName = item.PreviousAssigneeName,
                    reason = item.Reason, actorSid = item.ActorSid, appliedAtUtc = item.AppliedAtUtc }),
                nextBeforeOperationId = page.NextBeforeOperationId });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> WriteAsync(HttpContext context, Guid instanceId, Guid alertId, AssignmentBody body,
        IAlertAdministrationService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty || alertId == Guid.Empty || body.ExpectedRevision <= 0 ||
            !SqlObserver.Domain.Alerting.AlertIdentifier.TryParseRfc4122(body.OperationId, out Guid operationId) ||
            !SqlObserver.Domain.Alerting.AlertIdentifier.TryParseRfc4122(body.ExpectedEpisodeId, out Guid episodeId)) return Results.BadRequest();
        try
        {
            var authorization = roles.Resolve(context.User);
            ActorSecurityIdentifier? assignee = body.AssigneeSid is null ? null : new ActorSecurityIdentifier(body.AssigneeSid);
            var audit = new AdministrativeAuditEnvelope(authorization.ActorSid, new AuditCorrelationId(Guid.NewGuid()),
                assignee is null ? AdministrativeAuditAction.UnassignAlert : AdministrativeAuditAction.AssignAlert,
                new MonitoredInstanceId(instanceId));
            var request = new AlertAssignmentWriteRequest(alertId, episodeId, body.ExpectedRevision, assignee,
                body.Reason, body.OperationId, audit, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
            var receipt = await service.SetAssignmentAsync(authorization, request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { operationId, auditId = receipt.Audit.AuditId.Value, recordedAtUtc = receipt.Audit.RecordedAtUtc,
                revision = receipt.Revision, assigneeSid = receipt.AssigneeSid, appliedAtUtc = receipt.AppliedAtUtc });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
}
