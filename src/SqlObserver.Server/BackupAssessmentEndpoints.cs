using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Backups;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class BackupAssessmentEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new CanonicalUtcDateTimeOffsetConverter(), new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private static readonly string[] UtcFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"];
    public static IEndpointRouteBuilder MapBackupAssessmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v2/observation-targets/{instanceId:guid}/health/backups", ReadAsync).RequireAuthorization();
        return endpoints;
    }
    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId, BackupAssessmentQueryService service,
        WindowsGroupRoleResolver roles, BackupEvidenceCursorCodec codec, TimeProvider clock,
        string? limit = null, string? atUtc = null, string? targetRevision = null, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ApiCorrelation.Begin(context); context.Response.Headers.CacheControl = "no-store";
        BackupEvidenceRequest request;
        try
        {
            BackupEvidenceCursor? continuation = cursor is null ? null : codec.Decode(cursor);
            DateTimeOffset now = clock.GetUtcNow(); now = new(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
            DateTimeOffset at = atUtc is null ? continuation?.AtUtc ?? now : ParseAt(atUtc);
            if (at > now) throw new ArgumentException("Backup cutoff cannot be in the future.", nameof(atUtc));
            int count = limit is null ? 50 : int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                ? parsed : throw new ArgumentException("Backup limit is invalid.", nameof(limit));
            long? revision = targetRevision is null ? null : long.TryParse(targetRevision, NumberStyles.None, CultureInfo.InvariantCulture, out long r)
                ? r : throw new ArgumentException("Target revision is invalid.", nameof(targetRevision));
            request = new(new(instanceId), at, count, new(TimeSpan.FromSeconds(5)), revision, continuation);
        }
        catch (ArgumentException exception) { return Problem(context, 400, "invalid_request", exception.Message, exception.ParamName); }
        try
        {
            BackupAssessmentPage page = await service.ReadAsync(roles.Resolve(context.User), request, cancellationToken).ConfigureAwait(false);
            BackupEvidenceHeader h = page.Header;
            var response = new
            {
                contractVersion = 2, targetId = h.TargetId.Value, atUtc = h.AtUtc, h.TargetRevision, h.RunId,
                h.SourceObservedAtUtc, h.CompletedAtUtc, h.ReceivedAtUtc, h.SourceZoneId, h.HistoryLowerBoundLocal,
                h.Coverage, h.EvidenceStatus, h.SourceRows, h.OutputItems, h.RetainedItems, h.DatabaseCount, h.CollectorEnabled,
                action = h.EvidenceStatus switch
                {
                    BackupEvidenceStatus.NotCollected => "Enable and qualify backup v2 collection for this target revision.",
                    BackupEvidenceStatus.CollectionFailed => "Resolve the collector failure or permission gap and collect again.",
                    BackupEvidenceStatus.EvidenceNotRetained => "Choose a retained time window or collect new backup evidence.",
                    BackupEvidenceStatus.WithheldByLimit => "Resolve the collection limit; no partial inventory is published.",
                    BackupEvidenceStatus.NoData => "Check database visibility and collection permissions.",
                    _ => null,
                },
                items = page.Items.Select(Map).ToArray(), hasMore = page.NextCursor is not null,
                nextCursor = page.NextCursor is null ? null : codec.Encode(page.NextCursor),
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
            return bytes.Length <= 1_048_576 ? Results.Bytes(bytes, "application/json")
                : Problem(context, 502, "repository_contract_invalid", "Backup evidence exceeded its response bound.");
        }
        catch (BackupEvidenceCursorStaleException) { return Problem(context, 409, "cursor_stale", "Backup evidence changed or expired. Restart from the first page."); }
        catch (UnauthorizedAccessException) { return Problem(context, 403, "forbidden", "A read role scoped to this target is required."); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        { return Problem(context, 502, "repository_contract_invalid", "Backup evidence could not be verified."); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        { return Problem(context, 504, "request_timed_out", "Backup evidence exceeded its execution limit."); }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        { return Problem(context, 503, "repository_unavailable", "Backup evidence is temporarily unavailable."); }
        catch (Exception) { return Problem(context, 500, "request_failed", "Backup evidence could not be read."); }
    }
    private static object Map(BackupDatabaseAssessment item)
    {
        var e = item.Source.Evidence; var p = item.Source.Policy;
        return new
        {
            item.Source.DatabaseOrdinal, e.DatabaseFingerprint, e.FamilyFingerprint, e.RecoveryForkFingerprint,
            e.GroupFingerprint, e.AgDatabaseFingerprint, e.LocalReplicaFingerprint, e.RecoveryModel, e.CatalogDatabaseState,
            e.Scope, e.LocalReplicaRole, e.LocalReplicaPreferred, e.UnclassifiedBackupSeen, e.Coverage, item.PolicyRevision,
            policy = p is null ? null : new
            {
                p.Enabled, maximumFullAgeSeconds = (int)p.MaximumFullAge.TotalSeconds,
                maximumLogAgeSeconds = p.MaximumLogAge is { } log ? (int?)log.TotalSeconds : null,
                maximumObservationAgeSeconds = (int)p.MaximumObservationAge.TotalSeconds, p.FullRequirement, p.RequireChecksums,
            },
            full = Check(item.Full), log = Check(item.Log), item.ObservedIssues,
            samples = e.Samples.Select((s, i) => new
            {
                s.Freshness.Kind, s.Freshness.CopyOnly, recordedFinishUtc = s.Freshness.FinishUtc,
                s.Freshness.SourceLocalFinish, s.SourceLocalStart, s.RecordedStartOffsetMinutes, s.SizeBytes,
                s.Freshness.HasChecksum, s.Freshness.IsDamaged, s.Freshness.MetadataComplete,
                s.FirstRecoveryForkFingerprint, s.LastRecoveryForkFingerprint,
                timeState = s.TimeResolution.State, s.TimeResolution.QualificationRevision,
                qualificationTrusted = item.Source.QualificationTrusted[i],
            }).ToArray(),
        };
    }
    private static object Check(BackupFreshnessCheck check) => new
    {
        check.Status, check.Reason, maximumAgeSeconds = check.MaximumAge?.TotalSeconds,
        observedAgeSeconds = check.ObservedAge?.TotalSeconds, check.SelectedBackup,
        action = check.Reason switch
        {
            BackupFreshnessReason.PolicyNotConfigured => "Configure explicit full and log backup thresholds for this database identity.",
            BackupFreshnessReason.DatabaseIdentityUnknown => "Resolve database identity visibility before configuring a policy.",
            BackupFreshnessReason.TimestampQualificationUnavailable or BackupFreshnessReason.FinishTimeUnknown =>
                "Verify historical source-zone rules and collect new evidence with an active qualification.",
            BackupFreshnessReason.OtherReplicaEvidenceRequired => "Inspect backup evidence on every registered replica; local history is incomplete.",
            BackupFreshnessReason.StaleObservation or BackupFreshnessReason.SourceIncomplete => "Restore complete current collection coverage.",
            BackupFreshnessReason.FutureSourceTime => "Verify the source clock and backup timestamp provenance.",
            BackupFreshnessReason.BackupNotObserved => "Inspect backup jobs and retained history; no eligible backup was observed.",
            _ => null,
        },
    };
    private static DateTimeOffset ParseAt(string atUtc) => atUtc.EndsWith('Z') && DateTimeOffset.TryParseExact(atUtc, UtcFormats,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset result)
        ? result : throw new ArgumentException("Backup cutoff must end in Z and contain at most six fractional digits.", nameof(atUtc));
    private static IResult Problem(HttpContext context, int status, string code, string detail, string? field = null) =>
        Results.Problem(statusCode: status, title: "Backup assessment request failed", detail: detail,
            extensions: new Dictionary<string, object?>
            { ["code"] = code, ["field"] = field, ["correlationId"] = context.Response.Headers["X-Correlation-ID"].ToString() });
}
