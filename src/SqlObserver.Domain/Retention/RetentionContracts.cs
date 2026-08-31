namespace SqlObserver.Domain.Retention;

public enum RetentionExecutionState { Previewed = 1, Blocked = 2, Detached = 3, Grace = 4, Dropped = 5, Failed = 6, Retry = 7 }
public enum RetentionBlockReason { Disabled = 1, DurationUnconfigured = 2, RecoveryAttestationMissing = 3, ReaderLeaseActive = 4, BackfillIncomplete = 5, DependencyPending = 6, MinimumPartitionFloor = 7, UtcFloor = 8, RegistryMissing = 9, LeaseMissing = 10 }

/// <summary>Retention is deliberately disabled and duration-less until an administrator opts in.</summary>
public sealed record RetentionPolicy(string DataClass, bool Enabled = false, TimeSpan? RetainFor = null, int MinimumPartitionsToKeep = 3);
/* Validation is performed by RetentionPolicyValidator in the application boundary. */
/*
{
    public RetentionPolicy : this(DataClass, Enabled, RetainFor, MinimumPartitionsToKeep)
    {
        if (string.IsNullOrWhiteSpace(DataClass) || DataClass.Length > 128) throw new ArgumentException("Invalid data class.", nameof(DataClass));
        if (RetainFor is not null && RetainFor < TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(RetainFor));
        if (Enabled && RetainFor is null) throw new ArgumentException("An enabled policy must have a duration.", nameof(RetainFor));
        if (MinimumPartitionsToKeep < 1) throw new ArgumentOutOfRangeException(nameof(MinimumPartitionsToKeep));
    }
}; */

public sealed record RetentionPolicyHistory(string DataClass, bool Enabled, TimeSpan? RetainFor, DateTimeOffset ChangedAtUtc, string ChangedBy, string ChangeReason);
public sealed record RecoveryAttestation(Guid AttestationId, DateTimeOffset AttestedAtUtc, string AttestedBy, string BackupSetReference, DateTimeOffset ExpiresAtUtc, string DigestSha256, bool Valid = true);
/*
{
    public RecoveryAttestation : this(AttestationId, AttestedAtUtc, AttestedBy, BackupSetReference, ExpiresAtUtc, DigestSha256, Valid)
    {
        if (AttestationId == Guid.Empty || AttestedAtUtc.Offset != TimeSpan.Zero || ExpiresAtUtc <= AttestedAtUtc || ExpiresAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Invalid recovery attestation.");
        if (DigestSha256.Length != 64 || DigestSha256.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Attestation digest must be SHA-256.", nameof(DigestSha256));
    }
}; */

/// <summary>The row returned by <c>system.preview_m10_retention</c>.  Keep the
/// database column order explicit; this contract intentionally includes both
/// parent identifiers so callers cannot confuse rows from different parents.</summary>
public sealed record RetentionPreviewEntry(
    string DataClass,
    string ParentSchema,
    string ParentTable,
    string PartitionName,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    bool Eligible,
    string Reason)
{
    public static readonly IReadOnlySet<string> ClosedReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "retention_disabled", "duration_unconfigured", "partition_not_attached", "within_retention_window",
        "minimum_partition_floor", "reader_lease_active", "backfill_incomplete",
        "dependency_pending", "recovery_attestation_missing", "eligible"
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataClass) || DataClass.Length > 128 ||
            string.IsNullOrWhiteSpace(ParentSchema) || ParentSchema.Length > 128 ||
            string.IsNullOrWhiteSpace(ParentTable) || ParentTable.Length > 128 ||
            string.IsNullOrWhiteSpace(PartitionName) || PartitionName.Length > 128 ||
            new[] { DataClass, ParentSchema, ParentTable, PartitionName }.Any(static x => x.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))))
            throw new ArgumentException("Retention preview identifiers are invalid.");
        if (RangeStartUtc.Offset != TimeSpan.Zero || RangeEndUtc.Offset != TimeSpan.Zero || RangeEndUtc <= RangeStartUtc)
            throw new ArgumentException("Retention preview ranges must be ordered UTC intervals.");
        if (!ClosedReasons.Contains(Reason) || Eligible != (Reason == "eligible"))
            throw new ArgumentException("Retention eligibility and closed reason disagree.");
    }
}

