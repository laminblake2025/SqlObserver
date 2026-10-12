using System.Text.Json;

namespace SqlObserver.Domain.Collection;

/// <summary>Deterministic opaque-only input for the private cache transition writer.</summary>
public static class PlanCacheRuntimePersistencePayload
{
    public static byte[] Serialize(PlanCacheRuntimeOutputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("targetId", batch.TargetId.Value);
            writer.WriteNumber("targetRevision", batch.TargetRevision.Value);
            writer.WriteString("sourceEpoch", batch.SourceEpoch);
            writer.WriteString("readStartedUtc", batch.ReadStartedUtc.UtcDateTime);
            writer.WriteString("readCompletedUtc", batch.ReadCompletedUtc.UtcDateTime);
            writer.WriteNumber("candidateLimit", batch.CandidateLimit);
            writer.WriteNumber("candidateRows", batch.CandidateRows);
            writer.WriteNumber("unresolvedRows", batch.UnresolvedRows);
            writer.WriteNumber("invisibleDatabaseRows", batch.InvisibleDatabaseRows);
            writer.WriteNumber("excludedRows", batch.ExcludedRows);
            writer.WriteNumber("emittedRows", batch.EmittedRows);
            writer.WriteNumber("unqualifiedRows", batch.UnqualifiedRows);
            writer.WriteBoolean("sourceExhaustive", batch.SourceExhaustive);
            writer.WriteStartArray("items");
            foreach (PlanCacheRuntimeStatementObservation item in batch.Items
                .OrderBy(static item => item.Watermark.Key.Query.DatabaseId)
                .ThenBy(static item => item.Watermark.Key.Query.QueryFingerprint, StringComparer.Ordinal)
                .ThenBy(static item => item.Watermark.Key.CacheEntryFingerprint, StringComparer.Ordinal))
            {
                PlanCacheRuntimeWatermarkKey key = item.Watermark.Key;
                PlanCacheRuntimeCounters counters = item.Watermark.Counters;
                writer.WriteStartObject();
                writer.WriteNumber("databaseId", key.Query.DatabaseId);
                writer.WriteString("databaseIncarnation", key.DatabaseIncarnation);
                writer.WriteString("queryFingerprint", key.Query.QueryFingerprint);
                writer.WriteString("planFingerprint", key.Plan.PlanFingerprint);
                writer.WriteString("cacheEntryFingerprint", key.CacheEntryFingerprint);
                writer.WriteNumber("cpuUs", counters.CpuMicroseconds);
                writer.WriteNumber("durationUs", counters.DurationMicroseconds);
                writer.WriteNumber("executionCount", counters.Executions);
                writer.WriteNumber("logicalReads", counters.LogicalReads);
                writer.WriteNumber("writes", counters.Writes);
                writer.WriteNumber("rowsProcessed", counters.Rows);
                writer.WriteString("identityBeforeStartedUtc", item.Before.ReadStartedUtc.UtcDateTime);
                writer.WriteString("identityBeforeCompletedUtc", item.Before.ReadCompletedUtc.UtcDateTime);
                writer.WriteString("identityAfterStartedUtc", item.After.ReadStartedUtc.UtcDateTime);
                writer.WriteString("identityAfterCompletedUtc", item.After.ReadCompletedUtc.UtcDateTime);
                writer.WriteString("identityBeforeIncarnation", item.Before.DatabaseIncarnation);
                writer.WriteString("identityAfterIncarnation", item.After.DatabaseIncarnation);
                writer.WriteString("identityBeforeEpoch", item.Before.SourceEpoch);
                writer.WriteString("identityAfterEpoch", item.After.SourceEpoch);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.Length > PlanCacheRuntimeOutputBatch.MaximumSerializedBytes || buffer.Length > batch.EstimatedSizeBytes)
            throw new InvalidDataException("Cache commit payload exceeds its bounded JSON contract.");
        return buffer.ToArray();
    }
}
