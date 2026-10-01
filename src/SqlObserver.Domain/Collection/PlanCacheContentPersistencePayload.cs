using System.Text.Json;

namespace SqlObserver.Domain.Collection;

/// <summary>Opaque link input; ciphertext is written separately under its collector lease.</summary>
public static class PlanCacheContentPersistencePayload
{
    public static byte[] Serialize(PlanCacheContentOutputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("targetId", batch.Runtime.TargetId.Value);
            writer.WriteNumber("targetRevision", batch.Runtime.TargetRevision.Value);
            writer.WriteString("sourceEpoch", batch.Runtime.SourceEpoch);
            writer.WriteString("readCompletedUtc", batch.Runtime.ReadCompletedUtc.UtcDateTime);
            writer.WriteString("captureState", batch.CaptureState switch
            {
                PlanCacheContentCaptureState.Enabled => "enabled",
                PlanCacheContentCaptureState.Disabled => "disabled",
                PlanCacheContentCaptureState.ProtectionUnavailable => "protection_unavailable",
                _ => throw new InvalidDataException("Cache content capture state is not approved."),
            });
            writer.WriteNumber("contentLimit", batch.ContentLimit);
            writer.WriteNumber("attemptedEntries", batch.AttemptedEntries);
            writer.WriteNumber("unqualifiedEntries", batch.UnqualifiedEntries);
            writer.WriteNumber("sourceContentBytes", batch.SourceContentBytes);
            writer.WriteStartArray("items");
            foreach (PlanCacheStatementContentObservation item in batch.Items
                .OrderBy(static item => item.Statement.Watermark.Key.Query.DatabaseId)
                .ThenBy(static item => item.Statement.Watermark.Key.CacheEntryFingerprint, StringComparer.Ordinal))
            {
                PlanCacheRuntimeWatermarkKey key = item.Statement.Watermark.Key;
                writer.WriteStartObject();
                writer.WriteNumber("databaseId", key.Query.DatabaseId);
                writer.WriteString("databaseIncarnation", key.DatabaseIncarnation);
                writer.WriteString("queryFingerprint", key.Query.QueryFingerprint);
                writer.WriteString("planFingerprint", key.Plan.PlanFingerprint);
                writer.WriteString("cacheEntryFingerprint", key.CacheEntryFingerprint);
                WriteValue("text", item.Text); WriteValue("plan", item.Plan);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
            void WriteValue(string name, PlanCacheContentValue value)
            {
                if (value.ProtectedContent is not null)
                    throw new InvalidOperationException("Cache content must have a leased repository reference before serialization.");
                writer.WriteStartObject(name);
                writer.WriteString("state", StateName(value.State));
                if (value.Reference is { } reference)
                {
                    writer.WriteString("payloadId", reference.PayloadId.Value);
                    writer.WriteString("fingerprint", reference.Fingerprint.ToHexString().ToLowerInvariant());
                    writer.WriteNumber("ciphertextBytes", value.CiphertextBytes);
                }
                writer.WriteEndObject();
            }
        }
        if (buffer.Length > PlanCacheContentOutputBatch.MaximumSerializedBytes)
            throw new InvalidDataException("Cache content links exceed their serialized byte bound.");
        return buffer.ToArray();
    }

    private static string StateName(PlanCacheContentState state) => state switch
    {
        PlanCacheContentState.Available => "available",
        PlanCacheContentState.Unavailable => "unavailable",
        PlanCacheContentState.Oversized => "oversized",
        PlanCacheContentState.ResponseByteLimit => "response_byte_limit",
        PlanCacheContentState.ProtectionUnavailable => "protection_unavailable",
        PlanCacheContentState.ProtectionFailed => "protection_failed",
        _ => throw new InvalidDataException("Cache content state is not approved."),
    };
}
