using System.Globalization;

namespace SqlObserver.Domain.Collection;

/// <summary>Retained legacy facts. Availability means source accounting, never health or physical continuity.</summary>
public sealed record BackupAvailabilityWindowHistoryCoverage(int RunCount, int CompleteRunCount, int GapRunCount,
    int FailedRunCount, int LostRunCount, int MissingPayloadRunCount, int EmptyRunCount, int RetainedRowCount, bool HasVisibilityGap);
public sealed record BackupAvailabilityWindowHistoryReceipt(Guid RunId, long SampleTargetRevision, int CollectorVersion,
    int OutputSchemaVersion, DateTimeOffset CompletedAtUtc, DateTimeOffset? MonitorObservedAtUtc, string Outcome,
    string? LossKind, long? MinimumLostItems, int? SourceState, int? VisibilityScope, int ExpectedRowCount,
    int StoredRowCount, int StoredReplicaCount, int StoredDatabaseCount, string State, string Reason);
public sealed record BackupWindowHistoryItem(string DatabaseFingerprint, int Kind, string? BackupSetId,
    DateTimeOffset? LastFinishUtc, string? SourceLocalFinish, bool SourceTimeUnknown, string? SizeBytes,
    bool? CopyOnly, bool? HasChecksum, bool? IsDamaged, int Coverage, DateTimeOffset MonitorObservedAtUtc);
public sealed record AvailabilityReplicaWindowHistoryItem(string GroupFingerprint, string ReplicaFingerprint,
    string Role, string OperationalState, string ConnectedState, int VisibilityScope, bool StateAvailable, DateTimeOffset MonitorObservedAtUtc);
public sealed record AvailabilityDatabaseWindowHistoryItem(string GroupFingerprint, string DatabaseFingerprint,
    string SynchronizationState, string DatabaseState, int VisibilityScope, bool StateAvailable, DateTimeOffset MonitorObservedAtUtc);

