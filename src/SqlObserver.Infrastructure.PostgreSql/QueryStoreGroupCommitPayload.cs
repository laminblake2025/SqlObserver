using System.Globalization;
using System.Text.Json;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Canonical, opaque-only input for the fenced Query Store group commit.</summary>
internal static class QueryStoreGroupCommitPayload
{
    internal const int MaximumSerializedBytes = 8 * 1024 * 1024;

    internal static byte[] Serialize(QueryStoreGroupOutputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("targetId", batch.TargetId.Value);
            writer.WriteNumber("targetRevision", batch.TargetRevision.Value);
            writer.WriteString("windowStartUtc", batch.WindowStartUtc.UtcDateTime);
            writer.WriteString("windowEndUtc", batch.WindowEndUtc.UtcDateTime);
            writer.WriteBoolean("runtimeComplete", batch.RuntimeComplete);
            writer.WriteString("waitCoverage", batch.WaitCoverage switch
            {
                QueryStoreGroupWaitCoverage.Complete => "complete",
                QueryStoreGroupWaitCoverage.CaptureOff => "capture_off",
                QueryStoreGroupWaitCoverage.Unavailable => "unavailable",
                QueryStoreGroupWaitCoverage.Truncated => "truncated",
                _ => throw new InvalidDataException("Query Store wait coverage is not recognized."),
            });
            writer.WriteStartArray("items");
            foreach (QueryStoreGroupObservation group in batch.Items
                .OrderBy(static item => item.Runtime.Key.Query.DatabaseId)
                .ThenBy(static item => item.Runtime.Key.DatabaseIncarnation)
                .ThenBy(static item => item.Runtime.Key.Query.QueryFingerprint, StringComparer.Ordinal)
                .ThenBy(static item => item.Runtime.Key.Plan.PlanFingerprint, StringComparer.Ordinal)
                .ThenBy(static item => item.Runtime.Key.PlanInitialCompileUtc)
                .ThenBy(static item => item.Runtime.Key.SourceIntervalId)
                .ThenBy(static item => item.Runtime.Key.IntervalStartUtc)
                .ThenBy(static item => item.Runtime.Key.IntervalEndUtc)
                .ThenBy(static item => item.Runtime.Key.ExecutionType))
                WriteGroup(writer, group);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Bound(buffer);
    }

    internal static byte[] SerializeFailure(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, string outcome, string reason)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (string.IsNullOrWhiteSpace(outcome) || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A failed group commit requires a bounded outcome and reason.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("targetId", targetId.Value);
            writer.WriteNumber("targetRevision", targetRevision.Value);
            writer.WriteString("outcome", outcome);
            writer.WriteString("reason", reason);
            writer.WriteStartArray("items");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Bound(buffer);
    }

    private static void WriteGroup(Utf8JsonWriter writer, QueryStoreGroupObservation group)
    {
        QueryStoreRuntimeWatermark runtime = group.Runtime;
        QueryStoreRuntimeWatermarkKey key = runtime.Key;
        QueryStoreRuntimeCounters counters = runtime.Counters;
        writer.WriteStartObject();
        writer.WriteNumber("databaseId", key.Query.DatabaseId);
        writer.WriteString("databaseIncarnation", key.DatabaseIncarnation);
        writer.WriteString("queryFingerprint", key.Query.QueryFingerprint);
        writer.WriteString("planFingerprint", key.Plan.PlanFingerprint);
        writer.WriteString("planInitialCompileUtc", key.PlanInitialCompileUtc.UtcDateTime);
        writer.WriteNumber("sourceIntervalId", key.SourceIntervalId);
        writer.WriteString("intervalStartUtc", key.IntervalStartUtc.UtcDateTime);
        writer.WriteString("intervalEndUtc", key.IntervalEndUtc.UtcDateTime);
        writer.WriteNumber("executionType", key.ExecutionType);
        writer.WriteNumber("cpuMs", counters.CpuMilliseconds);
        writer.WriteNumber("durationMs", counters.DurationMilliseconds);
        writer.WriteNumber("executionCount", counters.Executions);
        writer.WriteNumber("logicalReads", counters.LogicalReads);
        writer.WriteNumber("writes", counters.Writes);
        writer.WriteNumber("rowsProcessed", counters.Rows);
        writer.WriteString("firstExecutionUtc", runtime.FirstExecutionUtc.UtcDateTime);
        writer.WriteString("lastExecutionUtc", runtime.LastExecutionUtc.UtcDateTime);
        writer.WriteString("runtimeObservedUtc", runtime.ObservedAtUtc.UtcDateTime);
        if (group.Waits is { } waits)
        {
            writer.WriteStartObject("waitCategories");
            foreach ((int category, long milliseconds) in waits.CategoryMilliseconds.OrderBy(static item => item.Key))
                writer.WriteNumber(category.ToString(CultureInfo.InvariantCulture), milliseconds);
            writer.WriteEndObject();
            writer.WriteString("waitObservedUtc", waits.ObservedAtUtc.UtcDateTime);
        }
        writer.WriteEndObject();
    }

    private static byte[] Bound(MemoryStream buffer)
    {
        if (buffer.Length > MaximumSerializedBytes)
            throw new InvalidDataException("Query Store group commit payload exceeds its bounded JSON contract.");
        return buffer.ToArray();
    }
}
