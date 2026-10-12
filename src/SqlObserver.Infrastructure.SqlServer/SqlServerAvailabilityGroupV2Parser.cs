using System.Globalization;
using System.Text;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

public sealed record AvailabilityGroupV2ParseResult(AvailabilityGroupsV2Snapshot Snapshot,
    int ResponseBytes, CollectorLossEvidence Loss);

/// <summary>Reads the exact staged AG v2 shape in ordinal order without retaining source identities.</summary>
public static class SqlServerAvailabilityGroupV2Parser
{
    public static async ValueTask<AvailabilityGroupV2ParseResult> ParseAsync(
        CollectorExecutionRequest request, IOperationalHealthRowReader reader,
        IdentityFingerprintKey key, DateTimeOffset observedAtUtc, CancellationToken cancellationToken,
        int maximumRows = OperationalHealthBounds.AvailabilityMaximumRows,
        int maximumResponseBytes = 2_097_152)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumRows is < 1 or > OperationalHealthBounds.AvailabilityMaximumRows ||
            maximumResponseBytes is < 256 or > 2_097_152)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (observedAtUtc.Offset != TimeSpan.Zero || observedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw new ArgumentException("AG observation time must be UTC and microsecond aligned.", nameof(observedAtUtc));
        if (reader.FieldCount != 20)
            throw new InvalidDataException("AG v2 source requires exactly 20 fields.");

        var replicas = new List<AvailabilityReplicaEvidence>();
        var databases = new List<AvailabilityDatabaseReplicaEvidence>();
        int rows = 0, responseBytes = 0, outputBytes = 256;
        string? previousSortKey = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            if (rows > maximumRows)
                return Result(truncated: true, new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false));

            if (reader.IsDBNull(0)) throw new InvalidDataException("AG v2 row kind is missing.");
            int kind = reader.GetInt32(0);
            if (kind is not (0 or 1)) throw new InvalidDataException("AG v2 row kind is invalid.");
            byte[] group = ReadDigest(reader, 1) ?? throw new InvalidDataException("AG group identity is missing.");
            byte[] replica = ReadDigest(reader, 2) ?? throw new InvalidDataException("AG replica identity is missing.");
            byte[]? database = ReadDigest(reader, 3);
            byte[]? observer = ReadDigest(reader, 4);
            bool? local = ReadBoolean(reader, 5);
            if (reader.IsDBNull(6)) throw new InvalidDataException("AG visibility scope is missing.");
            var scope = (AvailabilityVisibilityScope)reader.GetInt16(6);
            if (reader.IsDBNull(7)) throw new InvalidDataException("AG observed-state marker is missing.");
            bool observed = reader.GetBoolean(7);
            string? role = ReadState(reader, 8), operational = ReadState(reader, 9), connected = ReadState(reader, 10);
            string? synchronization = ReadState(reader, 11), health = ReadState(reader, 12), databaseState = ReadState(reader, 13);
            bool? suspended = ReadBoolean(reader, 14);
            long? sendQueue = ReadMetric(reader, 15), sendRate = ReadMetric(reader, 16);
            long? redoQueue = ReadMetric(reader, 17), redoRate = ReadMetric(reader, 18);
            DateTime? lastCommit = ReadLocalTime(reader, 19);

            string sortKey = string.Concat(kind.ToString(CultureInfo.InvariantCulture),
                Convert.ToHexString(group), Convert.ToHexString(replica),
                database is null ? "" : Convert.ToHexString(database));
            if (previousSortKey is not null && StringComparer.Ordinal.Compare(previousSortKey, sortKey) >= 0)
                throw new InvalidDataException("AG v2 identities are duplicated or unordered.");
            previousSortKey = sortKey;

            AvailabilityReplicaEvidenceIdentity identity;
            try
            {
                identity = new AvailabilityReplicaEvidenceIdentity(Fingerprint("group", group), Fingerprint("replica", replica),
                    observer is null ? null : Fingerprint("replica", observer), local, scope);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("AG v2 observing-replica identity is inconsistent.");
            }
            int rowBytes = 224 + (role?.Length ?? 0) + (operational?.Length ?? 0) + (connected?.Length ?? 0) +
                (synchronization?.Length ?? 0) + (health?.Length ?? 0) + (databaseState?.Length ?? 0);
            int itemBytes;
            if (kind == 0)
            {
                if (database is not null || synchronization is not null || health is not null || databaseState is not null ||
                    suspended is not null || sendQueue is not null || sendRate is not null || redoQueue is not null ||
                    redoRate is not null || lastCommit is not null)
                    throw new InvalidDataException("AG replica row carries database evidence.");
                var item = new AvailabilityReplicaEvidence(identity, observed, role, operational, connected);
                replicas.Add(item);
                itemBytes = item.EstimatedSizeBytes;
            }
            else
            {
                if (database is null || role is not null || operational is not null || connected is not null)
                    throw new InvalidDataException("AG database row has an invalid stream shape.");
                var item = new AvailabilityDatabaseReplicaEvidence(identity, Fingerprint("database", database), observed,
                    synchronization, health, databaseState, suspended, sendQueue, sendRate, redoQueue, redoRate, lastCommit);
                databases.Add(item);
                itemBytes = item.EstimatedSizeBytes;
            }
            if (responseBytes + rowBytes > maximumResponseBytes || outputBytes + itemBytes > maximumResponseBytes)
            {
                // A byte-truncated scan publishes no prefix. All read rows are
                // disclosed as withheld, so missing databases cannot look healthy.
                replicas.Clear();
                databases.Clear();
                return Result(truncated: true,
                    new CollectorLossEvidence(CollectorLossKind.ResponseByteLimit, rows, false));
            }
            responseBytes += rowBytes;
            outputBytes += itemBytes;
        }
        AvailabilityGroupV2ParseResult complete = Result(truncated: false, CollectorLossEvidence.None);
        return complete.Snapshot.State == OperationalObservationState.Degraded
            ? complete with { Loss = new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false) }
            : complete;

        string Fingerprint(string domain, byte[] source) => ReplicationIdentityFingerprint.FromTypedIdentity(
            "ag-v2-" + domain, Encoding.UTF8.GetBytes(string.Concat(request.TargetId.Value.ToString("D"), "|",
                request.TargetRevision.Value.ToString(CultureInfo.InvariantCulture), "|", Convert.ToHexString(source))), key);

        AvailabilityGroupV2ParseResult Result(bool truncated, CollectorLossEvidence loss)
        {
            try
            {
                var snapshot = new AvailabilityGroupsV2Snapshot(request.TargetId,
                    request.TargetRevision, request.RunId, observedAtUtc, replicas, databases, rows, truncated);
                // Match the operational collector's conservative response accounting:
                // fingerprint expansion and the snapshot envelope must also fit the cap.
                return new AvailabilityGroupV2ParseResult(snapshot,
                    Math.Max(responseBytes, snapshot.EstimatedSizeBytes), loss);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("AG v2 scan identities or state evidence are inconsistent.");
            }
        }
    }

    private static byte[]? ReadDigest(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        byte[] value = reader.GetBinary(ordinal);
        if (value.Length != 32) throw new InvalidDataException("AG source identity must be SHA-256.");
        return value;
    }

    private static bool? ReadBoolean(IOperationalHealthRowReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static long? ReadMetric(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        long value = reader.GetInt64(ordinal);
        return value >= 0 ? value : throw new InvalidDataException("AG queue or rate cannot be negative.");
    }

    private static DateTime? ReadLocalTime(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        DateTime value = reader.GetDateTime(ordinal);
        if (value.Kind != DateTimeKind.Unspecified || value.Year < 1753)
            throw new InvalidDataException("AG source commit time must be offset-free SQL datetime.");
        return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Unspecified);
    }

    private static string? ReadState(IOperationalHealthRowReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string value = reader.GetString(ordinal);
        bool valid = ordinal switch
        {
            8 => value is "PRIMARY" or "SECONDARY" or "RESOLVING",
            9 => value is "PENDING_FAILOVER" or "PENDING" or "ONLINE" or "OFFLINE" or "FAILED" or "FAILED_NO_QUORUM",
            10 => value is "CONNECTED" or "DISCONNECTED",
            11 => value is "NOT SYNCHRONIZING" or "SYNCHRONIZING" or "SYNCHRONIZED" or "REVERTING" or "INITIALIZING",
            12 => value is "NOT_HEALTHY" or "PARTIALLY_HEALTHY" or "HEALTHY",
            13 => value is "ONLINE" or "RESTORING" or "RECOVERING" or "RECOVERY_PENDING" or "SUSPECT" or "EMERGENCY" or "OFFLINE",
            _ => false,
        };
        return valid ? value : throw new InvalidDataException("AG source state is outside its closed contract.");
    }
}