/// <summary>Bounded, deterministic multi-row retention preview page.</summary>
public sealed record RetentionPreview(
    IReadOnlyList<RetentionPreviewEntry> Entries,
    bool Truncated,
    string? NextCursor,
    DateTimeOffset EvaluatedAtUtc)
{
    public RetentionPreview(IReadOnlyList<RetentionPreviewEntry> entries, DateTimeOffset evaluatedAtUtc)
        : this(entries, false, null, evaluatedAtUtc) { }

    public void Validate(int maximumEntries = 1024)
    {
        ArgumentNullException.ThrowIfNull(Entries);
        if (Entries.Count > maximumEntries || EvaluatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Retention preview page is outside its bounds.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (RetentionPreviewEntry entry in Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            entry.Validate();
            if (!identities.Add($"{entry.DataClass}\0{entry.ParentSchema}\0{entry.ParentTable}\0{entry.PartitionName}"))
                throw new ArgumentException("Retention preview entries must be distinct.");
        }
        if (!Truncated && NextCursor is not null || Truncated && string.IsNullOrWhiteSpace(NextCursor))
            throw new ArgumentException("Retention preview cursor and truncation state disagree.");
        if (NextCursor is not null && (NextCursor.Length > 1024 || NextCursor.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '|' or '/' or '+' or '='))))
            throw new ArgumentException("Retention preview cursor is invalid.");
    }
}
public sealed record RetentionExecutionResult(Guid ExecutionId, RetentionExecutionState State, RetentionBlockReason? BlockReason, DateTimeOffset? DropAfterUtc, string? FailureCode = null);

public sealed record RetentionPrerequisites(bool PolicyEnabled, bool DurationConfigured, bool RecoveryAttested, bool ReaderLeaseFree, bool BackfillComplete, bool DependenciesComplete, bool MinimumFloorSatisfied, bool UtcFloorSatisfied, bool RegistryPresent, bool LeaseOwned)
{
    public bool CanDetach => PolicyEnabled && DurationConfigured && RecoveryAttested && ReaderLeaseFree && BackfillComplete && DependenciesComplete && MinimumFloorSatisfied && UtcFloorSatisfied && RegistryPresent && LeaseOwned;
    public RetentionBlockReason? BlockingReason => !PolicyEnabled ? RetentionBlockReason.Disabled : !DurationConfigured ? RetentionBlockReason.DurationUnconfigured : !RecoveryAttested ? RetentionBlockReason.RecoveryAttestationMissing : !ReaderLeaseFree ? RetentionBlockReason.ReaderLeaseActive : !BackfillComplete ? RetentionBlockReason.BackfillIncomplete : !DependenciesComplete ? RetentionBlockReason.DependencyPending : !MinimumFloorSatisfied ? RetentionBlockReason.MinimumPartitionFloor : !UtcFloorSatisfied ? RetentionBlockReason.UtcFloor : !RegistryPresent ? RetentionBlockReason.RegistryMissing : !LeaseOwned ? RetentionBlockReason.LeaseMissing : null;
}

public static class RetentionPolicyValidator
{
    public static void Validate(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.DataClass) || policy.DataClass.Length > 128) throw new ArgumentException("Invalid data class.", nameof(policy));
        if (policy.RetainFor is not null && policy.RetainFor < TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.Enabled && policy.RetainFor is null) throw new ArgumentException("An enabled policy must have a duration.", nameof(policy));
        if (policy.MinimumPartitionsToKeep < 1) throw new ArgumentOutOfRangeException(nameof(policy));
    }
}
