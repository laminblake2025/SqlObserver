using System.Globalization;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

/// <summary>Versioned, read-only AG evidence with explicit coverage and authenticated continuations.</summary>
public static class AvailabilityGroupEvidenceEndpoints
{
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new CanonicalUtcDateTimeOffsetConverter() },
    };
    private static readonly string[] UtcFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"];
    private const int MaximumResponseBytes = 1_048_576;

    public static IEndpointRouteBuilder MapAvailabilityGroupEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder group = endpoints.MapGroup("/api/v2/observation-targets/{instanceId:guid}/availability-groups")
            .RequireAuthorization();
        group.MapGet("/replicas", ReplicasAsync);
        group.MapGet("/databases", DatabasesAsync);
        return endpoints;
    }

    private static Task<IResult> ReplicasAsync(HttpContext context, Guid instanceId,
        IAvailabilityGroupEvidenceQueryService service, WindowsGroupRoleResolver roles,
        AvailabilityGroupEvidenceCursorCodec codec, string? limit = null, string? atUtc = null,
        string? cursor = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, instanceId, AvailabilityGroupEvidenceKind.Replicas, limit, atUtc, cursor, codec,
            request => service.ReadReplicasAsync(roles.Resolve(context.User), request, cancellationToken), MapReplica);

    private static Task<IResult> DatabasesAsync(HttpContext context, Guid instanceId,
        IAvailabilityGroupEvidenceQueryService service, WindowsGroupRoleResolver roles,
        AvailabilityGroupEvidenceCursorCodec codec, string? limit = null, string? atUtc = null,
        string? cursor = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, instanceId, AvailabilityGroupEvidenceKind.Databases, limit, atUtc, cursor, codec,
            request => service.ReadDatabasesAsync(roles.Resolve(context.User), request, cancellationToken), MapDatabase);

    private static async Task<IResult> ExecuteAsync<T, TDto>(HttpContext context, Guid instanceId,
        AvailabilityGroupEvidenceKind stream, string? limit, string? atUtc, string? cursor,
        AvailabilityGroupEvidenceCursorCodec codec,
        Func<AvailabilityGroupEvidenceRequest, ValueTask<AvailabilityGroupEvidencePage<T>>> read,
        Func<T, TDto> map)
    {
        ApiCorrelation.Begin(context);
        context.Response.Headers.CacheControl = "no-store";
        AvailabilityGroupEvidenceRequest request;
        try
        {
            AvailabilityGroupEvidenceCursor? continuation = cursor is null ? null : codec.Decode(cursor, stream);
            DateTimeOffset cutoff = atUtc is not null ? ParseCutoff(atUtc) : continuation?.AtUtc ?? CurrentCutoff();
            request = new AvailabilityGroupEvidenceRequest(new MonitoredInstanceId(instanceId), cutoff,
                stream, ParseLimit(limit), RepositoryTimeout, continuation);
        }
        catch (ArgumentException exception)
        {
            string? field = exception.ParamName is "atUtc" or "cursor" or "limit" ? exception.ParamName : null;
            return Problem(context, 400, "invalid_request", "The AG evidence request is invalid.", field);
        }
        try
        {
            AvailabilityGroupEvidencePage<T> page = await read(request).ConfigureAwait(false);
            AvailabilityGroupEvidenceHeader header = page.Header;
            var response = new AvailabilityGroupEvidenceResponse<TDto>(2, header.TargetId.Value, header.AtUtc,
                header.TargetRevision, header.RunId, header.SourceObservedAtUtc, header.CompletedAtUtc,
                header.State.ToString(), EvidenceStatus(header.EvidenceStatus), header.SourceRows,
                header.OutputItems, header.RetainedItems, LossKind(header.LossKind), header.Truncated,
                header.CollectorEnabled, page.Items.Select(map).ToArray(), page.NextCursor is not null,
                page.NextCursor is null ? null : codec.Encode(page.NextCursor));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
            return bytes.Length > MaximumResponseBytes
                ? Problem(context, 502, "repository_contract_invalid", "AG evidence exceeded its response bound.")
                : Results.Bytes(bytes, "application/json");
        }
        catch (AvailabilityGroupEvidenceCursorStaleException)
        {
            return Problem(context, 409, "cursor_stale", "AG snapshot changed. Restart from the first page.");
        }
        catch (UnauthorizedAccessException)
        {
            return Problem(context, 403, "forbidden", "A read role scoped to this server is required.");
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return Problem(context, 502, "repository_contract_invalid", "AG evidence could not be verified.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return Problem(context, 504, "request_timed_out", "AG evidence exceeded its execution limit.");
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        {
            return Problem(context, 503, "repository_unavailable", "AG evidence is temporarily unavailable.");
        }
        catch (Exception)
        {
            return Problem(context, 500, "request_failed", "AG evidence could not be read.");
        }
    }

    private static IResult Problem(HttpContext context, int status, string code, string detail, string? field = null) =>
        Results.Problem(statusCode: status, title: "AG evidence request failed", detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code, ["correlationId"] = context.Response.Headers["X-Correlation-ID"].ToString(),
                ["field"] = field,
            });

    private static DateTimeOffset CurrentCutoff()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    }

    private static DateTimeOffset ParseCutoff(string atUtc) =>
        atUtc.EndsWith('Z') && DateTimeOffset.TryParseExact(atUtc, UtcFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset result)
            ? result : throw new ArgumentException("atUtc must be an ISO UTC timestamp ending in Z with at most six fractional digits.", nameof(atUtc));

    private static int ParseLimit(string? limit) => limit is null ? 50 :
        int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is >= 1 and <= 100
            ? value : throw new ArgumentException("limit must be an integer from 1 to 100.", nameof(limit));

    private static string EvidenceStatus(AvailabilityGroupEvidenceStatus status) => status switch
    {
        AvailabilityGroupEvidenceStatus.NotCollected => "not_collected",
        AvailabilityGroupEvidenceStatus.CollectionFailed => "collection_failed",
        AvailabilityGroupEvidenceStatus.EvidenceNotRetained => "evidence_not_retained",
        AvailabilityGroupEvidenceStatus.WithheldByByteLimit => "withheld_by_byte_limit",
        AvailabilityGroupEvidenceStatus.NoData => "no_data",
        AvailabilityGroupEvidenceStatus.Retained => "retained",
        _ => throw new InvalidDataException("AG evidence status is unknown."),
    };

    private static string LossKind(CollectorLossKind loss) => loss switch
    {
        CollectorLossKind.None => "none", CollectorLossKind.SourceRowLimit => "source_row_limit",
        CollectorLossKind.ResponseByteLimit => "response_byte_limit",
        CollectorLossKind.OutputValidationFailure => "output_validation_failure",
        CollectorLossKind.IngestionRejection => "ingestion_rejection",
        CollectorLossKind.VisibilityIncomplete => "visibility_incomplete",
        _ => throw new InvalidDataException("AG evidence loss kind is unknown."),
    };

    private static AvailabilityReplicaEvidenceDto MapReplica(AvailabilityReplicaEvidence item) =>
        new(item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint, item.Identity.ObserverReplicaFingerprint,
            item.Identity.ReplicaIsLocal, item.Identity.VisibilityScope.ToString(), item.StateObserved,
            item.Role, item.OperationalState, item.ConnectedState);

    private static AvailabilityDatabaseReplicaEvidenceDto MapDatabase(AvailabilityDatabaseReplicaEvidence item) =>
        new(item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint, item.DatabaseFingerprint,
            item.Identity.ObserverReplicaFingerprint, item.Identity.ReplicaIsLocal, item.Identity.VisibilityScope.ToString(),
            item.StateObserved, item.SynchronizationState, item.SynchronizationHealth, item.DatabaseState, item.IsSuspended,
            item.LogSendQueueKb, item.LogSendRateKbPerSecond, item.RedoQueueKb, item.RedoRateKbPerSecond,
            item.SourceLastCommitLocal?.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture));

    public sealed record AvailabilityGroupEvidenceResponse<T>(int SchemaVersion, Guid TargetId, DateTimeOffset AtUtc,
        long TargetRevision, Guid? RunId, DateTimeOffset? SourceObservedAtUtc, DateTimeOffset? CompletedAtUtc,
        string State, string EvidenceStatus, int SourceRows, int OutputItems, long RetainedItems,
        string LossKind, bool Truncated, bool CollectorEnabled, IReadOnlyList<T> Items, bool HasMore, string? NextCursor);

    public sealed record AvailabilityReplicaEvidenceDto(string GroupFingerprint, string ReplicaFingerprint,
        string? ObserverReplicaFingerprint, bool? ReplicaIsLocal, string VisibilityScope, bool StateObserved,
        string? Role, string? OperationalState, string? ConnectedState);

    public sealed record AvailabilityDatabaseReplicaEvidenceDto(string GroupFingerprint, string ReplicaFingerprint,
        string DatabaseFingerprint, string? ObserverReplicaFingerprint, bool? ReplicaIsLocal,
        string VisibilityScope, bool StateObserved, string? SynchronizationState, string? SynchronizationHealth,
        string? DatabaseState, bool? IsSuspended, long? LogSendQueueKb, long? LogSendRateKbPerSecond,
        long? RedoQueueKb, long? RedoRateKbPerSecond, string? SourceLastCommitLocal);
}
