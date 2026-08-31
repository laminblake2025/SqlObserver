using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Analytics;

/// <summary>Hard safety limits for every analytics worker invocation.</summary>
public static class AnalyticsJobBounds
{
    public const int MaximumRows = 100_000;
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(45);
    public const int MaximumConcurrency = 2;
    public const int MaximumBackfillDays = 90;
    public static string LeaseKey(string jobKind, Guid? targetId) => $"analytics/{jobKind}/{(targetId?.ToString("D") ?? "global")}";
}

public enum AnalyticsReplayStatus { New = 1, Replay = 2, Divergent = 3 }
public sealed record AnalyticsReplayResult(AnalyticsReplayStatus Status, string ResultDigest, string? ResultJson);

/// <summary>The canonical replay envelope shared by every analytics write.</summary>
public sealed record AnalyticsReplayEnvelope(Guid OperationId, byte[] RequestDigest, byte[] ResultDigest, string CanonicalRequest)
{
    public string RequestDigestHex => Convert.ToHexString(RequestDigest).ToLowerInvariant();
    public string ResultDigestHex => Convert.ToHexString(ResultDigest).ToLowerInvariant();
}

/// <summary>
/// Computes operation and content identities from all normalized identity,
/// payload, and lease actor fields.  This prevents two writers from silently
/// sharing an operation id for different inputs.
/// </summary>
public static class AnalyticsReplayContract
{
    public static AnalyticsReplayEnvelope Create(string operationKind, AnalyticsJobRequest request, string payloadJson, string? resultJson = null)
    {
        ArgumentNullException.ThrowIfNull(request); AnalyticsJobRequest.Validate(request);
        return Create(operationKind, request.JobId, request.TargetId?.Value, request.TargetRevision?.Value, request.WorkKey,
            request.Lease.Owner.Value, request.Lease.FencingToken.Value, payloadJson, resultJson);
    }

    public static AnalyticsReplayEnvelope CreateForBackfill(string operationKind, AnalyticsBackfillJob job, WorkerLeaseIdentity lease,
        DateTimeOffset dayStartUtc, string? cursor, string payloadJson, string? resultJson = null)
    {
        ArgumentNullException.ThrowIfNull(job); ArgumentNullException.ThrowIfNull(lease); job.Validate();
        if (dayStartUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Backfill day must be UTC.", nameof(dayStartUtc));
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        string context = JsonSerializer.Serialize(new { dayStartUtc = dayStartUtc.ToString("O"), cursor = cursor ?? string.Empty, payload = payload.RootElement });
        return Create(operationKind, job.JobId, job.TargetId.Value, job.TargetRevision.Value, lease.Key.Value,
            lease.Owner.Value, lease.FencingToken.Value, context, resultJson);
    }

    private static AnalyticsReplayEnvelope Create(string operationKind, Guid jobId, Guid? targetId, long? targetRevision,
        string workKey, Guid owner, long fence, string payloadJson, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(operationKind) || operationKind.Length > 64 || jobId == Guid.Empty || targetId == Guid.Empty ||
            targetRevision is <= 0 || owner == Guid.Empty || fence <= 0) throw new ArgumentException("Analytics replay identity is invalid.", nameof(operationKind));
        string normalizedPayload = Canonicalize(payloadJson);
        string identity = string.Join("|", "analytics-replay-v1", operationKind.Trim(), jobId.ToString("D"), targetId!.Value.ToString("D"),
            targetRevision!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), workKey.Trim(), owner.ToString("D"), fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string canonicalRequest = identity + "|payload=" + normalizedPayload;
        byte[] requestDigest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest));
        string normalizedResult = Canonicalize(resultJson ?? normalizedPayload);
        byte[] resultDigest = SHA256.HashData(Encoding.UTF8.GetBytes(identity + "|payload=" + normalizedPayload + "|result=" + normalizedResult));
        byte[] operationHash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest));
        return new AnalyticsReplayEnvelope(new Guid(operationHash.AsSpan(0, 16)), requestDigest, resultDigest, canonicalRequest);
    }

    private static string Canonicalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Replay payload is required.", nameof(json));
        using JsonDocument document = JsonDocument.Parse(json); return Canonicalize(document.RootElement);
    }

    private static string Canonicalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonicalize(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonicalize)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()), JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "null",
        _ => throw new ArgumentException("Replay payload contains an unsupported JSON value.")
    };
}

/// <summary>Deterministic replay fence. A job identity can only be reused with byte-identical input.</summary>
public sealed class AnalyticsReplayGuard
{
    private readonly Dictionary<Guid, (string Request, AnalyticsReplayResult Result)> values = new();
    public AnalyticsReplayResult Check(Guid operationId, ReadOnlySpan<byte> requestBytes, string resultJson)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(resultJson); if (resultJson.Length > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(resultJson));
        string request = Convert.ToHexString(SHA256.HashData(requestBytes)).ToLowerInvariant();
        lock (values)
        {
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resultJson))).ToLowerInvariant();
            if (values.TryGetValue(operationId, out var existing))
                return existing.Request == request && existing.Result.ResultDigest == digest ? existing.Result with { Status = AnalyticsReplayStatus.Replay } : new AnalyticsReplayResult(AnalyticsReplayStatus.Divergent, existing.Result.ResultDigest, null);
            var result = new AnalyticsReplayResult(AnalyticsReplayStatus.New, digest, resultJson); values.Add(operationId, (request, result)); return result;
        }
    }
}

