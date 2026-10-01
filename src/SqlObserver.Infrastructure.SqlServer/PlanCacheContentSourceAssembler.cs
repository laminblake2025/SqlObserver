using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.SqlServer;

internal static class PlanCacheContentSourceAssembler
{
    internal static PlanCacheContentOutputBatch Bind(PlanCacheContentSourceRead source, PlanCacheRuntimeOutputBatch runtime)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.TargetId != source.Counters.TargetId || runtime.TargetRevision != source.Counters.TargetRevision
            || runtime.SourceEpoch != source.Counters.SourceEpoch
            || runtime.ReadStartedUtc.Ticks != source.Counters.ReadStartedAtUtc.Ticks - source.Counters.ReadStartedAtUtc.Ticks % 10
            || runtime.ReadCompletedUtc.Ticks != source.Counters.ReadCompletedAtUtc.Ticks - source.Counters.ReadCompletedAtUtc.Ticks % 10
            || source.ContentLimit is < 0 or > PlanCacheContentOutputBatch.MaximumEntries
            || source.ContentLimit > source.Counters.CandidateLimit || source.Content.Count > source.ContentLimit)
            throw new InvalidDataException("Cache content differs from its qualified runtime frame.");
        var original = source.Counters.Rows.ToDictionary(static row => row.CacheEntryFingerprint, StringComparer.Ordinal);
        var qualified = runtime.Items.ToDictionary(static row => row.Watermark.Key.CacheEntryFingerprint, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var content = new List<PlanCacheStatementContentObservation>();
        int unqualified = 0, bytes = 0;
        foreach (PlanCacheProtectedContent row in source.Content)
        {
            if (row is null || !seen.Add(row.Statement.CacheEntryFingerprint)
                || !original.TryGetValue(row.Statement.CacheEntryFingerprint, out var originalRow) || originalRow != row.Statement)
                throw new InvalidDataException("Cache content does not identify its original captured statement.");
            PlanCacheContentValue text = Value(row.TextState, row.Text, SensitivePayloadKind.QueryText);
            PlanCacheContentValue plan = Value(row.PlanState, row.Plan, SensitivePayloadKind.ExecutionPlan);
            bytes = checked(bytes + text.CiphertextBytes + plan.CiphertextBytes);
            if (!qualified.TryGetValue(row.Statement.CacheEntryFingerprint, out var statement)) { unqualified++; continue; }
            if (statement.Watermark.Key.Query != row.Statement.Query || statement.Watermark.Key.Plan != row.Statement.Plan
                || statement.Watermark.Counters != row.Statement.Counters)
                throw new InvalidDataException("Qualified cache content differs from its captured query, plan or counters.");
            content.Add(new(statement, text, plan));
        }
        if (bytes != source.ProtectedContentBytes || bytes > PlanCacheContentOutputBatch.MaximumCiphertextBytes)
            throw new InvalidDataException("Cache content protected-byte accounting differs from its payloads.");
        return new(runtime, source.ContentLimit, source.Content.Count, unqualified, source.SourceContentBytes, content, source.CaptureState);
    }

    private static PlanCacheContentValue Value(string state, ProtectedSensitivePayload? payload, SensitivePayloadKind kind)
    {
        if (state == "protected")
        {
            if (payload is null || payload.Kind != kind)
                throw new InvalidDataException("Protected cache content omitted its exact payload kind.");
            return PlanCacheContentValue.Protected(payload);
        }
        if (payload is not null) throw new InvalidDataException("Unavailable cache content contains a protected payload.");
        PlanCacheContentState result = state switch
        {
            "unavailable" => PlanCacheContentState.Unavailable,
            "oversized" => PlanCacheContentState.Oversized,
            "response_byte_limit" => PlanCacheContentState.ResponseByteLimit,
            "protection_unavailable" => PlanCacheContentState.ProtectionUnavailable,
            "protection_failed" => PlanCacheContentState.ProtectionFailed,
            _ => throw new InvalidDataException("Cache content state is not approved."),
        };
        return PlanCacheContentValue.Missing(kind, result);
    }
}
