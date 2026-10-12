using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

internal static class PlanCacheContentApiEndpoints
{
    internal static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId, int databaseId,
        string queryFingerprint, string planFingerprint, Guid collectionRunId, string cacheEntryFingerprint,
        string payloadKind, IPlanCacheContentReadService service, WindowsGroupRoleResolver roles,
        string? databaseIncarnation = null, CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var kind = payloadKind switch
            {
                "text" => SensitivePayloadKind.QueryText,
                "plan" => SensitivePayloadKind.ExecutionPlan,
                _ => throw new ArgumentException("Cache content kind must be text or plan."),
            };
            var request = new PlanCacheContentReadRequest(new MonitoredInstanceId(instanceId), collectionRunId, databaseId,
                databaseIncarnation ?? "", queryFingerprint, planFingerprint, cacheEntryFingerprint, kind,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
            var result = await service.ReadAsync(roles.Resolve(context.User), request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                targetId = instanceId,
                databaseId,
                databaseIncarnation,
                queryFingerprint,
                planFingerprint,
                collectionRunId,
                cacheEntryFingerprint,
                payloadKind,
                result.Status,
                result.Reason,
                result.ObservedAtUtc,
                text = kind == SensitivePayloadKind.QueryText ? result.Content : null,
                xml = kind == SensitivePayloadKind.ExecutionPlan ? result.Content : null,
            });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
}
