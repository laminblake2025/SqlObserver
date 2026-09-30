using System.Runtime.Serialization;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class BackupZoneQualificationEndpoints
{
    public static IEndpointRouteBuilder MapBackupZoneQualificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/health/backup-zone-qualification")
            .RequireAuthorization();
        group.MapGet("/", ReadAsync);
        group.MapPut("/", SetAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId, long targetRevision,
        BackupZoneQualificationAdministrationService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        ApiCorrelation.Begin(context);
        try
        {
            BackupZoneQualificationSnapshot? snapshot = await service.ReadAsync(roles.Resolve(context.User),
                new(instanceId), new(targetRevision), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                targetId = instanceId, targetRevision, qualificationRevision = snapshot?.Revision ?? 0,
                enabled = snapshot?.Qualification is not null,
                sourceZoneId = snapshot?.Qualification?.SourceZoneId,
                fromUtc = snapshot?.Qualification?.FromUtc, toUtc = snapshot?.Qualification?.ToUtc,
                evidenceSha256 = snapshot?.EvidenceSha256,
                rulesSha256 = snapshot?.RulesSha256,
            });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> SetAsync(HttpContext context, Guid instanceId, BackupZoneQualificationBody body,
        BackupZoneQualificationAdministrationService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        var correlation = ApiCorrelation.Begin(context);
        try
        {
            if (body.Enabled is null || body.ExpectedQualificationRevision < 0 || body.ExpectedQualificationRevision == long.MaxValue)
                return Results.BadRequest();
            var target = new MonitoredInstanceId(instanceId);
            var revision = new ObservationTargetRevision(body.ExpectedTargetRevision);
            BackupHistoricalZoneQualification? qualification = null;
            if (body.Enabled.Value)
            {
                if (body.HistoricalConfigurationConfirmed != true || body.SerializedZoneRules is not { } serialized ||
                    serialized.Contains('\0') || Encoding.UTF8.GetByteCount(serialized) > 65536 ||
                    body.SourceZoneId is null || body.FromUtc is null || body.ToUtc is null)
                    return Results.BadRequest();
                TimeZoneInfo zone;
                try { zone = TimeZoneInfo.FromSerializedString(serialized); }
                catch (Exception exception) when (exception is SerializationException or InvalidTimeZoneException or ArgumentException)
                { return Results.BadRequest(); }
                qualification = new(target, revision, body.ExpectedQualificationRevision + 1, zone,
                    body.SourceZoneId, body.FromUtc.Value, body.ToUtc.Value);
            }
            else if (body.SerializedZoneRules is not null || body.SourceZoneId is not null || body.FromUtc is not null ||
                body.ToUtc is not null || body.EvidenceSha256 is not null || body.HistoricalConfigurationConfirmed == true)
                return Results.BadRequest();
            var change = new BackupZoneQualificationChange(target, revision, body.ExpectedQualificationRevision,
                body.OperationId, qualification, body.EvidenceSha256);
            BackupZoneQualificationMutationResult result = await service.SetAsync(roles.Resolve(context.User), change,
                correlation, new(TimeSpan.FromSeconds(5)), cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                BackupZoneQualificationMutationStatus.Applied or BackupZoneQualificationMutationStatus.Replayed =>
                    Results.Ok(new { targetId = instanceId, targetRevision = body.ExpectedTargetRevision,
                        qualificationRevision = result.Revision, enabled = body.Enabled.Value }),
                BackupZoneQualificationMutationStatus.NotFound => Results.NotFound(),
                BackupZoneQualificationMutationStatus.Inactive or BackupZoneQualificationMutationStatus.RevisionConflict =>
                    Results.Conflict(new { code = "backup_zone_qualification_conflict", message = "Target or qualification changed. Reload before trying again." }),
                _ => throw new InvalidDataException("Unknown backup qualification status."),
            };
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
}

public sealed record BackupZoneQualificationBody(long ExpectedTargetRevision, long ExpectedQualificationRevision,
    Guid OperationId, bool? Enabled, bool? HistoricalConfigurationConfirmed, string? SerializedZoneRules,
    string? SourceZoneId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? EvidenceSha256);
