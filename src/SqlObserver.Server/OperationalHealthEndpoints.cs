using System.Text.Json;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

/// <summary>Bounded, target-scoped operational-health read routes.</summary>
public static class OperationalHealthEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new CanonicalUtcDateTimeOffsetConverter() } };
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));
    private const int MaximumResponseBytes = 1_048_576;

    public static IEndpointRouteBuilder MapOperationalHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}").RequireAuthorization();
        group.MapGet("/backups", BackupsAsync); group.MapGet("/sql-agent/failures", AgentAsync); group.MapGet("/tempdb", TempDbAsync); group.MapGet("/tempdb/files", TempDbFilesAsync); group.MapGet("/availability-groups/replicas", ReplicasAsync); group.MapGet("/availability-groups/databases", DatabasesAsync);
        return endpoints;
    }
    private static Task<IResult> BackupsAsync(HttpContext c, Guid instanceId, IOperationalHealthQueryService s, WindowsGroupRoleResolver r, int limit = 50, string? cursor = null, CancellationToken ct = default) => Execute(c, token => s.GetBackupsAsync(r.Resolve(c.User), Request(instanceId, limit, cursor), token), MapBackups);
    private static Task<IResult> AgentAsync(HttpContext c, Guid instanceId, IOperationalHealthQueryService s, WindowsGroupRoleResolver r, int limit = 50, string? cursor = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken ct = default) => Execute(c, token => s.GetAgentFailuresAsync(r.Resolve(c.User), Request(instanceId, limit, cursor, fromUtc, toUtc), token), MapAgent);
    private static Task<IResult> TempDbAsync(HttpContext c, Guid instanceId, IOperationalHealthQueryService s, WindowsGroupRoleResolver r, int limit = 50, string? cursor = null, CancellationToken ct = default) => Execute(c, token => s.GetTempDbAsync(r.Resolve(c.User), Request(instanceId, limit, cursor), token), x => MapTempDb(x, false));
    private static Task<IResult> TempDbFilesAsync(HttpContext c, Guid instanceId, IOperationalHealthQueryService s, WindowsGroupRoleResolver r, int limit = 50, string? cursor = null, CancellationToken ct = default) => Execute(c, token => s.GetTempDbFilesAsync(r.Resolve(c.User), Request(instanceId, limit, cursor), token), x => MapTempDb(x, true));
    private static Task<IResult> ReplicasAsync(HttpContext c, Guid instanceId, IOperationalHealthQueryService s, WindowsGroupRoleResolver r, int limit = 50, string? cursor = null, CancellationToken ct = default) => Execute(c, token => s.GetAvailabilityGroupReplicasAsync(r.Resolve(c.User), Request(instanceId, limit, cursor), token), x => MapAvailability(x, true));
    private static Task<IResult> DatabasesAsync(HttpContext c, Guid instanceId, IOperationalHealthQueryService s, WindowsGroupRoleResolver r, int limit = 50, string? cursor = null, CancellationToken ct = default) => Execute(c, token => s.GetAvailabilityGroupDatabasesAsync(r.Resolve(c.User), Request(instanceId, limit, cursor), token), x => MapAvailability(x, false));

    private static async Task<IResult> Execute<T>(HttpContext context, Func<CancellationToken, ValueTask<T?>> call, Func<T, OperationalHealthPageDto> map) where T : class
    {
        try
        {
            TimeSpan timeout = context.RequestServices.GetService<IOptions<OperationalHealthServerOptions>>()?.Value.RequestTimeout ?? TimeSpan.FromSeconds(15);
            using var envelope = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); envelope.CancelAfter(timeout);
            T? value = await call(envelope.Token).ConfigureAwait(false); if (value is null) return Results.NotFound();
            OperationalHealthPageDto dto = map(value);
            return BoundedOk(dto);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (OperationalHealthContractException exception) { return exception.IsDrift ? Results.StatusCode(StatusCodes.Status409Conflict) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (OperationalHealthRepositoryException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (OperationCanceledException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
        catch (Exception) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
    /// <summary>Shared response cap seam used by production mapping and bounded-surface tests.</summary>
    internal static IResult BoundedOk(OperationalHealthPageDto dto)
        => JsonSerializer.SerializeToUtf8Bytes(dto, Json).Length > MaximumResponseBytes
            ? Results.StatusCode(StatusCodes.Status413PayloadTooLarge)
            : Results.Json(dto, Json);
    private static OperationalHealthRequest Request(Guid id, int limit, string? cursor, DateTimeOffset? from = null, DateTimeOffset? to = null) => new(new MonitoredInstanceId(id), from, to, limit, cursor, RepositoryTimeout);
    private static OperationalHealthPageDto MapBackups(BackupStatusSnapshot x) => Page(x.TargetId, x.TargetRevision.Value, x.RunId, x.ObservedAtUtc, x.State, x.Items.Select(static i => (object)new { databaseFingerprint = i.DatabaseFingerprint, kind = i.Kind.ToString(), lastFinishUtc = i.LastFinishUtc, sourceLocalFinish = i.SourceLocalFinish, sourceTimeUnknown = i.SourceTimeUnknown, sizeBytes = i.SizeBytes, copyOnly = i.CopyOnly, hasChecksum = i.HasChecksum, isDamaged = i.IsDamaged, coverage = i.Coverage.ToString(), backupSetId = i.BackupSetId }).ToArray(), x.NextCursor, x.NextCursor is not null, x.Truncated, null, null, null, null, null, null, null, null);
    private static OperationalHealthPageDto MapAgent(SqlAgentFailureSnapshot x) => Page(x.TargetId, x.TargetRevision.Value, x.RunId, x.ObservedAtUtc, x.State, x.Items.Select(static i => (object)new { jobId = i.JobId, historyInstanceId = i.HistoryInstanceId, stepId = i.StepId, runStatus = i.RunStatus, isJobOutcome = SqlAgentFailureSemantics.IsJobOutcome(i), countsAsJobFailure = SqlAgentFailureSemantics.CountsAsJobFailure(i), failureKind = i.FailureKind.ToString(), messageId = i.MessageId, severity = i.Severity, retryAttempt = i.RetryAttempt, durationSeconds = i.DurationSeconds, firstObservedAtUtc = i.FirstObservedAtUtc, contentAvailable = false, failureFingerprint = i.FailureFingerprint }).ToArray(), x.NextCursor, x.NextCursor is not null, x.Truncated, null, null, null, null, x.CoverageFromUtc, x.CoverageToUtc, "firstObservedUtc", null);
    private static OperationalHealthPageDto MapTempDb(TempDbSnapshot x, bool files) => Page(x.TargetId, x.TargetRevision.Value, x.RunId, x.ObservedAtUtc, x.State, files ? x.Files.Select(static i => (object)new { fileId = i.FileId, sizeBytes = i.SizeBytes, usedBytes = i.UsedBytes, freeBytes = i.FreeBytes, state = i.State.ToString() }).ToArray() : Array.Empty<object>(), x.NextCursor, x.NextCursor is not null, x.Truncated, files ? null : x.TotalBytes, files ? null : x.UsedBytes, files ? null : x.LogTotalBytes, files ? null : x.LogUsedBytes, null, null, null, null);
    private static OperationalHealthPageDto MapAvailability(AvailabilityGroupsSnapshot x, bool replicas) => Page(x.TargetId, x.TargetRevision.Value, x.RunId, x.ObservedAtUtc, x.State, replicas ? x.Replicas.Select(static i => (object)new { groupFingerprint = i.GroupFingerprint, replicaFingerprint = i.ReplicaFingerprint, role = i.Role, operationalState = i.OperationalState, connectedState = i.ConnectedState, visibilityScope = i.VisibilityScope.ToString(), stateAvailable = i.StateAvailable }).ToArray() : x.Databases.Select(static i => (object)new { groupFingerprint = i.GroupFingerprint, databaseFingerprint = i.DatabaseFingerprint, synchronizationState = i.SynchronizationState, databaseState = i.DatabaseState, visibilityScope = i.VisibilityScope.ToString(), stateAvailable = i.StateAvailable }).ToArray(), x.NextCursor, x.NextCursor is not null, x.Truncated, null, null, null, null, null, null, null, x.VisibilityScope.ToString());
    private static OperationalHealthPageDto Page(MonitoredInstanceId target, long revision, CollectorRunId? run, DateTimeOffset observed, OperationalObservationState state, object[] items, string? next, bool more, bool truncated, object? total, object? used, object? logTotal, object? logUsed, DateTimeOffset? from, DateTimeOffset? to, string? coverage, string? visibility) => new(target.Value, revision, run?.Value, observed, state.ToString(), items, more, next, truncated, total, used, logTotal, logUsed, from, to, coverage, visibility, state == OperationalObservationState.NoData ? null : new { observedAtUtc = observed });
    public sealed record OperationalHealthPageDto(Guid TargetId, long TargetRevision, Guid? RunId, DateTimeOffset ObservedAtUtc, string State, object[] Items, bool HasMore, string? NextCursor, bool Truncated, object? TotalBytes, object? UsedBytes, object? LogTotalBytes, object? LogUsedBytes, DateTimeOffset? CoverageFromUtc, DateTimeOffset? CoverageToUtc, string? Coverage, string? VisibilityScope, object? Evidence);
}

public sealed class OperationalHealthServerOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
