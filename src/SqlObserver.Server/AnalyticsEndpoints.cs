using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;
using SqlObserver.Security;

namespace SqlObserver.Server;

/// <summary>Bounded M10 analytics and retention HTTP surface.  It only depends on repository/application ports.</summary>
public static class AnalyticsEndpoints
{
    private const int MaxMetricPoints = 1000;
    private const int MaxPage = 200;
    private const int MaxMutationBytes = 16 * 1024;
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly JsonSerializerOptions MutationJsonOptions = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly HashSet<string> RetentionDataClasses = new(StringComparer.Ordinal)
    {
        "m5_activity", "m5_requests", "m5_waits", "m5_blocking",
        "m10_host_metrics", "m10_replication", "m10_rollups", "m10_evidence"
    };
    private static readonly HashSet<string> MutationReceiptStates = new(StringComparer.Ordinal)
    {
        "queued", "committed", "replayed", "accepted", "blocked", "detached", "grace", "dropped", "failed", "retry"
    };

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder targets = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/analytics").RequireAuthorization();
        targets.MapGet("/series", SeriesAsync);
        targets.MapGet("/rollups", RollupsAsync);
        targets.MapGet("/compare", CompareAsync);
        targets.MapGet("/baselines", BaselinesAsync);
        targets.MapGet("/forecasts", ForecastsAsync);
        targets.MapGet("/jobs", JobsAsync);
        targets.MapGet("/backfill", BackfillAsync);
        targets.MapGet("/host/status", HostStatusAsync);
        targets.MapGet("/host/metrics", HostMetricsAsync);
        targets.MapGet("/replication/status", ReplicationStatusAsync);
        targets.MapGet("/replication/evidence", ReplicationEvidenceAsync);
        targets.MapGet("/diagnostics/search", DiagnosticsAsync);
        targets.MapGet("/incidents", IncidentsAsync);
        targets.MapGet("/evidence-packets", EvidencePacketsAsync);
        targets.MapPost("/host-binding", BindHostAsync);
        targets.MapPost("/backfill", StartBackfillAsync);
        RouteGroupBuilder observationTargets = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}").RequireAuthorization();
        observationTargets.MapGet("/host/status", HostStatusAsync);
        observationTargets.MapGet("/host/metrics", HostMetricsAsync);
        observationTargets.MapGet("/replication/status", ReplicationStatusAsync);
        observationTargets.MapGet("/replication/evidence", ReplicationEvidenceAsync);
        observationTargets.MapGet("/diagnostics/search", DiagnosticsAsync);
        observationTargets.MapGet("/incidents", IncidentsAsync);
        observationTargets.MapGet("/evidence-packets", EvidencePacketsAsync);
        observationTargets.MapPost("/host-binding", BindHostAsync);
        observationTargets.MapPost("/backfill", StartBackfillAsync);
        RouteGroupBuilder retention = endpoints.MapGroup("/api/v1/retention").RequireAuthorization().RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        retention.MapGet("/policies/{dataClass}", GetPolicyAsync);
        retention.MapPut("/policies/{dataClass}", UpdatePolicyAsync);
        retention.MapGet("/preview", PreviewRetentionAsync);
        retention.MapPost("/attestations", RecordAttestationAsync);
        retention.MapPost("/detach", DetachRetentionAsync);
        retention.MapPost("/drop", DropRetentionAsync);
        return endpoints;
    }

    private static async Task<IResult> SeriesAsync(HttpContext context, Guid instanceId, IAnalyticsRepositoryPort repository, WindowsGroupRoleResolver resolver, string metricKey, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 1000, CancellationToken cancellationToken = default)
    {
        try
        {
            if (limit is < 1 or > MaxMetricPoints || string.IsNullOrWhiteSpace(metricKey) || metricKey.Length > 128) return Results.BadRequest();
            if (fromUtc.HasValue != toUtc.HasValue) return Results.BadRequest(); DateTimeOffset to = toUtc ?? DateTimeOffset.UtcNow; DateTimeOffset from = fromUtc ?? to.AddHours(-24); if (!ValidWindow(from, to, TimeSpan.FromDays(31))) return Results.BadRequest(); var target = new MonitoredInstanceId(instanceId); AuthorizationContext auth = resolver.Resolve(context.User); if (!CanReadTarget(auth, target)) return Results.Forbid();
            var rows = await repository.ReadMetricPointsAsync(new AnalyticsQueryRequest(target, metricKey, from, to, limit, RepositoryTimeout), cancellationToken).ConfigureAwait(false);
            return Bounded(new { targetId = instanceId, metricKey, fromUtc = from, toUtc = to, items = rows.Select(x => new { observedAtUtc = x.ObservedAtUtc, value = x.Value, dimensions = x.Dimensions }).ToArray(), state = rows.Count == 0 ? "no_data" : "complete" });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (InvalidOperationException) { return Results.Conflict(); }
        catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> RollupsAsync(HttpContext context, Guid instanceId, IAnalyticsQueryService service, WindowsGroupRoleResolver resolver, string metricKey, string? interval = "hour", DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (fromUtc.HasValue != toUtc.HasValue) return Results.BadRequest(); if (limit is < 1 or > MaxPage || !ValidCursor(cursor)) return Results.BadRequest(); RollupInterval bucket = interval switch { "5m" or "five_minutes" => RollupInterval.FiveMinutes, "hour" => RollupInterval.Hour, "day" => RollupInterval.Day, _ => throw new ArgumentException("Unsupported interval.") }; DateTimeOffset to = toUtc ?? DateTimeOffset.UtcNow; DateTimeOffset from = fromUtc ?? to.AddDays(-7); if (!ValidWindow(from, to, TimeSpan.FromDays(31))) return Results.BadRequest(); var target = new MonitoredInstanceId(instanceId); AuthorizationContext auth = resolver.Resolve(context.User); if (!CanReadTarget(auth, target)) return Results.Forbid(); AnalyticsRollupPage page = await service.GetRollupPageAsync(auth, new AnalyticsQueryRequest(target, metricKey, from, to, limit, RepositoryTimeout), bucket, cursor, cancellationToken).ConfigureAwait(false); if (page.TargetRevision < 1 || page.Generation < 1 || page.SnapshotUtc.Offset != TimeSpan.Zero || page.SourceCutoffUtc.Offset != TimeSpan.Zero || page.HasMore != (page.NextCursor is not null) || page.NextCursor is not null && !ValidCursor(page.NextCursor)) return Results.Conflict(); return Bounded(new { targetId = instanceId, metricKey, interval, fromUtc = from, toUtc = to, items = page.Items, hasMore = page.HasMore, nextCursor = page.NextCursor, snapshotUtc = page.SnapshotUtc, sourceCutoffUtc = page.SourceCutoffUtc, targetRevision = page.TargetRevision, generation = page.Generation, state = RollupState(page.Items) });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> CompareAsync(HttpContext context, Guid instanceId, IAnalyticsQueryService service, WindowsGroupRoleResolver resolver, string metricKey, DateTimeOffset leftFromUtc, DateTimeOffset leftToUtc, DateTimeOffset rightFromUtc, DateTimeOffset rightToUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            TimeSpan leftDuration = leftToUtc - leftFromUtc, rightDuration = rightToUtc - rightFromUtc;
            bool overlap = leftFromUtc < rightToUtc && rightFromUtc < leftToUtc;
            if (string.IsNullOrWhiteSpace(metricKey) || metricKey.Length > 128 || leftFromUtc.Offset != TimeSpan.Zero || leftToUtc.Offset != TimeSpan.Zero || rightFromUtc.Offset != TimeSpan.Zero || rightToUtc.Offset != TimeSpan.Zero || leftDuration <= TimeSpan.Zero || rightDuration <= TimeSpan.Zero || leftDuration != rightDuration || leftDuration > TimeSpan.FromDays(31) || overlap) return Results.BadRequest();
            var target = new MonitoredInstanceId(instanceId); AuthorizationContext auth = resolver.Resolve(context.User); if (!CanReadTarget(auth, target)) return Results.Forbid(); DateTimeOffset from = leftFromUtc < rightFromUtc ? leftFromUtc : rightFromUtc; DateTimeOffset to = leftToUtc > rightToUtc ? leftToUtc : rightToUtc; var request = new AnalyticsQueryRequest(target, metricKey, from, to, MaxPage, RepositoryTimeout); WindowComparisonResult result = await service.CompareAsync(auth, request, leftFromUtc, leftToUtc, rightFromUtc, rightToUtc, cancellationToken).ConfigureAwait(false); return Bounded(new { targetId = instanceId, metricKey, leftFromUtc, leftToUtc, rightFromUtc, rightToUtc, result });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> BaselinesAsync(HttpContext context, Guid instanceId, IAnalyticsRepositoryPort repository, WindowsGroupRoleResolver resolver, string metricKey, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        try { if (fromUtc.HasValue != toUtc.HasValue) return Results.BadRequest(); if (limit is < 1 or > 200) return Results.BadRequest(); DateTimeOffset to = toUtc ?? DateTimeOffset.UtcNow, from = fromUtc ?? to.AddDays(-31); if (!ValidWindow(from, to, TimeSpan.FromDays(31))) return Results.BadRequest(); var target = new MonitoredInstanceId(instanceId); AuthorizationContext auth = resolver.Resolve(context.User); if (!CanReadTarget(auth, target)) return Results.Forbid(); var rows = await repository.ReadBaselinesAsync(new AnalyticsQueryRequest(target, metricKey, from, to, limit, RepositoryTimeout), cancellationToken).ConfigureAwait(false); return Bounded(new { targetId = instanceId, metricKey, fromUtc = from, toUtc = to, items = rows, state = rows.Count == 0 ? "no_data" : rows.Any(x => x.VisibilityState == "unsupported") ? "unsupported" : rows.Any(x => x.VisibilityState != "complete" || x.Confidence < .5) ? "partial" : "complete" }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
    private static async Task<IResult> ForecastsAsync(HttpContext context, Guid instanceId, IAnalyticsRepositoryPort repository, WindowsGroupRoleResolver resolver, string metricKey, string? dimensionsSha256 = null, string? volumeFingerprint = null, int horizonDays = 30, CancellationToken cancellationToken = default)
    {
        try { if (horizonDays is < 1 or > 366 || dimensionsSha256 is not null && (dimensionsSha256.Length != 64 || !dimensionsSha256.All(Uri.IsHexDigit)) || volumeFingerprint is not null && (volumeFingerprint.Length is 0 or > 256 || volumeFingerprint.Any(char.IsControl))) return Results.BadRequest(); var target = new MonitoredInstanceId(instanceId); AuthorizationContext auth = resolver.Resolve(context.User); if (!CanReadTarget(auth, target)) return Results.Forbid(); IReadOnlyDictionary<string, string>? dimensions = volumeFingerprint is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["volume"] = volumeFingerprint }; string? effectiveHash = dimensions is null ? dimensionsSha256 : CanonicalDimensions.Sha256(dimensions); var request = new AnalyticsQueryRequest(target, metricKey, DateTimeOffset.UtcNow.AddDays(-31), DateTimeOffset.UtcNow, 200, RepositoryTimeout, DimensionsSha256: effectiveHash, Dimensions: dimensions); var rows = await repository.ReadForecastsAsync(request, TimeSpan.FromDays(horizonDays), cancellationToken).ConfigureAwait(false); return Bounded(new { targetId = instanceId, metricKey, dimensionsSha256 = effectiveHash, horizonDays, items = rows, state = rows.Count == 0 ? "no_data" : rows.Any(x => x.VisibilityState == "unsupported") ? "unsupported" : rows.Any(x => x.VisibilityState != "complete" || x.Confidence < .5) ? "partial" : "complete" }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
    private static async Task<IResult> IncidentsAsync(HttpContext context, Guid instanceId, IAnalyticsRepositoryPort repository, WindowsGroupRoleResolver resolver, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        try { if (fromUtc.HasValue != toUtc.HasValue) return Results.BadRequest(); if (limit is < 1 or > 100) return Results.BadRequest(); DateTimeOffset to = toUtc ?? DateTimeOffset.UtcNow, from = fromUtc ?? to.AddDays(-31); if (!ValidWindow(from, to, TimeSpan.FromDays(31))) return Results.BadRequest(); var target = new MonitoredInstanceId(instanceId); AuthorizationContext auth = resolver.Resolve(context.User); if (!CanReadTarget(auth, target)) return Results.Forbid(); var rows = await repository.ReadIncidentsAsync(new AnalyticsQueryRequest(target, "host.cpu.percent", from, to, limit, RepositoryTimeout), cancellationToken).ConfigureAwait(false); return Bounded(new { targetId = instanceId, fromUtc = from, toUtc = to, items = rows, state = rows.Count == 0 ? "no_data" : "complete" }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
    private static Task<IResult> JobsAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 100, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "jobs", fromUtc, toUtc, limit, cursor, ct);
    private static Task<IResult> BackfillAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 100, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "backfill", fromUtc, toUtc, limit, cursor, ct);
    private static Task<IResult> HostStatusAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 200, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "host/status", fromUtc, toUtc, limit, cursor, ct);
    private static Task<IResult> HostMetricsAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 200, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "host/metrics", fromUtc, toUtc, limit, cursor, ct);
    private static Task<IResult> ReplicationStatusAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 200, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "replication/status", fromUtc, toUtc, limit, cursor, ct);
    private static Task<IResult> ReplicationEvidenceAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 200, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "replication/evidence", fromUtc, toUtc, limit, cursor, ct);
    private static Task<IResult> DiagnosticsAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 100, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "diagnostics/search", fromUtc, toUtc, limit, cursor, ct);
    private static Task<IResult> EvidencePacketsAsync(HttpContext c, Guid instanceId, IAnalyticsSurfaceRepositoryPort p, WindowsGroupRoleResolver r, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int limit = 200, string? cursor = null, CancellationToken ct = default) => ReadSurfaceAsync(c, instanceId, p, r, "evidence-packets", fromUtc, toUtc, limit, cursor, ct);
    private static async Task<IResult> ReadSurfaceAsync(HttpContext context, Guid instanceId, IAnalyticsSurfaceRepositoryPort repository, WindowsGroupRoleResolver resolver, string surface, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit, string? cursor, CancellationToken cancellationToken)
    {
        try { if (fromUtc.HasValue != toUtc.HasValue) return Results.BadRequest(); if (limit is < 1 or > MaxPage || !ValidCursor(cursor)) return Results.BadRequest(); DateTimeOffset to = toUtc ?? DateTimeOffset.UtcNow, from = fromUtc ?? to.AddDays(surface == "diagnostics/search" ? -7 : -1); if (!ValidWindow(from, to, TimeSpan.FromDays(7))) return Results.BadRequest(); var target = new MonitoredInstanceId(instanceId); if (!CanReadTarget(resolver.Resolve(context.User), target)) return Results.Forbid(); AnalyticsSurfacePage page = await repository.ReadSurfaceAsync(instanceId, surface, from, to, limit, cursor, cancellationToken).ConfigureAwait(false); if (page.TargetRevision < 1 || page.Generation < 1 || page.SnapshotUtc.Offset != TimeSpan.Zero || page.CutoffUtc.Offset != TimeSpan.Zero) return Results.Conflict(); if (page.NextCursor is not null && !ValidCursor(page.NextCursor)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); return Bounded(new { targetId = instanceId, surface, fromUtc = from, toUtc = to, items = page.Items, state = page.State, nextCursor = page.NextCursor, cutoffUtc = page.CutoffUtc, snapshotUtc = page.SnapshotUtc, generation = page.Generation, targetRevision = page.TargetRevision }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (KeyNotFoundException) { return Results.NotFound(); } catch (ArgumentException) { return Results.BadRequest(); } catch (InvalidOperationException) { return Results.Conflict(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
    private static async Task<IResult> BindHostAsync(HttpContext context, Guid instanceId, IAnalyticsSurfaceRepositoryPort repository, WindowsGroupRoleResolver resolver, IdentityFingerprintKey fingerprintKey, long expectedRevision = 0, Guid? operationId = null, CancellationToken cancellationToken = default)
    { try { AuthorizationContext auth = resolver.Resolve(context.User); var target = new MonitoredInstanceId(instanceId); if (!auth.CanAccess(ApplicationRole.TargetAdministrator, target)) return Results.Forbid(); using JsonDocument document = await ReadBoundedJsonAsync(context, cancellationToken).ConfigureAwait(false); HostBindingBody body = ParseHostBinding(document.RootElement, instanceId, expectedRevision, operationId, fingerprintKey); JsonElement profile = body.Payload.GetProperty("profile"); byte[] computed = MutationDigestV1.HostBinding(instanceId, body.HostId, body.HostName, body.IdentityFingerprint, body.BindingRevision, body.ProfileRevision, body.ExpectedRevision, profile, body.OperationId, auth.ActorSid.Value, MutationDigestV1.Scope(auth.GetScopeForRoles(new[] { ApplicationRole.TargetAdministrator })), body.CorrelationId, body.ChangeReason); VerifyDigest(body.RequestDigest, computed); AnalyticsMutationReceipt receipt = ValidateReceipt(await repository.BindHostAsync(new HostBindingMutationRequest(instanceId, body.Payload, body.ExpectedRevision, body.OperationId, computed, auth.ActorSid.Value, body.CorrelationId, body.ChangeReason), cancellationToken).ConfigureAwait(false)); context.Response.Headers["X-Correlation-ID"] = body.CorrelationId.ToString("D"); return Results.Ok(receipt); } catch (UnsupportedMutationMediaTypeException) { return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); } catch (InvalidOperationException) { return Results.Conflict(); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (JsonException) { return Results.BadRequest(); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); } }
    private static async Task<IResult> StartBackfillAsync(HttpContext context, Guid instanceId, IAnalyticsSurfaceRepositoryPort repository, WindowsGroupRoleResolver resolver, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, string? metricKey = null, long? expectedRevision = null, CancellationToken cancellationToken = default)
    { try { AuthorizationContext auth = resolver.Resolve(context.User); var target = new MonitoredInstanceId(instanceId); if (!auth.CanAccess(ApplicationRole.TargetAdministrator, target)) return Results.Forbid(); using JsonDocument document = await ReadBoundedJsonAsync(context, cancellationToken).ConfigureAwait(false); BackfillBody body = ParseBackfill(document.RootElement, instanceId, fromUtc, toUtc, metricKey, expectedRevision); byte[] computed = MutationDigestV1.Backfill(instanceId, body.FromUtc, body.ToUtc, body.MetricKey, body.ExpectedRevision, body.OperationId, auth.ActorSid.Value, MutationDigestV1.Scope(auth.GetScopeForRoles(new[] { ApplicationRole.TargetAdministrator })), body.CorrelationId, body.ChangeReason); VerifyDigest(body.RequestDigest, computed); AnalyticsMutationReceipt receipt = ValidateReceipt(await repository.StartBackfillAsync(new BackfillMutationRequest(instanceId, body.FromUtc, body.ToUtc, body.MetricKey, body.ExpectedRevision, body.OperationId, computed, auth.ActorSid.Value, body.CorrelationId, body.ChangeReason), cancellationToken).ConfigureAwait(false)); context.Response.Headers["X-Correlation-ID"] = body.CorrelationId.ToString("D"); return Results.Accepted($"/api/v1/observation-targets/{instanceId:D}/analytics/jobs", receipt); } catch (UnsupportedMutationMediaTypeException) { return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); } catch (InvalidOperationException) { return Results.Conflict(); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (JsonException) { return Results.BadRequest(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); } }
    private static async Task<IResult> RecordAttestationAsync(HttpContext context, IAnalyticsSurfaceRepositoryPort repository, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken = default)
    { try { AuthorizationContext auth = resolver.Resolve(context.User); if (!auth.HasRoleForAllTargets(ApplicationRole.SecurityAdministrator)) return Results.Forbid(); using JsonDocument document = await ReadBoundedJsonAsync(context, cancellationToken).ConfigureAwait(false); (JsonElement Payload, Guid OperationId, Guid CorrelationId, string ChangeReason, string? RequestDigest) = ParseAttestation(document.RootElement); JsonElement root = Payload; byte[] computed = MutationDigestV1.Attestation(OperationId, RequiredText(root, "digest", 64), RequiredText(root, "backupSetReference", 512), RequiredText(root, "attestedBy", 256), DateTimeOffset.Parse(root.GetProperty("expiresAtUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), auth.ActorSid.Value, "all", CorrelationId, ChangeReason); if (RequestDigest is not null) VerifyDigest(RequestDigest, computed); AnalyticsMutationReceipt receipt = ValidateReceipt(await repository.RecordAttestationAsync(new AttestationMutationRequest(Payload, OperationId, auth.ActorSid.Value, CorrelationId, ChangeReason, computed), cancellationToken).ConfigureAwait(false)); context.Response.Headers["X-Correlation-ID"] = CorrelationId.ToString("D"); return Results.Ok(receipt); } catch (UnsupportedMutationMediaTypeException) { return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); } catch (InvalidOperationException) { return Results.Conflict(); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (JsonException) { return Results.BadRequest(); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); } }
    private static async Task<JsonDocument> ReadBoundedJsonAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out MediaTypeHeaderValue? contentType)
            || !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedMutationMediaTypeException();
        }

        if (context.Request.ContentLength is > MaxMutationBytes)
            throw new ArgumentException("Mutation body is too large.");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxMutationBytes)
                throw new ArgumentException("Mutation body is too large.");
            buffer.Write(chunk, 0, read);
        }

        JsonDocument result = JsonDocument.Parse(buffer.ToArray());
        if (result.RootElement.ValueKind != JsonValueKind.Object)
        {
            result.Dispose();
            throw new ArgumentException("Mutation body must be a JSON object.");
        }
        return result;
    }

    private sealed class UnsupportedMutationMediaTypeException : Exception;

    private static bool ValidCursor(string? value) => value is null || value.Length is > 0 and <= 1024 && value.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or ':' or '|' or '/' or '+' or '=');

    private static AnalyticsMutationReceipt ValidateReceipt(AnalyticsMutationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.OperationId == Guid.Empty || !MutationReceiptStates.Contains(receipt.State) || receipt.Revision is <= 0 || receipt.AcceptedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Repository returned an invalid mutation receipt.");
        return receipt;
    }

    private static BackfillBody ParseBackfill(JsonElement root, Guid instanceId, DateTimeOffset? queryFrom, DateTimeOffset? queryTo, string? queryMetric, long? queryRevision)
    {
        EnsureKeys(root, ["targetId", "fromUtc", "toUtc", "metricKey", "expectedRevision", "operationId", "requestDigest", "correlationId", "changeReason"]);
        if (RequiredGuid(root, "targetId") != instanceId) throw new ArgumentException("Backfill target binding mismatch.");
        if (!root.TryGetProperty("fromUtc", out JsonElement fromValue) || !root.TryGetProperty("toUtc", out JsonElement toValue) || fromValue.ValueKind != JsonValueKind.String || toValue.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(fromValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset from) ||
            !DateTimeOffset.TryParse(toValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset to) ||
            !ValidWindow(from, to, TimeSpan.FromDays(90))) throw new ArgumentException("Backfill requires a bounded UTC window.");
        string? metric = root.TryGetProperty("metricKey", out JsonElement metricValue) && metricValue.ValueKind != JsonValueKind.Null ? metricValue.ValueKind == JsonValueKind.String ? metricValue.GetString() : throw new ArgumentException("Backfill metric is invalid.") : null;
        if (metric is not null && (metric.Length is 0 or > 128 || !metric.All(static c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))) throw new ArgumentException("Backfill metric is invalid.");
        long revision = RequiredPositiveInt64(root, "expectedRevision");
        Guid operationId = RequiredGuid(root, "operationId"); Guid correlationId = RequiredGuid(root, "correlationId");
        string digest = RequiredSha256(root, "requestDigest"); string reason = RequiredText(root, "changeReason", 512);
        if (queryFrom.HasValue && queryFrom.Value != from || queryTo.HasValue && queryTo.Value != to || queryMetric is not null && queryMetric != metric || queryRevision.HasValue && queryRevision.Value != revision) throw new ArgumentException("Backfill query and body disagree.");
        _ = digest; _ = reason;
        return new BackfillBody(from, to, metric, revision, operationId, correlationId, digest, reason);
    }

    private static HostBindingBody ParseHostBinding(JsonElement root, Guid instanceId, long queryRevision, Guid? queryOperationId, IdentityFingerprintKey fingerprintKey)
    {
        EnsureKeys(root, ["targetId", "hostId", "hostName", "identityFingerprint", "bindingRevision", "profileRevision", "expectedRevision", "operationId", "requestDigest", "correlationId", "changeReason", "profile"]);
        if (RequiredGuid(root, "targetId") != instanceId) throw new ArgumentException("Host binding target binding mismatch.");
        Guid hostId = RequiredGuid(root, "hostId"); string hostName = RequiredText(root, "hostName", 512); string fingerprint = RequiredSha256(root, "identityFingerprint");
        long expectedRevision = root.TryGetProperty("expectedRevision", out JsonElement revisionValue) ? PositiveInt64FromElement(revisionValue, "expectedRevision") : queryRevision;
        if (expectedRevision < 1 || queryRevision > 0 && expectedRevision != queryRevision) throw new ArgumentException("Host binding revision mismatch.");
        long bindingRevision = RequiredInt64(root.GetProperty("bindingRevision"), "bindingRevision");
        long profileRevision = PositiveInt64FromElement(root.GetProperty("profileRevision"), "profileRevision");
        if (bindingRevision < 0) throw new ArgumentException("Binding revision is invalid.");
        Guid operationId = RequiredGuid(root, "operationId");
        if (queryOperationId.HasValue && queryOperationId.Value != operationId) throw new ArgumentException("Host binding operation id mismatch.");
        Guid correlationId = RequiredGuid(root, "correlationId");
        string requestDigest = RequiredSha256(root, "requestDigest"); string changeReason = RequiredText(root, "changeReason", 512);
        if (!root.TryGetProperty("profile", out JsonElement profile) || profile.ValueKind != JsonValueKind.Object || !RequiredProfile(profile)) throw new ArgumentException("Host profile is incomplete.");
        if (SqlObserver.Domain.Hosts.HostIdentityFingerprint.Parse(fingerprint).ToStableHostId(fingerprintKey) != hostId) throw new ArgumentException("Host identity is not stable.");
        return new HostBindingBody(root.Clone(), hostId, hostName, fingerprint, bindingRevision, profileRevision, expectedRevision, operationId, correlationId, requestDigest, changeReason);
    }

    private static (JsonElement Payload, Guid OperationId, Guid CorrelationId, string ChangeReason, string? RequestDigest) ParseAttestation(JsonElement root)
    {
        EnsureKeys(root, ["attestationId", "attestedBy", "backupSetReference", "expiresAtUtc", "digest", "correlationId", "changeReason", "requestDigest"]);
        Guid id = RequiredGuid(root, "attestationId"); _ = RequiredText(root, "attestedBy", 256); _ = RequiredText(root, "backupSetReference", 512);
        if (!root.TryGetProperty("expiresAtUtc", out JsonElement expiry) || expiry.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset expires) || expires.Offset != TimeSpan.Zero || expires <= DateTimeOffset.UtcNow) throw new ArgumentException("Attestation expiry must be future UTC.");
        _ = RequiredSha256(root, "digest"); Guid correlation = root.TryGetProperty("correlationId", out JsonElement correlationValue) ? GuidFromElement(correlationValue, "correlationId") : id; string reason = RequiredText(root, "changeReason", 512);
        string? requestDigest = root.TryGetProperty("requestDigest", out JsonElement requestValue) && requestValue.ValueKind != JsonValueKind.Null ? RequiredSha256(root, "requestDigest") : null;
        return (root.Clone(), id, correlation, reason, requestDigest);
    }

    private static void EnsureKeys(JsonElement root, params string[] allowed)
    { if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal))) throw new ArgumentException("Mutation payload contains an unknown field."); }
    private static Guid RequiredGuid(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) ? GuidFromElement(value, name) : throw new ArgumentException($"{name} is required.");
    private static Guid GuidFromElement(JsonElement value, string name) => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid result) && result != Guid.Empty ? result : throw new ArgumentException($"{name} is invalid.");
    private static long RequiredInt64(JsonElement value, string name) => value.TryGetInt64(out long result) ? result : throw new ArgumentException($"{name} is invalid.");
    private static long RequiredPositiveInt64(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) ? PositiveInt64FromElement(value, name) : throw new ArgumentException($"{name} is required.");
    private static long PositiveInt64FromElement(JsonElement value, string name) { long result = RequiredInt64(value, name); return result > 0 ? result : throw new ArgumentException($"{name} is invalid."); }
    private static string RequiredText(JsonElement root, string name, int max) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? RequiredText(value.GetString(), name, max) : throw new ArgumentException($"{name} is required.");
    private static string RequiredText(string? value, string name, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl) ? value : throw new ArgumentException($"{name} is invalid.");
    private static string RequiredSha256(JsonElement root, string name) { string value = RequiredText(root, name, 64); return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : throw new ArgumentException($"{name} is invalid."); }
    private static bool RequiredProfile(JsonElement profile)
    {
        string[] allowed = ["osFamily", "osVersion", "cpuCount", "memoryBytes", "capabilityState", "capabilities"];
        if (profile.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal))) return false;
        if (profile.TryGetProperty("capabilities", out JsonElement capabilities) &&
            (capabilities.ValueKind != JsonValueKind.Number || !capabilities.TryGetInt32(out int flags) || flags < 0 || (flags & ~15) != 0)) return false;
        if (!profile.TryGetProperty("osFamily", out JsonElement family) || family.GetString() is not { Length: > 0 and <= 128 } ||
            !profile.TryGetProperty("osVersion", out JsonElement version) || version.GetString() is not { Length: > 0 and <= 128 } ||
            !profile.TryGetProperty("cpuCount", out JsonElement cpu) || !cpu.TryGetInt32(out int cpuCount) || cpuCount is < 1 or > 65536 ||
            !profile.TryGetProperty("memoryBytes", out JsonElement memory) || !memory.TryGetInt64(out long memoryBytes) || memoryBytes is < 0 or > (1L << 60) ||
            !profile.TryGetProperty("capabilityState", out JsonElement state) || state.GetString() is not ("available" or "partial" or "unsupported" or "permission_denied")) return false;
        return !family.GetString()!.Any(char.IsControl) && !version.GetString()!.Any(char.IsControl);
    }

    private sealed record BackfillBody(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? MetricKey, long ExpectedRevision, Guid OperationId, Guid CorrelationId, string RequestDigest, string ChangeReason);
    private sealed record HostBindingBody(JsonElement Payload, Guid HostId, string HostName, string IdentityFingerprint, long BindingRevision, long ProfileRevision, long ExpectedRevision, Guid OperationId, Guid CorrelationId, string RequestDigest, string ChangeReason);

    private static async Task<IResult> GetPolicyAsync(string dataClass, HttpContext context, IRetentionPolicyService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken = default)
    { try { if (!RetentionDataClasses.Contains(dataClass)) return Results.BadRequest(); return Results.Ok(await service.GetPolicyAsync(resolver.Resolve(context.User), dataClass, cancellationToken).ConfigureAwait(false)); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (KeyNotFoundException) { return Results.NotFound(); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); } }

    private static async Task<IResult> UpdatePolicyAsync(string dataClass, PolicyBody body, HttpContext context, IRetentionPolicyService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken = default)
    { try { if (body is null || !RetentionDataClasses.Contains(dataClass) || body.ExpectedRevision < 1 || body.ChangeReason is null || body.ChangeReason.Length is 0 or > 512 || dataClass != body.DataClass || body.MinimumPartitionsToKeep is < 1 or > 10000 || body.RetainFor is not null && (body.RetainFor < TimeSpan.FromDays(1) || body.RetainFor > TimeSpan.FromDays(3650)) || body.Enabled && body.RetainFor is null) return Results.BadRequest(); AuthorizationContext auth = resolver.Resolve(context.User); RetentionPolicy policy = new(dataClass, body.Enabled, body.RetainFor, body.MinimumPartitionsToKeep); return Results.Ok(await service.UpdatePolicyAsync(new RetentionPolicyUpdateRequest(auth, policy, body.ExpectedRevision, body.ChangeReason, RepositoryTimeout), cancellationToken).ConfigureAwait(false)); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (InvalidOperationException) { return Results.Conflict(); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); } }

    private static async Task<IResult> PreviewRetentionAsync(HttpContext context, IRetentionService service, WindowsGroupRoleResolver resolver, int limit = 100, string? cursor = null, CancellationToken cancellationToken = default)
    { try { if (limit is < 1 or > 1024 || cursor is { Length: > 1024 } || cursor is not null && !ValidCursor(cursor)) return Results.BadRequest(); var result = await service.PreviewAsync(new RetentionPreviewQuery(resolver.Resolve(context.User), limit, RepositoryTimeout, cursor), cancellationToken).ConfigureAwait(false); return Bounded(result); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); } catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); } catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); } }

    private static bool CanReadTarget(AuthorizationContext authorization, MonitoredInstanceId target) =>
        authorization.CanAccess(ApplicationRole.Viewer, target) ||
        authorization.CanAccess(ApplicationRole.Operator, target) ||
        authorization.CanAccess(ApplicationRole.TargetAdministrator, target);

    private static bool ValidWindow(DateTimeOffset from, DateTimeOffset to, TimeSpan maximum) =>
        from.Offset == TimeSpan.Zero && to.Offset == TimeSpan.Zero && to > from && to - from <= maximum;

    private static IResult Bounded<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value).Length > 1_048_576 ? Results.StatusCode(StatusCodes.Status413PayloadTooLarge) : Results.Ok(value);
    private static string RollupState(IReadOnlyList<RollupResult> rows) => rows.Count == 0 ? "no_data" : rows.Any(x => x.VisibilityState == "unsupported") ? "unsupported" : rows.Any(x => x.VisibilityState != "complete" || x.Truncated || x.Coverage < 1) ? "partial" : "complete";
    private static Task<IResult> DetachRetentionAsync(HttpContext context, IRetentionService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken = default) => ExecuteRetentionAsync(context, service, resolver, "detach", cancellationToken);
    private static Task<IResult> DropRetentionAsync(HttpContext context, IRetentionService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken = default) => ExecuteRetentionAsync(context, service, resolver, "drop", cancellationToken);
    private static async Task<IResult> ExecuteRetentionAsync(HttpContext context, IRetentionService service, WindowsGroupRoleResolver resolver, string operation, CancellationToken cancellationToken)
    {
        try
        {
            AuthorizationContext authorization = resolver.Resolve(context.User);
            if (!authorization.HasRoleForAllTargets(ApplicationRole.SecurityAdministrator)) return Results.Forbid();
            using JsonDocument document = await ReadBoundedJsonAsync(context, cancellationToken).ConfigureAwait(false);
            RetentionExecutionBody? body = document.RootElement.Deserialize<RetentionExecutionBody>(MutationJsonOptions);
            if (body is null || !RetentionDataClasses.Contains(body.DataClass) || body.OperationId == Guid.Empty || body.ExecutionId == Guid.Empty || body.ExpectedPolicyRevision < 1 || body.CorrelationId == Guid.Empty || !SafeIdentifier(body.ParentSchema, 128) || !SafeIdentifier(body.ParentTable, 128) || !SafeIdentifier(body.PartitionName, 128) || body.ChangeReason is null || body.ChangeReason.Length is 0 or > 512 || body.RequestDigest is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) || body.Operation is not null && !string.Equals(body.Operation, operation, StringComparison.Ordinal)) return Results.BadRequest();
            byte[] computed = MutationDigestV1.Retention(operation, body.DataClass, body.ParentSchema, body.ParentTable, body.PartitionName, body.ExpectedPolicyRevision, body.ExecutionId, body.OperationId, authorization.ActorSid.Value, "all", body.CorrelationId, body.ChangeReason);
            VerifyDigest(body.RequestDigest, computed);
            RetentionExecutionRequest request = new(authorization, body.DataClass, body.ParentSchema, body.ParentTable, body.PartitionName, body.ExecutionId, RepositoryTimeout, body.ExpectedPolicyRevision, MutationDigestV1.Hex(computed), body.CorrelationId, body.ChangeReason, operation, body.OperationId);
            context.Response.Headers["X-Correlation-ID"] = body.CorrelationId.ToString("D");
            RetentionExecutionResult result = await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return result.State == RetentionExecutionState.Blocked ? Results.Conflict(result) : Results.Ok(result);
        }
        catch (UnsupportedMutationMediaTypeException) { return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (InvalidOperationException) { return Results.Conflict(); }
        catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static void VerifyDigest(string suppliedHex, ReadOnlySpan<byte> computed)
    {
        if (suppliedHex is null || suppliedHex.Length != 64 || !suppliedHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Mutation requestDigest is invalid.");
        byte[] supplied = Convert.FromHexString(suppliedHex);
        if (!CryptographicOperations.FixedTimeEquals(supplied, computed))
            throw new ArgumentException("Mutation requestDigest does not match the canonical request.");
    }

    private static bool SafeIdentifier(string value, int max) => value is { Length: > 0 } && value.Length <= max && value.All(static c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    public sealed record RetentionExecutionBody(string? Operation, Guid OperationId, Guid ExecutionId, string DataClass, string ParentSchema, string ParentTable, string PartitionName, long ExpectedPolicyRevision, string ChangeReason, Guid CorrelationId, string? RequestDigest = null);
    public sealed record PolicyBody(string DataClass, bool Enabled, TimeSpan? RetainFor, int MinimumPartitionsToKeep, long ExpectedRevision, string ChangeReason);
}
