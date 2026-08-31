using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

public interface IReplicationRowReader
{
    int FieldCount { get; }
    bool IsDBNull(int ordinal);
    byte[] GetBinary(int ordinal);
    int GetInt32(int ordinal);
    long GetInt64(int ordinal);
    bool GetBoolean(int ordinal);
    ValueTask<bool> ReadAsync(CancellationToken cancellationToken);
}

public sealed record ReplicationParseResult(ReplicationHealthSnapshot Snapshot, int Rows, int Items, int Bytes, CollectorLossEvidence Loss);

/// <summary>Converts the fixed SQL row shape to closed, typed and privacy-minimized evidence.</summary>
public static class SqlServerReplicationParser
{
    public static async ValueTask<ReplicationParseResult> ParseAsync(CollectorExecutionRequest request, IReplicationRowReader reader, bool registeredDistributionDatabase, IdentityFingerprintKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(key);
        // The SQL assets are a closed contract.  Accepting an extra column
        // would silently allow an unreviewed result shape to cross the
        // collector boundary (and makes ordinal parsing ambiguous).
        if (reader.FieldCount != 11) throw new InvalidDataException("Replication row shape is invalid; exactly 11 fields are required.");
        var items = new List<ReplicationObservation>();
        int rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            if (rows > ReplicationBounds.MaximumRows) continue;
            ReplicationTopologyState topology = ReadTopology(reader.GetInt32(0));
            ReplicationRole role = ReadRole(reader.GetInt32(1));
            string? publication = ReadFingerprint(reader, 2, key);
            string? subscription = ReadFingerprint(reader, 3, key);
            ReplicationStatus status = ReadStatus(reader.GetInt32(4));
            int? pending = reader.IsDBNull(5) ? null : ReadPending(reader.GetInt64(5));
            decimal? latency = reader.IsDBNull(6) ? null : ReadScaled(reader.GetInt32(6), ReplicationBounds.MaximumLatencySeconds);
            decimal? rate = reader.IsDBNull(7) ? null : ReadScaled(reader.GetInt32(7), ReplicationBounds.MaximumRatePerSecond);
            long? ageSeconds = reader.IsDBNull(8) ? null : ReadAge(reader.GetInt64(8));
            bool unknown = reader.IsDBNull(9) || reader.GetBoolean(9);
            ReplicationCoverage coverage = ReadCoverage(reader.GetInt32(10));
            if (!registeredDistributionDatabase)
            {
                // A discovered distribution name is never followed and cannot
                // promote local evidence to detailed evidence.
                publication = null;
                subscription = null;
                coverage = ReplicationCoverage.VisibilityGap;
            }
            bool lastSuccessUnknown = unknown || ageSeconds is null;
            var observation = new ReplicationObservation(request.TargetId, request.TargetRevision, topology, role, publication, subscription, status, pending, latency, rate, new ReplicationLastSuccessAge(lastSuccessUnknown ? null : TimeSpan.FromSeconds(ageSeconds!.Value), lastSuccessUnknown), coverage);
            if (coverage == ReplicationCoverage.VisibilityGap)
            {
                // Keep a stable opaque row identity even when both topology
                // names are unavailable. It is typed (target/revision/state/
                // ordinal), keyed, and domain-separated; raw names never enter.
                observation = observation with
                {
                    VisibilityGapFingerprint = ReplicationIdentityFingerprint.Gap(request.TargetId.Value, request.TargetRevision.Value, (int)topology, (int)role, items.Count, key)
                };
            }
            items.Add(observation);
        }
        if (items.Count == 0) throw new InvalidDataException("Replication collector returned no validated rows.");
        // A result can contain rows with different coverage (for example, a
        // detailed row alongside a row for which the distribution binding was
        // not visible). Summarize the least-complete coverage so the snapshot
        // cannot claim complete evidence based on whichever row happened to be
        // returned first.
        ReplicationCoverage snapshotCoverage = registeredDistributionDatabase
            ? SummarizeCoverage(items)
            : ReplicationCoverage.VisibilityGap;
        bool truncated = rows > ReplicationBounds.MaximumRows;
        bool degradedCoverage = snapshotCoverage != ReplicationCoverage.Complete;
        OperationalObservationState state = truncated
            ? OperationalObservationState.Partial
            : degradedCoverage
                ? OperationalObservationState.Degraded
                : OperationalObservationState.Complete;
        var snapshot = new ReplicationHealthSnapshot(request.TargetId, request.TargetRevision, request.RunId, DateTimeOffset.UtcNow, state, items, snapshotCoverage, rows, truncated);
        // Loss evidence has one closed kind, so row truncation takes
        // precedence when it co-occurs with a visibility gap. The snapshot
        // still retains the degraded coverage and Partial state in that case.
        var loss = truncated
            ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false)
            : degradedCoverage
                ? new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false)
                : CollectorLossEvidence.None;
        return new ReplicationParseResult(snapshot, rows, items.Count, checked(items.Count * 192), loss);
    }

    private static ReplicationCoverage SummarizeCoverage(IReadOnlyList<ReplicationObservation> items)
    {
        if (items.Any(static item => item.Coverage == ReplicationCoverage.VisibilityGap)) return ReplicationCoverage.VisibilityGap;
        if (items.Any(static item => item.Coverage == ReplicationCoverage.Unknown)) return ReplicationCoverage.Unknown;
        if (items.Any(static item => item.Coverage == ReplicationCoverage.LocalSummary)) return ReplicationCoverage.LocalSummary;
        return ReplicationCoverage.Complete;
    }

    private static string? ReadFingerprint(IReplicationRowReader reader, int ordinal, IdentityFingerprintKey key)
    {
        if (reader.IsDBNull(ordinal)) return null;
        byte[] bytes = reader.GetBinary(ordinal);
        if (bytes.Length != 32) throw new InvalidDataException("Replication fingerprint must be SHA-256.");
        // SQL Server supplies only an adapter-local digest. Re-key it before
        // it becomes a persisted/API identity, with a distinct domain per
        // field, so equal names cannot correlate across identity classes.
        string domain = ordinal == 2 ? "replication-publication" : "replication-subscription";
        return ReplicationIdentityFingerprint.FromTypedIdentity(domain, bytes, key);
    }
    private static int ReadPending(long value) => value is >= 0 and <= ReplicationBounds.MaximumPendingCommands ? checked((int)value) : throw new InvalidDataException("Replication pending command count is out of bounds.");
    private static decimal ReadScaled(int value, decimal maximum) { if (value < 0) throw new InvalidDataException("Replication metric cannot be negative."); decimal result = value / 1000m; return result <= maximum ? result : throw new InvalidDataException("Replication metric is out of bounds."); }
    private static long ReadAge(long value) => value is >= 0 and <= 315_576_000 ? value : throw new InvalidDataException("Replication last-success age is out of bounds.");
    private static ReplicationTopologyState ReadTopology(int value) => Enum.IsDefined((ReplicationTopologyState)value) ? (ReplicationTopologyState)value : throw new InvalidDataException("Replication topology state is invalid.");
    private static ReplicationRole ReadRole(int value) => Enum.IsDefined((ReplicationRole)value) ? (ReplicationRole)value : throw new InvalidDataException("Replication role is invalid.");
    private static ReplicationStatus ReadStatus(int value) => Enum.IsDefined((ReplicationStatus)value) ? (ReplicationStatus)value : throw new InvalidDataException("Replication status is invalid.");
    private static ReplicationCoverage ReadCoverage(int value) => Enum.IsDefined((ReplicationCoverage)value) ? (ReplicationCoverage)value : throw new InvalidDataException("Replication coverage is invalid.");
}