public static class BackupAvailabilityWindowHistoryValidation
{
    public const long MaximumRevision = 9007199254740991;
    public const int MaximumRuns = 20480, MaximumRows = 250000, MaximumBackupRows = 1537, MaximumAvailabilityRows = 2048, MaximumPageSize = 100;
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(7), CursorLifetime = TimeSpan.FromMinutes(15);
    public static void Id(Guid value) { if (value == Guid.Empty) throw new ArgumentException("A nonempty identity is required."); }
    public static void Revision(long value) { if (value is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Utc(DateTimeOffset value) => DomainValidation.RequireUtcMicrosecondAligned(value, nameof(value));
    public static void Window(DateTimeOffset from, DateTimeOffset to) { Utc(from); Utc(to); if (from >= to || to - from > MaximumWindow) throw new ArgumentException("Invalid bounded window."); }
    public static void Digest(string value) { if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("Invalid opaque identity."); }
    public static long ExactNumber(string value) { if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 0 || n.ToString(CultureInfo.InvariantCulture) != value) throw new ArgumentException("Invalid exact Int64 fact."); return n; }
    public static void Kind(string kind) { if (kind is not ("backup" or "availability")) throw new ArgumentException("Unknown source kind."); }
    public static void Coverage(BackupAvailabilityWindowHistoryCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RunCount is < 0 or > MaximumRuns || value.CompleteRunCount < 0 || value.GapRunCount < 0 || value.CompleteRunCount + value.GapRunCount != value.RunCount ||
            value.FailedRunCount < 0 || value.FailedRunCount > value.GapRunCount || value.LostRunCount < 0 || value.LostRunCount > value.GapRunCount ||
            value.MissingPayloadRunCount < 0 || value.MissingPayloadRunCount > value.GapRunCount || value.EmptyRunCount < 0 || value.EmptyRunCount > value.CompleteRunCount ||
            value.RetainedRowCount is < 0 or > MaximumRows || value.GapRunCount > 0 && !value.HasVisibilityGap) throw new ArgumentException("Invalid source accounting.");
    }
    public static void Receipt(BackupAvailabilityWindowHistoryReceipt value, string kind, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, long revision)
    {
        ArgumentNullException.ThrowIfNull(value); Kind(kind); Id(value.RunId); Revision(value.SampleTargetRevision); Utc(value.CompletedAtUtc);
        int bound = kind == "backup" ? MaximumBackupRows : MaximumAvailabilityRows;
        if (value.SampleTargetRevision > revision || value.CompletedAtUtc < from || value.CompletedAtUtc >= to || value.CompletedAtUtc > cutoff || value.CollectorVersion < 1 || value.OutputSchemaVersion < 1 ||
            value.Outcome is not ("succeeded" or "partial" or "timed_out" or "transient_failure" or "permanent_failure" or "permission_denied" or "unsupported" or "output_invalid" or "lease_lost" or "circuit_open" ) ||
            value.LossKind is not (null or "none" or "source_row_limit" or "response_byte_limit" or "output_validation_failure" or "ingestion_rejection" or "blocking_graph_limit" or "duplicate_overlap" or "visibility_incomplete") || value.MinimumLostItems is < 0 or > int.MaxValue ||
            value.SourceState is < 1 or > 6 || value.VisibilityScope is < 1 or > 3 || value.ExpectedRowCount < 0 || value.StoredRowCount < 0 || value.StoredRowCount > bound ||
            value.StoredRowCount == 0 && value.MonitorObservedAtUtc is not null || value.StoredReplicaCount < 0 || value.StoredDatabaseCount < 0 || (kind == "availability" ? value.StoredReplicaCount + value.StoredDatabaseCount != value.StoredRowCount : value.StoredReplicaCount != 0 || value.StoredDatabaseCount != 0) ||
            value.State is not ("available" or "partial" or "unavailable") || value.Reason is not ("observed_rows" or "observed_empty" or "collection_failed" or "collection_loss" or "source_schema_unsupported" or "source_header_missing" or "payload_missing" or "stored_count_mismatch" or "source_clock_skew" or "source_partial")) throw new ArgumentException("Invalid retained source receipt.");
        if (value.MonitorObservedAtUtc is { } observed) Utc(observed);
        if (value.State == "available" && (value.CollectorVersion != 1 || value.OutputSchemaVersion != 1 || value.Outcome != "succeeded" || value.LossKind != "none" || value.MinimumLostItems != 0 ||
            value.SourceState is not (1 or 6) || value.SourceState == 6 && value.StoredRowCount != 0 || kind == "availability" && value.VisibilityScope is null || value.ExpectedRowCount != value.StoredRowCount ||
            value.Reason != (value.StoredRowCount == 0 ? "observed_empty" : "observed_rows") || value.StoredRowCount > 0 && (value.MonitorObservedAtUtc is null || value.MonitorObservedAtUtc > cutoff || (value.MonitorObservedAtUtc.Value - value.CompletedAtUtc).Duration() > TimeSpan.FromMinutes(5)))) throw new ArgumentException("Incomplete evidence was presented as available.");
        if (value.State != "available" && value.Reason is "observed_rows" or "observed_empty") throw new ArgumentException("Available reason requires complete evidence.");
    }
    public static void Backup(BackupWindowHistoryItem value)
    {
        ArgumentNullException.ThrowIfNull(value); Digest(value.DatabaseFingerprint); Utc(value.MonitorObservedAtUtc);
        if (value.Kind is < 1 or > 3 || value.Coverage is < 1 or > 4) throw new ArgumentException("Invalid backup enums.");
        if (value.BackupSetId is not null && ExactNumber(value.BackupSetId) == 0) throw new ArgumentException("Invalid backup set identity.");
        if (value.SizeBytes is not null) _ = ExactNumber(value.SizeBytes);
        if (value.LastFinishUtc is { } utc) Utc(utc);
        if (value.SourceLocalFinish is { } local && (!DateTime.TryParseExact(local, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed) || parsed.Kind != DateTimeKind.Unspecified)) throw new ArgumentException("Invalid offset-free backup source time.");
        if (value.SourceTimeUnknown != (value.LastFinishUtc is null && value.SourceLocalFinish is not null)) throw new ArgumentException("Backup source clock qualification mismatch.");
    }
    public static void Replica(AvailabilityReplicaWindowHistoryItem value)
    {
        ArgumentNullException.ThrowIfNull(value); Digest(value.GroupFingerprint); Digest(value.ReplicaFingerprint); Utc(value.MonitorObservedAtUtc);
        if (value.VisibilityScope is < 1 or > 3 || value.Role is not ("PRIMARY" or "SECONDARY" or "RESOLVING" or "unknown") ||
            value.OperationalState is not ("PENDING_FAILOVER" or "PENDING" or "ONLINE" or "OFFLINE" or "FAILED" or "FAILED_NO_QUORUM" or "unknown") || value.ConnectedState is not ("CONNECTED" or "DISCONNECTED" or "unknown")) throw new ArgumentException("Invalid reported replica state.");
    }
    public static void Database(AvailabilityDatabaseWindowHistoryItem value)
    {
        ArgumentNullException.ThrowIfNull(value); Digest(value.GroupFingerprint); Digest(value.DatabaseFingerprint); Utc(value.MonitorObservedAtUtc);
        if (value.VisibilityScope is < 1 or > 3 || value.SynchronizationState is not ("NOT SYNCHRONIZING" or "SYNCHRONIZING" or "SYNCHRONIZED" or "REVERTING" or "INITIALIZING" or "unknown") ||
            value.DatabaseState is not ("ONLINE" or "RESTORING" or "RECOVERING" or "RECOVERY_PENDING" or "SUSPECT" or "EMERGENCY" or "OFFLINE" or "unknown")) throw new ArgumentException("Invalid reported database state.");
    }
}
