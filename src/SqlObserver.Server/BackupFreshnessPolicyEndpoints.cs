using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class BackupFreshnessPolicyEndpoints
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    public static IEndpointRouteBuilder MapBackupFreshnessPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/health/backups/databases/{databaseFingerprint}/policy")
            .RequireAuthorization();
        group.MapGet("/", ReadAsync);
        group.MapPut("/", SetAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId, string databaseFingerprint,
        long targetRevision, BackupFreshnessPolicyService service, WindowsGroupRoleResolver roles, TimeProvider clock,
        DateTimeOffset? atUtc = null, CancellationToken cancellationToken = default)
    {
        ApiCorrelation.Begin(context);
        try
        {
            DateTimeOffset now = clock.GetUtcNow(); now = now.AddTicks(-now.Ticks % TimeSpan.TicksPerMicrosecond);
            DateTimeOffset at = atUtc ?? now;
            if (at > now) return Results.BadRequest();
            BackupFreshnessPolicy? policy = await service.ReadAsync(roles.Resolve(context.User),
                new(new(instanceId), new(targetRevision), databaseFingerprint, at, Timeout), cancellationToken).ConfigureAwait(false);
            return policy is null ? Results.NotFound() : Results.Ok(Map(policy));
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> SetAsync(HttpContext context, Guid instanceId, string databaseFingerprint,
        BackupFreshnessPolicyBody body, BackupFreshnessPolicyService service, WindowsGroupRoleResolver roles,
        CancellationToken cancellationToken)
    {
        var correlation = ApiCorrelation.Begin(context);
        try
        {
            if (body.ExpectedPolicyRevision < 0 || body.ExpectedPolicyRevision == long.MaxValue ||
                body.Enabled is null || body.MaximumFullAgeSeconds is null || body.MaximumObservationAgeSeconds is null ||
                body.RequireChecksums is null || body.OperationId == Guid.Empty) return Results.BadRequest();
            var requirement = body.FullRequirement switch
            {
                "full_image" => FullBackupRequirement.FullImage,
                "differential_base" => FullBackupRequirement.DifferentialBase,
                _ => throw new ArgumentException("An explicit full requirement is required."),
            };
            var policy = new BackupFreshnessPolicy(new(instanceId), new(body.ExpectedTargetRevision), databaseFingerprint,
                body.ExpectedPolicyRevision + 1, body.Enabled.Value, TimeSpan.FromSeconds(body.MaximumFullAgeSeconds.Value),
                body.MaximumLogAgeSeconds is { } log ? TimeSpan.FromSeconds(log) : null,
                TimeSpan.FromSeconds(body.MaximumObservationAgeSeconds.Value), requirement, body.RequireChecksums.Value);
            var authorization = roles.Resolve(context.User);
            var audit = new AdministrativeAuditEnvelope(authorization.ActorSid, correlation,
                AdministrativeAuditAction.UpdateObservationTarget, policy.TargetId);
            var result = await service.SetAsync(authorization,
                new(policy, body.ExpectedPolicyRevision, body.OperationId, audit, Timeout), cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                BackupPolicyMutationStatus.Applied or BackupPolicyMutationStatus.Replayed => Results.Ok(Map(policy)),
                BackupPolicyMutationStatus.NotFound or BackupPolicyMutationStatus.DatabaseNotObserved => Results.NotFound(),
                BackupPolicyMutationStatus.LogThresholdRequired => Results.BadRequest(new { code = "log_threshold_required",
                    message = "Full and Bulk-logged databases require an explicit log backup threshold." }),
                BackupPolicyMutationStatus.EvidenceUnavailable => Results.Conflict(new { code = "backup_inventory_unavailable",
                    message = "Collect recent complete database identity and recovery-model evidence before enabling this policy." }),
                BackupPolicyMutationStatus.Inactive or BackupPolicyMutationStatus.RevisionConflict => Results.Conflict(new
                { code = "backup_policy_conflict", message = "Target or policy changed. Reload before trying again." }),
                _ => throw new InvalidDataException("Unknown backup policy mutation status."),
            };
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static object Map(BackupFreshnessPolicy policy) => new
    {
        targetId = policy.TargetId.Value, targetRevision = policy.TargetRevision.Value,
        databaseFingerprint = policy.DatabaseFingerprint, policyRevision = policy.PolicyRevision, enabled = policy.Enabled,
        maximumFullAgeSeconds = (int)policy.MaximumFullAge.TotalSeconds,
        maximumLogAgeSeconds = policy.MaximumLogAge is { } log ? (int?)log.TotalSeconds : null,
        maximumObservationAgeSeconds = (int)policy.MaximumObservationAge.TotalSeconds,
        fullRequirement = policy.FullRequirement == FullBackupRequirement.FullImage ? "full_image" : "differential_base",
        requireChecksums = policy.RequireChecksums,
    };
}

public sealed record BackupFreshnessPolicyBody(long ExpectedTargetRevision, long ExpectedPolicyRevision,
    Guid OperationId, bool? Enabled, int? MaximumFullAgeSeconds, int? MaximumLogAgeSeconds,
    int? MaximumObservationAgeSeconds, string? FullRequirement, bool? RequireChecksums);