public sealed record AnalyticsWorkBudget(int Rows = 0, int Bytes = 0, TimeSpan? Duration = null, int Concurrency = AnalyticsJobBounds.MaximumConcurrency)
{
    public bool IsValid => Rows is >= 0 and <= AnalyticsJobBounds.MaximumRows && Bytes is >= 0 and <= AnalyticsJobBounds.MaximumBytes && (Duration is null || Duration.Value > TimeSpan.Zero && Duration.Value <= AnalyticsJobBounds.MaximumDuration) && Concurrency is >= 1 and <= AnalyticsJobBounds.MaximumConcurrency;
}

/// <summary>One explicitly enqueued backfill range. Range metadata is persisted by the job store.</summary>
public sealed record AnalyticsBackfillJob(
    Guid JobId,
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? MetricKey,
    string? Cursor = null)
{
    /// <summary>Persisted calendar day enables crash recovery without rescanning prior days.</summary>
    public DateTimeOffset? CurrentDayUtc { get; init; }
    /// <summary>Database-persisted components of the opaque total-order cursor.</summary>
    public string? CursorSourceKind { get; init; }
    public DateTimeOffset? CursorObservedAtUtc { get; init; }
    public Guid? CursorSourceId { get; init; }
    public string? CursorMetricKey { get; init; }
    public string? CursorDimensionHash { get; init; }
    public int? CursorOrdinal { get; init; }
    public Guid? CursorTargetId { get; init; }
    public long? CursorTargetRevision { get; init; }
    public DateTimeOffset? CursorDayUtc { get; init; }
    public int? CursorCatalogVersion { get; init; }
    public void Validate()
    {
        if (JobId == Guid.Empty || FromUtc.Offset != TimeSpan.Zero || ToUtc.Offset != TimeSpan.Zero || ToUtc <= FromUtc || ToUtc - FromUtc > TimeSpan.FromDays(AnalyticsJobBounds.MaximumBackfillDays) || CurrentDayUtc is not null && (CurrentDayUtc.Value.Offset != TimeSpan.Zero || CurrentDayUtc.Value.UtcDateTime.TimeOfDay != TimeSpan.Zero || CurrentDayUtc.Value < new DateTimeOffset(FromUtc.UtcDateTime.Date, TimeSpan.Zero) || CurrentDayUtc.Value >= ToUtc))
            throw new ArgumentException("Backfill range is outside the bounded UTC contract.");
        if (MetricKey is not null && (MetricKey.Length is 0 or > 128 || !MetricKey.All(static c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Backfill metric key is not a catalog token.", nameof(MetricKey));
    }
}

public sealed record AnalyticsBackfillPage(IReadOnlyList<MetricPoint> Points, string? NextCursor, bool HasMore, string? SafeCursor = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Points);
        if (Points.Count > AnalyticsJobBounds.MaximumRows || NextCursor is { Length: > 4096 } || SafeCursor is { Length: > 4096 } || (HasMore && string.IsNullOrEmpty(NextCursor)))
            throw new ArgumentException("Backfill page exceeds its bounded cursor contract.");
    }
}

public static class AnalyticsBackfillWindows
{
    /// <summary>Returns one stable half-open window for a calendar day.</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ForDay(AnalyticsBackfillJob job, DateTimeOffset dayStartUtc)
    {
        ArgumentNullException.ThrowIfNull(job); job.Validate();
        if (dayStartUtc.Offset != TimeSpan.Zero || dayStartUtc.TimeOfDay != TimeSpan.Zero) throw new ArgumentException("Backfill day must be a UTC boundary.", nameof(dayStartUtc));
        DateTimeOffset end = dayStartUtc.AddDays(1) < job.ToUtc ? dayStartUtc.AddDays(1) : job.ToUtc;
        DateTimeOffset start = dayStartUtc > job.FromUtc ? dayStartUtc : job.FromUtc;
        if (end <= start) throw new ArgumentException("Backfill day is outside the requested range.", nameof(dayStartUtc));
        return (start, end);
    }
}

public enum AnalyticsBackfillCompletion { Succeeded = 1, Partial = 2, Failed = 3, Cancelled = 4 }

/// <summary>
/// Repository-only backfill control plane. Implementations call fixed
/// migration functions; they never open a monitored SQL target connection.
/// </summary>
public interface IAnalyticsBackfillStore
{
    ValueTask<IReadOnlyList<AnalyticsBackfillJob>> ClaimAsync(WorkerLeaseIdentity lease, int maximumJobs, CancellationToken cancellationToken);
    /// <summary>Creates/locks the target day's repository partition under the worker fence.</summary>
    ValueTask EnsureHistoricalPartitionAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, DateTimeOffset dayStartUtc, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask<AnalyticsBackfillPage> ReadPageAsync(AnalyticsBackfillJob job, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, int maximumRows, int maximumBytes, CancellationToken cancellationToken);
    ValueTask SaveCursorAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, DateTimeOffset dayStartUtc, string? cursor, CancellationToken cancellationToken);
    ValueTask CompleteAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, AnalyticsBackfillCompletion completion, string? failureDetail, CancellationToken cancellationToken);
    ValueTask RecordReplayAsync(Guid operationId, AnalyticsBackfillJob job, WorkerLeaseIdentity lease, ReadOnlyMemory<byte> requestDigest, ReadOnlyMemory<byte> resultDigest, string resultJson, CancellationToken cancellationToken);
}
