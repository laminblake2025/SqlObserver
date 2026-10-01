using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Private writer compatibility entry point for the shared opaque serialization.</summary>
internal static class PlanCacheRuntimeCommitPayload
{
    internal static byte[] Serialize(PlanCacheRuntimeOutputBatch batch) => PlanCacheRuntimePersistencePayload.Serialize(batch);
}
