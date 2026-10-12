using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

public sealed record ServerLifecycleParseResult(ServerLifecycleSnapshot Snapshot,
    int ResponseBytes, CollectorLossEvidence Loss);

/// <summary>Exact bounded source shape; private source names never enter the output receipt.</summary>
public static class SqlServerLifecycleParser
{
    public static async ValueTask<ServerLifecycleParseResult> ParseAsync(
        CollectorExecutionRequest request, IOperationalHealthRowReader reader,
        IdentityFingerprintKey key, CancellationToken cancellationToken,
        int maximumRows = SqlServerLifecycleCollector.MaximumRows,
        int maximumResponseBytes = SqlServerLifecycleCollector.MaximumResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumRows is < 1 or > SqlServerLifecycleCollector.MaximumRows ||
            maximumResponseBytes is < 512 or > SqlServerLifecycleCollector.MaximumResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (reader.FieldCount != 17)
            throw new InvalidDataException("Lifecycle source requires exactly 17 fields.");

        EngineRow? header = null;
        var members = new List<ServerLifecycleAgMemberEvidence>();
        string? previousKey = null;
        int rows = 0, responseBytes = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows++;
            if (rows > maximumRows)
                return Result(true, new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false));
            if (reader.IsDBNull(0)) throw new InvalidDataException("Lifecycle row kind is missing.");
            int kind = reader.GetInt32(0);
            if (kind is not (0 or 1)) throw new InvalidDataException("Lifecycle row kind is invalid.");
            EngineRow engine = ReadEngine(reader);
            Guid? group = Id(reader, 9), replica = Id(reader, 10), observer = Id(reader, 11);
            string? role = State(reader, 12), observerRole = State(reader, 13);
            bool? stateObserved = Boolean(reader, 14);
            int? expectedMembers = Integer(reader, 15);
            if (reader.IsDBNull(16) || reader.GetInt32(16) != ServerLifecycleSnapshot.CurrentSourceSchemaVersion)
                throw new InvalidDataException("Lifecycle source schema is not supported.");
            if (kind == 0)
            {
                if (header is not null || rows != 1 || group is not null || replica is not null || observer is not null ||
                    role is not null || observerRole is not null || stateObserved is not null || expectedMembers is not null)
                    throw new InvalidDataException("Lifecycle engine header is missing, repeated or carries AG evidence.");
                header = engine;
            }
            else
            {
                if (header is null || header != engine || engine.HadrEnabled == false ||
                    group is null || replica is null || stateObserved is null)
                    throw new InvalidDataException("Lifecycle AG row does not belong to its engine receipt.");
                string sortKey = string.Concat(group.Value.ToString("D"), replica.Value.ToString("D"));
                if (previousKey is not null && StringComparer.Ordinal.Compare(previousKey, sortKey) >= 0)
                    throw new InvalidDataException("Lifecycle membership is duplicated or unordered.");
                previousKey = sortKey;
                try
                {
                    members.Add(new ServerLifecycleAgMemberEvidence(
                        ServerLifecycleIdentityFingerprint.AvailabilityGroup(group.Value, key),
                        ServerLifecycleIdentityFingerprint.AvailabilityReplica(replica.Value, key),
                        observer is null ? null : ServerLifecycleIdentityFingerprint.AvailabilityReplica(observer.Value, key),
                        role, observerRole, stateObserved.Value, expectedMembers));
                }
                catch (ArgumentException)
                { throw new InvalidDataException("Lifecycle membership evidence is inconsistent."); }
            }
            // The pinned SQL casts names to 128 characters and states to 60;
            // conservative UTF-16 widths cover wire bytes and opaque expansion.
            int rowBytes = 256 + 2 * ((engine.PhysicalNode?.Length ?? 0) + (engine.LogicalInstance?.Length ?? 0) +
                (role?.Length ?? 0) + (observerRole?.Length ?? 0));
            if (responseBytes + rowBytes > maximumResponseBytes || 512 + members.Count * 512 > maximumResponseBytes)
            {
                if (kind == 0) throw new InvalidDataException("Lifecycle engine receipt exceeds its byte bound.");
                members.Clear();
                return Result(true, new CollectorLossEvidence(CollectorLossKind.ResponseByteLimit, rows - 1, false));
            }
            responseBytes += rowBytes;
        }
        if (header is null) throw new InvalidDataException("Lifecycle engine receipt is missing.");
        ServerLifecycleParseResult complete = Result(false, CollectorLossEvidence.None);
        return complete.Snapshot.EngineCoverage != ServerLifecycleCoverage.Complete ||
            complete.Snapshot.AvailabilityCoverage is ServerLifecycleCoverage.Partial or ServerLifecycleCoverage.Unknown
            ? complete with { Loss = new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false) }
            : complete;

        ServerLifecycleParseResult Result(bool truncated, CollectorLossEvidence loss)
        {
            if (header is null) throw new InvalidDataException("Lifecycle engine receipt is missing.");
            try
            {
                var evidence = new ServerLifecycleEngineEvidence(
                    header.PhysicalNode is null ? null : ServerLifecycleIdentityFingerprint.PhysicalNode(header.PhysicalNode, key),
                    header.LogicalInstance is null ? null : ServerLifecycleIdentityFingerprint.LogicalInstance(header.LogicalInstance, key),
                    header.ProcessId, header.SourceStartLocal, header.StartMsTicks, header.CurrentMsTicks);
                var snapshot = new ServerLifecycleSnapshot(request.TargetId, request.TargetRevision,
                    request.RunId, header.SourceObservedAtUtc, evidence, header.HadrEnabled, members, rows, truncated);
                return new ServerLifecycleParseResult(snapshot,
                    Math.Max(responseBytes, snapshot.EstimatedSizeBytes), loss);
            }
            catch (ArgumentException)
            { throw new InvalidDataException("Lifecycle source receipt is inconsistent."); }
        }
    }

    private sealed record EngineRow(DateTimeOffset SourceObservedAtUtc, string? PhysicalNode,
        string? LogicalInstance, int? ProcessId, DateTime? SourceStartLocal, long? StartMsTicks,
        long? CurrentMsTicks, bool? HadrEnabled);

    private static EngineRow ReadEngine(IOperationalHealthRowReader reader)
    {
        if (reader.IsDBNull(1)) throw new InvalidDataException("Lifecycle source observation UTC is missing.");
        DateTime sourceUtc = reader.GetDateTime(1);
        if (sourceUtc.Kind == DateTimeKind.Local || sourceUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw new InvalidDataException("Lifecycle source observation must be microsecond UTC.");
        var observedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(sourceUtc, DateTimeKind.Utc));
        string? physical = Name(reader, 2), logical = Name(reader, 3);
        int? pid = Integer(reader, 4);
        DateTime? startLocal = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
        long? startTicks = Long(reader, 6), currentTicks = Long(reader, 7);
        bool? hadr = Boolean(reader, 8);
        if (pid is <= 0 || startTicks < 0 || currentTicks < 0 ||
            startLocal is { } start && (start.Kind != DateTimeKind.Unspecified || start.Year < 1753))
            throw new InvalidDataException("Lifecycle source startup evidence is invalid.");
        return new EngineRow(observedAtUtc, physical, logical, pid, startLocal, startTicks, currentTicks, hadr);
    }

    private static string? Name(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string value = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Trim() != value || value.Any(char.IsControl))
            throw new InvalidDataException("Lifecycle SQL identity is invalid.");
        return value;
    }
    private static string? State(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string value = reader.GetString(ordinal);
        if (value is not ("PRIMARY" or "SECONDARY" or "RESOLVING"))
            throw new InvalidDataException("Lifecycle role is invalid.");
        return value;
    }
    private static Guid? Id(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        Guid value = reader.GetGuid(ordinal);
        if (value == Guid.Empty) throw new InvalidDataException("Lifecycle GUID is empty.");
        return value;
    }
    private static bool? Boolean(IOperationalHealthRowReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    private static int? Integer(IOperationalHealthRowReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? Long(IOperationalHealthRowReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
