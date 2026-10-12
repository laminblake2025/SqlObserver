using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class PlanCacheContentReadRequest
{
    public PlanCacheContentReadRequest(MonitoredInstanceId targetId, Guid collectionRunId, int databaseId,
        string databaseIncarnation, string queryFingerprint, string planFingerprint, string cacheEntryFingerprint,
        SensitivePayloadKind kind, RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (collectionRunId == Guid.Empty || databaseId is < 5 or > 32766
            || !PlanCacheIntervalHistoryRequest.Digest(databaseIncarnation) || !PlanCacheIntervalHistoryRequest.Digest(queryFingerprint)
            || !PlanCacheIntervalHistoryRequest.Digest(planFingerprint) || !PlanCacheIntervalHistoryRequest.Digest(cacheEntryFingerprint)
            || kind is not (SensitivePayloadKind.QueryText or SensitivePayloadKind.ExecutionPlan))
            throw new ArgumentException("Cache content requires an exact run, user database, incarnation, query, plan, cache entry and approved content kind.");
        CollectionRunId = collectionRunId; DatabaseId = databaseId; DatabaseIncarnation = databaseIncarnation;
        QueryFingerprint = queryFingerprint; PlanFingerprint = planFingerprint; CacheEntryFingerprint = cacheEntryFingerprint; Kind = kind;
    }
    public MonitoredInstanceId TargetId { get; }
    public Guid CollectionRunId { get; }
    public int DatabaseId { get; }
    public string DatabaseIncarnation { get; }
    public string QueryFingerprint { get; }
    public string PlanFingerprint { get; }
    public string CacheEntryFingerprint { get; }
    public SensitivePayloadKind Kind { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed record PlanCacheContentStoredResult(string Reason, DateTimeOffset? ObservedAtUtc, ProtectedSensitivePayload? Payload);
public sealed record PlanCacheContentReadResult(string Status, string Reason, DateTimeOffset? ObservedAtUtc, string? Content);

public interface IPlanCacheContentReadRepositoryPort
{
    ValueTask<PlanCacheContentStoredResult> ReadAsync(PlanCacheContentReadRequest request, CancellationToken cancellationToken);
    ValueTask AuditAsync(PlanCacheContentReadRequest request, string actorSid, string outcome, CancellationToken cancellationToken);
}
public interface IPlanCacheContentReadService
{
    ValueTask<PlanCacheContentReadResult> ReadAsync(AuthorizationContext authorization, PlanCacheContentReadRequest request, CancellationToken cancellationToken);
}
