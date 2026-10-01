using System.Text.Json;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Domain.Collection;

/// <summary>The five JSON inputs charged by the atomic v2 PostgreSQL writer.</summary>
public static class QueryPerformanceV2PersistenceBudget
{
    public static int Measure(IReadOnlyList<QueryPerformanceObservation> observations,
        IReadOnlyList<QueryPerformanceDatabaseStatus> statuses, PlanCacheRuntimeOutputBatch? cache,
        QueryPerformanceTargetStatus? targetStatus = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(statuses);
        int bytes = QueryPerformancePersistencePayload.Serialize(observations, statuses, targetStatus,
            contentLinksCommittedWithRun: true).Length;
        if (cache is not null)
        {
            using var document = JsonDocument.Parse(PlanCacheRuntimePersistencePayload.Serialize(cache));
            bytes = checked(bytes + QueryPerformancePersistencePayload.SerializeCanonicalJsonb(document.RootElement).Length);
        }
        // A protected payload receives a new opaque UUID at the fenced write.
        // Every UUID has the same JSON width, so the zero UUID measures it
        // exactly without allocating an identity before that write succeeds.
        var text = observations.Where(static item => item.ContentReference is not null || item.ProtectedContent is not null)
            .GroupBy(static item => (item.Query.DatabaseId, item.Query.QueryFingerprint)).Select(static group => group.First())
            .Select(static item => new
            {
                database_id = item.Query.DatabaseId,
                query_fingerprint = item.Query.QueryFingerprint,
                payload_id = item.ContentReference?.PayloadId.Value ?? Guid.Empty,
                fingerprint = Fingerprint(item.ContentReference, item.ProtectedContent),
            }).ToArray();
        var plans = observations.Where(static item => item.PlanContentReference is not null || item.ProtectedPlanContent is not null)
            .GroupBy(static item => (item.Query.DatabaseId, item.Query.QueryFingerprint, item.Plan!.PlanFingerprint)).Select(static group => group.First())
            .Select(static item => new
            {
                database_id = item.Query.DatabaseId,
                query_fingerprint = item.Query.QueryFingerprint,
                plan_fingerprint = item.Plan!.PlanFingerprint,
                payload_id = item.PlanContentReference?.PayloadId.Value ?? Guid.Empty,
                fingerprint = Fingerprint(item.PlanContentReference, item.ProtectedPlanContent),
            }).ToArray();
        var waits = observations.Where(static item => item.WaitSnapshot is not null)
            .GroupBy(static item => (item.Query.DatabaseId, item.Query.QueryFingerprint, item.Plan!.PlanFingerprint)).Select(static group => group.First())
            .Select(static item => new
            {
                database_id = item.Query.DatabaseId,
                query_fingerprint = item.Query.QueryFingerprint,
                plan_fingerprint = item.Plan!.PlanFingerprint,
                categories = item.WaitSnapshot!.Categories.Select(static category => new
                { category = category.Category, waitMilliseconds = category.WaitMilliseconds }),
            }).ToArray();
        return checked(bytes + QueryPerformancePersistencePayload.SerializeCanonicalJsonb(text).Length +
            QueryPerformancePersistencePayload.SerializeCanonicalJsonb(plans).Length +
            QueryPerformancePersistencePayload.SerializeCanonicalJsonb(waits).Length);
    }

    private static string Fingerprint(SensitivePayloadReference? reference, ProtectedSensitivePayload? payload) =>
        (reference?.Fingerprint ?? payload!.Fingerprint).ToHexString().ToLowerInvariant();
}
