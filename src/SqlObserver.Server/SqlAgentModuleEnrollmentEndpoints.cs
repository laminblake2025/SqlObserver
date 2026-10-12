using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class SqlAgentModuleEnrollmentEndpoints
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    public static IEndpointRouteBuilder MapSqlAgentModuleEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        const string route = "/api/v1/observation-targets/{instanceId:guid}/sql-agent/module-enrollment";
        endpoints.MapGet(route, ReadAsync).RequireAuthorization();
        endpoints.MapPut(route, SetAsync).RequireAuthorization()
            .RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId,
        SqlAgentModuleEnrollmentAdministrationService service, WindowsGroupRoleResolver roles)
    {
        Begin(context);
        try
        {
            RequireRoute(context, instanceId);
            if (context.Request.Query.Count != 1 || !context.Request.Query.TryGetValue("targetRevision", out var values) || values.Count != 1)
                return Invalid(context);
            string text = values.ToString();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ||
                revision.ToString(CultureInfo.InvariantCulture) != text) return Invalid(context);
            RequireRevision(revision);
            var result = await service.ReadAsync(roles.Resolve(context.User), new(instanceId), new(revision), context.RequestAborted).ConfigureAwait(false);
            return result.Status switch
            {
                SqlAgentModuleEnrollmentReadStatus.Found => Results.Ok(new
                {
                    targetId = instanceId, targetRevision = revision, enrollmentRevision = result.Snapshot!.Revision,
                    enrollment = ToBody(result.Snapshot.Enrollment),
                }),
                SqlAgentModuleEnrollmentReadStatus.NotFound => NotFound(context),
                SqlAgentModuleEnrollmentReadStatus.Inactive or SqlAgentModuleEnrollmentReadStatus.RevisionConflict => Conflict(context),
                _ => throw new InvalidDataException("Unknown module enrollment status."),
            };
        }
        catch (UnauthorizedAccessException) { return Forbidden(context); }
        catch (ArgumentException) { return Invalid(context); }
        catch (Exception error) when (error is TimeoutException or NpgsqlException) { return Unavailable(context); }
        catch (InvalidDataException) { return Unverified(context); }
    }

    private static async Task<IResult> SetAsync(HttpContext context, Guid instanceId, SqlAgentModuleEnrollmentBody body,
        SqlAgentModuleEnrollmentAdministrationService service, WindowsGroupRoleResolver roles)
    {
        var correlation = Begin(context);
        try
        {
            RequireRoute(context, instanceId);
            RequireRevision(body.ExpectedTargetRevision);
            if (context.Request.Query.Count != 0 || body.Enrolled is null || body.Enrolled.Value != (body.Enrollment is not null) ||
                body.ExpectedEnrollmentRevision is < 0 or >= SqlAgentModuleEnrollment.MaximumRevision) return Invalid(context);
            var target = new MonitoredInstanceId(instanceId);
            var revision = new ObservationTargetRevision(body.ExpectedTargetRevision);
            var item = body.Enrollment;
            if (item is not null && item.Binding is null) return Invalid(context);
            var enrollment = item is null ? null : new SqlAgentModuleEnrollment(target, revision,
                body.ExpectedEnrollmentRevision + 1, item.EnrollmentId, item.ExpectedWindowsSid, item.Binding.ToDomain(),
                item.NotBeforeUtc, item.ExpiresAtUtc, item.EvidenceSha256);
            var change = new SqlAgentModuleEnrollmentChange(target, revision, body.ExpectedEnrollmentRevision, body.OperationId, enrollment);
            var result = await service.SetAsync(roles.Resolve(context.User), change, correlation, Timeout, context.RequestAborted).ConfigureAwait(false);
            return result.Status switch
            {
                SqlAgentModuleEnrollmentMutationStatus.Applied or SqlAgentModuleEnrollmentMutationStatus.Replayed => Results.Ok(new
                { targetId = instanceId, targetRevision = revision.Value, enrollmentRevision = result.Revision, enrolled = enrollment is not null }),
                SqlAgentModuleEnrollmentMutationStatus.NotFound => NotFound(context),
                SqlAgentModuleEnrollmentMutationStatus.Inactive or SqlAgentModuleEnrollmentMutationStatus.RevisionConflict => Conflict(context),
                _ => throw new InvalidDataException("Unknown module enrollment status."),
            };
        }
        catch (UnauthorizedAccessException) { return Forbidden(context); }
        catch (ArgumentException) { return Invalid(context); }
        catch (Exception error) when (error is TimeoutException or NpgsqlException) { return Unavailable(context); }
        catch (InvalidDataException) { return Unverified(context); }
    }

    private static SqlAgentModuleEnrollmentValue? ToBody(SqlAgentModuleEnrollment? item) => item is null ? null : new(
        item.EnrollmentId, item.ExpectedWindowsSid, SqlAgentModuleBindingBody.FromDomain(item.Binding),
        item.NotBeforeUtc, item.ExpiresAtUtc, item.EvidenceSha256);
    private static void RequireRevision(long revision)
    {
        if (revision is < 1 or > SqlAgentModuleEnrollment.MaximumRevision) throw new ArgumentOutOfRangeException(nameof(revision));
    }
    private static void RequireRoute(HttpContext context, Guid id)
    {
        if (id == Guid.Empty || context.Request.RouteValues["instanceId"] as string != id.ToString("D"))
            throw new ArgumentException("A canonical target ID is required.");
    }
    private static SqlObserver.Domain.Auditing.AuditCorrelationId Begin(HttpContext context)
    { context.Response.Headers.CacheControl = "private, no-store"; return ApiCorrelation.Begin(context); }
    private static IResult Forbidden(HttpContext context) => Failure(context, "forbidden", "Target Administrator on this target is required.", 403);
    private static IResult Invalid(HttpContext context) => Failure(context, "invalid_request", "The target or module enrollment is invalid.", 400);
    private static IResult NotFound(HttpContext context) => Failure(context, "target_not_found", "The observation target was not found.", 404);
    private static IResult Conflict(HttpContext context) => Failure(context, "module_enrollment_conflict", "Target or enrollment changed. Read current state before another change.", 409);
    private static IResult Unavailable(HttpContext context) => Failure(context, "module_enrollment_unavailable", "Enrollment storage is temporarily unavailable. Read current state before retrying a change.", 503);
    private static IResult Unverified(HttpContext context) => Failure(context, "request_failed", "Enrollment state could not be verified. Read current state before another change.", 500);
    private static IResult Failure(HttpContext context, string code, string message, int status) => Results.Json(
        new SqlObserverProblemResponse(code, message, context.Response.Headers["X-Correlation-ID"].ToString()), statusCode: status);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SqlAgentModuleEnrollmentBody([property: JsonRequired] long ExpectedTargetRevision,
    [property: JsonRequired] long ExpectedEnrollmentRevision, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] bool? Enrolled, SqlAgentModuleEnrollmentValue? Enrollment);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SqlAgentModuleEnrollmentValue([property: JsonRequired] Guid EnrollmentId,
    [property: JsonRequired] string ExpectedWindowsSid, [property: JsonRequired] SqlAgentModuleBindingBody Binding,
    [property: JsonRequired] DateTimeOffset NotBeforeUtc, [property: JsonRequired] DateTimeOffset ExpiresAtUtc,
    [property: JsonRequired] string EvidenceSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SqlAgentModuleBindingBody([property: JsonRequired] string EndpointHost,
    [property: JsonRequired] int TcpPort, [property: JsonRequired] string CertificateHost,
    [property: JsonRequired] string ServerName, [property: JsonRequired] string MachineName,
    [property: JsonRequired] string InstanceName, [property: JsonRequired] int ObjectId,
    [property: JsonRequired] int CertificateId, [property: JsonRequired] string DefinitionSha256,
    [property: JsonRequired] string PublicCertificateSha256, [property: JsonRequired] string ThumbprintHex)
{
    internal SqlAgentModuleBinding ToDomain() => new(EndpointHost, TcpPort, CertificateHost, ServerName,
        MachineName, InstanceName, ObjectId, CertificateId, DefinitionSha256, PublicCertificateSha256, ThumbprintHex);
    internal static SqlAgentModuleBindingBody FromDomain(SqlAgentModuleBinding item) => new(item.EndpointHost,
        item.TcpPort, item.CertificateHost, item.ServerName, item.MachineName, item.InstanceName, item.ObjectId,
        item.CertificateId, item.DefinitionSha256, item.PublicCertificateSha256, item.ThumbprintHex);
}
