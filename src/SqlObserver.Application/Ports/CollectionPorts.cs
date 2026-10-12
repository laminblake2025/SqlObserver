using System.Collections.ObjectModel;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record CollectorSha256Digest
{
    public CollectorSha256Digest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A collector asset digest must be exactly 64 lowercase hexadecimal SHA-256 characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public byte[] ToByteArray() => Convert.FromHexString(Value);

    public override string ToString() => Value;
}

public sealed record CollectorScheduleRevision
{
    public CollectorScheduleRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

        Value = value;
    }

    public long Value { get; }
}

public sealed class CollectorCatalogEntry
{
    public CollectorCatalogEntry(
        int executionOrder,
        CollectorManifest manifest,
        CollectorSha256Digest manifestDigest,
        CollectorSha256Digest assetBundleDigest)
    {
        if (executionOrder is <= 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(executionOrder));
        }

        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifestDigest);
        ArgumentNullException.ThrowIfNull(assetBundleDigest);
        ExecutionOrder = executionOrder;
        Manifest = manifest;
        ManifestDigest = manifestDigest;
        AssetBundleDigest = assetBundleDigest;
    }

    public int ExecutionOrder { get; }
    public CollectorManifest Manifest { get; }
    public CollectorSha256Digest ManifestDigest { get; }
    public CollectorSha256Digest AssetBundleDigest { get; }
}

public sealed class ReconcileCollectorCatalogRequest
{
    public const int MaximumEntries = 64;
    private readonly ReadOnlyCollection<CollectorCatalogEntry> _entries;

    public ReconcileCollectorCatalogRequest(
        IReadOnlyList<CollectorCatalogEntry> entries,
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        if (entries.Count is 0 or > MaximumEntries)
        {
            throw new ArgumentException(
                $"A collector catalog must contain between 1 and {MaximumEntries} entries.",
                nameof(entries));
        }

        var copy = new CollectorCatalogEntry[entries.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        for (int index = 0; index < entries.Count; index++)
        {
            CollectorCatalogEntry entry = entries[index] ?? throw new ArgumentException(
                "A collector catalog cannot contain null entries.", nameof(entries));
            if (!ids.Add(entry.Manifest.Id.Value) || !orders.Add(entry.ExecutionOrder))
            {
                throw new ArgumentException("Collector catalog identities and execution orders must be unique.", nameof(entries));
            }

            copy[index] = entry;
        }

        _entries = Array.AsReadOnly(copy);
        Lease = lease;
        Timeout = timeout;
    }

    public IReadOnlyList<CollectorCatalogEntry> Entries => _entries;
    public WorkerLeaseIdentity Lease { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class CollectorCatalogReconcileResult
{
    public CollectorCatalogReconcileResult(
        int insertedCount,
        int updatedCount,
        int unchangedCount,
        DateTimeOffset repositoryTimeUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(insertedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(updatedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(unchangedCount);
        if (checked(insertedCount + updatedCount + unchangedCount) is <= 0 or > ReconcileCollectorCatalogRequest.MaximumEntries)
        {
            throw new ArgumentException("Catalog reconciliation counts are outside the bounded request size.");
        }

        InsertedCount = insertedCount;
        UpdatedCount = updatedCount;
        UnchangedCount = unchangedCount;
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public int InsertedCount { get; }
    public int UpdatedCount { get; }
    public int UnchangedCount { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public sealed class ListDueCollectorWorkRequest
{
    public const int MaximumItems = 16;

    public ListDueCollectorWorkRequest(int maxItems, RepositoryCallTimeout timeout)
    {
        if (maxItems is <= 0 or > MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems));
        }

        ArgumentNullException.ThrowIfNull(timeout);
        MaxItems = maxItems;
        Timeout = timeout;
    }

    public int MaxItems { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed record ClaimDueCollectorWorkRequest(
    WorkerExecutionId Owner,
    WorkerLeaseDuration LeaseDuration,
    RepositoryCallTimeout Timeout);

public sealed record CollectorClaimedWork(
    CollectorDueWorkItem Work,
    WorkerLease Lease,
    DateTimeOffset LeaseRepositoryTimeUtc);

public sealed class CollectorDueWorkItem
{
    public CollectorDueWorkItem(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        SqlServerConnectionPolicy connectionPolicy,
        CollectorId collectorId,
        int collectorManifestVersion,
        int outputSchemaVersion,
        CollectorScheduleRevision scheduleRevision,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset repositoryTimeUtc,
        CollectorCircuitSnapshot circuit,
        CapabilityProfile? capabilityProfile)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(scheduleRevision);
        ArgumentNullException.ThrowIfNull(circuit);
        if (collectorManifestVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(collectorManifestVersion));
        }

        if (outputSchemaVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSchemaVersion));
        }

        scheduledAtUtc = CollectionPortValidation.RequireUtc(scheduledAtUtc, nameof(scheduledAtUtc));
        repositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
        if (scheduledAtUtc > repositoryTimeUtc)
        {
            throw new ArgumentException("Due work cannot be scheduled after repository time.", nameof(scheduledAtUtc));
        }

        if (circuit.RepositoryTimeUtc != repositoryTimeUtc)
        {
            throw new ArgumentException("Circuit and due-work repository timestamps must match.", nameof(circuit));
        }

        if (capabilityProfile is not null &&
            (capabilityProfile.TargetId != targetId || capabilityProfile.TargetRevision != targetRevision))
        {
            throw new ArgumentException("A due-work capability profile must match the exact target revision.", nameof(capabilityProfile));
        }

        TargetId = targetId;
        TargetRevision = targetRevision;
        ConnectionPolicy = connectionPolicy;
        CollectorId = collectorId;
        CollectorManifestVersion = collectorManifestVersion;
        OutputSchemaVersion = outputSchemaVersion;
        ScheduleRevision = scheduleRevision;
        ScheduledAtUtc = scheduledAtUtc;
        RepositoryTimeUtc = repositoryTimeUtc;
        Circuit = circuit;
        CapabilityProfile = capabilityProfile;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public SqlServerConnectionPolicy ConnectionPolicy { get; }
    public CollectorId CollectorId { get; }
    public int CollectorManifestVersion { get; }
    public int OutputSchemaVersion { get; }
    public CollectorScheduleRevision ScheduleRevision { get; }
    public DateTimeOffset ScheduledAtUtc { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
    public CollectorCircuitSnapshot Circuit { get; }
    public CapabilityProfile? CapabilityProfile { get; }
}

public sealed class CollectorDueWorkBatch
{
    private readonly ReadOnlyCollection<CollectorDueWorkItem> _items;

    public CollectorDueWorkBatch(IReadOnlyList<CollectorDueWorkItem> items, bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > ListDueCollectorWorkRequest.MaximumItems)
        {
            throw new ArgumentException("A due-work batch exceeds its hard item limit.", nameof(items));
        }

        var copy = new CollectorDueWorkItem[items.Count];
        var keys = new HashSet<(Guid TargetId, string CollectorId)>();
        for (int index = 0; index < items.Count; index++)
        {
            CollectorDueWorkItem item = items[index] ?? throw new ArgumentException(
                "A due-work batch cannot contain null items.", nameof(items));
            if (!keys.Add((item.TargetId.Value, item.CollectorId.Value)))
            {
                throw new ArgumentException("A due-work batch cannot duplicate a target/collector key.", nameof(items));
            }

            copy[index] = item;
        }

        _items = Array.AsReadOnly(copy);
        HasMore = hasMore;
    }

    public IReadOnlyList<CollectorDueWorkItem> Items => _items;
    public bool HasMore { get; }
}

public sealed class CommitCollectorRunRequest
{
    public CommitCollectorRunRequest(
        CollectorDueWorkItem work,
        CollectorRunSummary summary,
        CollectorPayload payload,
        CollectorCircuitSnapshot nextCircuit,
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(nextCircuit);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        if (summary.TargetId != work.TargetId ||
            summary.TargetRevision != work.TargetRevision ||
            summary.CollectorId != work.CollectorId ||
            summary.CollectorManifestVersion != work.CollectorManifestVersion ||
            summary.OutputSchemaVersion != work.OutputSchemaVersion)
        {
            throw new ArgumentException("Collector summary and due work must describe the same contract revision.", nameof(summary));
        }

        bool payloadAccountingMatches =
            summary.Accounting.OutputItemsProduced == payload.ItemCount &&
            summary.Accounting.OutputBytes == payload.EstimatedSizeBytes;
        bool rejectedOutputWasAccounted =
            summary.Outcome == CollectorRunOutcome.OutputInvalid &&
            summary.Reason == CollectorRunReason.OutputValidationFailed &&
            summary.Loss.Kind == CollectorLossKind.OutputValidationFailure &&
            payload.ItemCount == 0 &&
            payload.EstimatedSizeBytes == 0;
        if (!payloadAccountingMatches && !rejectedOutputWasAccounted)
        {
            throw new ArgumentException(
                "Collector summary and commit payload accounting must match unless invalid output was explicitly rejected.",
                nameof(payload));
        }

        Work = work;
        Summary = summary;
        Payload = payload;
        NextCircuit = nextCircuit;
        Lease = lease;
        Timeout = timeout;
    }

    public CollectorDueWorkItem Work { get; }
    public CollectorRunSummary Summary { get; }
    public CollectorPayload Payload { get; }
    public CollectorCircuitSnapshot NextCircuit { get; }
    public WorkerLeaseIdentity Lease { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class BeginCollectorRunRequest
{
    public BeginCollectorRunRequest(
        CollectorDueWorkItem work,
        CollectorRunId runId,
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);

        Work = work;
        RunId = runId;
        Lease = lease;
        Timeout = timeout;
    }

    public CollectorDueWorkItem Work { get; }
    public CollectorRunId RunId { get; }
    public WorkerLeaseIdentity Lease { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public enum CollectorRunStartStatus
{
    Started = 1,
    RunningReplay = 2,
    CommittedReplay = 3,
    TargetNotFound = 4,
    TargetInactive = 5,
    TargetRevisionConflict = 6,
    ScheduleConflict = 7,
    LeaseLost = 8,
}

public sealed class CollectorRunStartResult
{
    public CollectorRunStartResult(
        CollectorRunStartStatus status,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset repositoryTimeUtc)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        bool started = status is CollectorRunStartStatus.Started or
            CollectorRunStartStatus.RunningReplay or
            CollectorRunStartStatus.CommittedReplay;
        if (started != (startedAtUtc is not null))
        {
            throw new ArgumentException("Only an accepted run start carries its start timestamp.", nameof(startedAtUtc));
        }

        Status = status;
        StartedAtUtc = startedAtUtc is null
            ? null
            : CollectionPortValidation.RequireUtc(startedAtUtc.Value, nameof(startedAtUtc));
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public CollectorRunStartStatus Status { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public enum CollectorRunCommitStatus
{
    Committed = 1,
    Replayed = 2,
    TargetNotFound = 3,
    TargetInactive = 4,
    TargetRevisionConflict = 5,
    ScheduleConflict = 6,
    LeaseLost = 7,
}

public sealed class CollectorRunCommitResult
{
    public CollectorRunCommitResult(
        CollectorRunCommitStatus status,
        int insertedCount,
        int duplicateCount,
        int rejectedCount,
        int persistedBytes,
        DateTimeOffset? committedAtUtc)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(insertedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(rejectedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(persistedBytes);
        bool committed = status is CollectorRunCommitStatus.Committed or CollectorRunCommitStatus.Replayed;
        if (committed != (committedAtUtc is not null))
        {
            throw new ArgumentException("Only a committed or replayed run carries a repository timestamp.", nameof(committedAtUtc));
        }

        if (!committed && (insertedCount != 0 || duplicateCount != 0 || rejectedCount != 0 || persistedBytes != 0))
        {
            throw new ArgumentException("A rejected collector commit cannot report persisted output.");
        }

        Status = status;
        InsertedCount = insertedCount;
        DuplicateCount = duplicateCount;
        RejectedCount = rejectedCount;
        PersistedBytes = persistedBytes;
        CommittedAtUtc = committedAtUtc is null
            ? null
            : CollectionPortValidation.RequireUtc(committedAtUtc.Value, nameof(committedAtUtc));
    }

    public CollectorRunCommitStatus Status { get; }
    public int InsertedCount { get; }
    public int DuplicateCount { get; }
    public int RejectedCount { get; }
    public int PersistedBytes { get; }
    public DateTimeOffset? CommittedAtUtc { get; }
}

public interface ICollectorRuntimeRepositoryPort
{
    ValueTask<CollectorCatalogReconcileResult> ReconcileCatalogAsync(
        ReconcileCollectorCatalogRequest request,
        CancellationToken cancellationToken);

    ValueTask<CollectorDueWorkBatch> ListDueAsync(
        ListDueCollectorWorkRequest request,
        CancellationToken cancellationToken);

    ValueTask<CollectorClaimedWork?> ClaimDueAsync(
        ClaimDueCollectorWorkRequest request,
        CancellationToken cancellationToken);

    ValueTask<CollectorRunStartResult> BeginRunAsync(
        BeginCollectorRunRequest request,
        CancellationToken cancellationToken);

    ValueTask<CollectorRunCommitResult> CommitRunAsync(
        CommitCollectorRunRequest request,
        CancellationToken cancellationToken);
}

public enum CollectorHealthState
{
    Pending = 1,
    Current = 2,
    Degraded = 3,
    Unavailable = 4,
    Unsupported = 5,
    Stale = 6,
    Disabled = 7,
}

public enum CollectorHealthReason
{
    None = 1,
    NeverCollected = 2,
    CapabilityProfileMissing = 3,
    CapabilityProfileStale = 4,
    CapabilityMissing = 5,
    PermissionDenied = 6,
    VersionUnsupported = 7,
    PlatformUnsupported = 8,
    EditionUnsupported = 9,
    TimedOut = 10,
    CollectionFailed = 11,
    OutputInvalid = 12,
    SampleLoss = 13,
    CircuitOpen = 14,
    EvidenceStale = 15,
}

public sealed class CollectorHealthProjection
{
    public CollectorHealthProjection(
        MonitoredInstanceId targetId,
        CollectorId collectorId,
        int collectorManifestVersion,
        int outputSchemaVersion,
        CollectorHealthState state,
        CollectorHealthReason reason,
        CollectorCircuitSnapshot circuit,
        CollectorRunSummary? latestRun,
        int insertedCount,
        int duplicateCount,
        int rejectedCount,
        int persistedBytes,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset? lastAttemptAtUtc,
        DateTimeOffset? lastSuccessAtUtc,
        DateTimeOffset nextDueAtUtc,
        DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(circuit);
        if (collectorManifestVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion ||
            outputSchemaVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(collectorManifestVersion));
        }

        if (!Enum.IsDefined(state) || !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(insertedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(rejectedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(persistedBytes);
        scheduledAtUtc = CollectionPortValidation.RequireUtc(scheduledAtUtc, nameof(scheduledAtUtc));
        nextDueAtUtc = CollectionPortValidation.RequireUtc(nextDueAtUtc, nameof(nextDueAtUtc));
        repositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
        if (lastAttemptAtUtc is not null)
        {
            lastAttemptAtUtc = CollectionPortValidation.RequireUtc(lastAttemptAtUtc.Value, nameof(lastAttemptAtUtc));
        }

        if (lastSuccessAtUtc is not null)
        {
            lastSuccessAtUtc = CollectionPortValidation.RequireUtc(lastSuccessAtUtc.Value, nameof(lastSuccessAtUtc));
        }

        if (latestRun is not null &&
            (latestRun.TargetId != targetId || latestRun.CollectorId != collectorId))
        {
            throw new ArgumentException("The latest run must belong to this health projection.", nameof(latestRun));
        }

        TargetId = targetId;
        CollectorId = collectorId;
        CollectorManifestVersion = collectorManifestVersion;
        OutputSchemaVersion = outputSchemaVersion;
        State = state;
        Reason = reason;
        Circuit = circuit;
        LatestRun = latestRun;
        InsertedCount = insertedCount;
        DuplicateCount = duplicateCount;
        RejectedCount = rejectedCount;
        PersistedBytes = persistedBytes;
        ScheduledAtUtc = scheduledAtUtc;
        LastAttemptAtUtc = lastAttemptAtUtc;
        LastSuccessAtUtc = lastSuccessAtUtc;
        NextDueAtUtc = nextDueAtUtc;
        RepositoryTimeUtc = repositoryTimeUtc;
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorId CollectorId { get; }
    public int CollectorManifestVersion { get; }
    public int OutputSchemaVersion { get; }
    public CollectorHealthState State { get; }
    public CollectorHealthReason Reason { get; }
    public CollectorCircuitSnapshot Circuit { get; }
    public CollectorRunSummary? LatestRun { get; }
    public int InsertedCount { get; }
    public int DuplicateCount { get; }
    public int RejectedCount { get; }
    public int PersistedBytes { get; }
    public DateTimeOffset ScheduledAtUtc { get; }
    public DateTimeOffset? LastAttemptAtUtc { get; }
    public DateTimeOffset? LastSuccessAtUtc { get; }
    public DateTimeOffset NextDueAtUtc { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public sealed class GetInstanceHealthRepositoryRequest
{
    public GetInstanceHealthRepositoryRequest(MonitoredInstanceId targetId, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetId = targetId;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class InstanceHealthProjection
{
    private readonly ReadOnlyCollection<MetricSample> _coreMetrics;

    public InstanceHealthProjection(
        MonitoredInstanceId targetId,
        CollectorHealthProjection coreCollector,
        IReadOnlyList<MetricSample> coreMetrics,
        DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(coreCollector);
        ArgumentNullException.ThrowIfNull(coreMetrics);
        if (coreCollector.TargetId != targetId || coreMetrics.Count > 64 ||
            coreMetrics.Any(metric => metric.InstanceId != targetId))
        {
            throw new ArgumentException("Instance health evidence must be bounded and belong to the requested target.");
        }

        TargetId = targetId;
        CoreCollector = coreCollector;
        _coreMetrics = Array.AsReadOnly(coreMetrics.ToArray());
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorHealthProjection CoreCollector { get; }
    public IReadOnlyList<MetricSample> CoreMetrics => _coreMetrics;
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public sealed record DatabaseHealthCursor
{
    public DatabaseHealthCursor(
        MonitoredInstanceId targetId,
        CollectorRunId snapshotRunId,
        ObservationTargetRevision snapshotTargetRevision,
        int databaseId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(snapshotRunId);
        ArgumentNullException.ThrowIfNull(snapshotTargetRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(databaseId);

        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        DatabaseId = databaseId;
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId SnapshotRunId { get; }
    public ObservationTargetRevision SnapshotTargetRevision { get; }
    public int DatabaseId { get; }
}

public sealed record DatabaseFileHealthCursor
{
    public DatabaseFileHealthCursor(
        MonitoredInstanceId targetId,
        CollectorRunId snapshotRunId,
        ObservationTargetRevision snapshotTargetRevision,
        int databaseId,
        int fileId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(snapshotRunId);
        ArgumentNullException.ThrowIfNull(snapshotTargetRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(databaseId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);

        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        DatabaseId = databaseId;
        FileId = fileId;
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId SnapshotRunId { get; }
    public ObservationTargetRevision SnapshotTargetRevision { get; }
    public int DatabaseId { get; }
    public int FileId { get; }
}

public sealed class ListDatabaseHealthRepositoryRequest
{
    public const int MaximumResults = 100;

    public ListDatabaseHealthRepositoryRequest(
        MonitoredInstanceId targetId,
        int maxResults,
        DatabaseHealthCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        if (maxResults is <= 0 or > MaximumResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        if (cursor is not null && cursor.TargetId != targetId)
        {
            throw new ArgumentException("A database-health cursor must belong to the requested target.", nameof(cursor));
        }

        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public DatabaseHealthCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListDatabaseFileHealthRepositoryRequest
{
    public const int MaximumResults = 100;

    public ListDatabaseFileHealthRepositoryRequest(
        MonitoredInstanceId targetId,
        int maxResults,
        DatabaseFileHealthCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        if (maxResults is <= 0 or > MaximumResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        if (cursor is not null && cursor.TargetId != targetId)
        {
            throw new ArgumentException("A database-file-health cursor must belong to the requested target.", nameof(cursor));
        }

        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public DatabaseFileHealthCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class DatabaseHealthItem
{
    public DatabaseHealthItem(DatabaseObservation observation, CollectorHealthProjection collector)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(collector);
        if (observation.TargetId != collector.TargetId)
        {
            throw new ArgumentException("Database and collector health evidence must belong to the same target.");
        }

        Observation = observation;
        Collector = collector;
    }

    public DatabaseObservation Observation { get; }
    public CollectorHealthProjection Collector { get; }
}

public sealed class DatabaseFileHealthItem
{
    public DatabaseFileHealthItem(DatabaseFileObservation observation, CollectorHealthProjection collector)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(collector);
        if (observation.TargetId != collector.TargetId)
        {
            throw new ArgumentException("Database-file and collector health evidence must belong to the same target.");
        }

        Observation = observation;
        Collector = collector;
    }

    public DatabaseFileObservation Observation { get; }
    public CollectorHealthProjection Collector { get; }
}

public sealed class DatabaseHealthPage
{
    private readonly ReadOnlyCollection<DatabaseHealthItem> _items;

    public DatabaseHealthPage(
        MonitoredInstanceId targetId,
        CollectorRunId? snapshotRunId,
        ObservationTargetRevision? snapshotTargetRevision,
        CollectorHealthProjection collector,
        IReadOnlyList<DatabaseHealthItem> items,
        DatabaseHealthCursor? nextCursor,
        DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(items);
        bool hasSnapshotIdentity = snapshotRunId is not null && snapshotTargetRevision is not null;
        if (collector.TargetId != targetId ||
            items.Count > ListDatabaseHealthRepositoryRequest.MaximumResults ||
            items.Any(item => item is null || item.Observation.TargetId != targetId) ||
            ((snapshotRunId is null) != (snapshotTargetRevision is null)) ||
            (items.Count > 0 && !hasSnapshotIdentity) ||
            (hasSnapshotIdentity && items.Any(item =>
                item.Observation.TargetRevision != snapshotTargetRevision)) ||
            (nextCursor is not null &&
                (!hasSnapshotIdentity ||
                 nextCursor.TargetId != targetId ||
                 nextCursor.SnapshotRunId != snapshotRunId ||
                 nextCursor.SnapshotTargetRevision != snapshotTargetRevision)))
        {
            throw new ArgumentException("A database health page must be bounded and target-consistent.", nameof(items));
        }

        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        Collector = collector;
        _items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId? SnapshotRunId { get; }
    public ObservationTargetRevision? SnapshotTargetRevision { get; }
    public CollectorHealthProjection Collector { get; }
    public IReadOnlyList<DatabaseHealthItem> Items => _items;
    public DatabaseHealthCursor? NextCursor { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public sealed class DatabaseFileHealthPage
{
    private readonly ReadOnlyCollection<DatabaseFileHealthItem> _items;

    public DatabaseFileHealthPage(
        MonitoredInstanceId targetId,
        CollectorRunId? snapshotRunId,
        ObservationTargetRevision? snapshotTargetRevision,
        CollectorHealthProjection collector,
        IReadOnlyList<DatabaseFileHealthItem> items,
        DatabaseFileHealthCursor? nextCursor,
        DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(items);
        bool hasSnapshotIdentity = snapshotRunId is not null && snapshotTargetRevision is not null;
        if (collector.TargetId != targetId ||
            items.Count > ListDatabaseFileHealthRepositoryRequest.MaximumResults ||
            items.Any(item => item is null || item.Observation.TargetId != targetId) ||
            ((snapshotRunId is null) != (snapshotTargetRevision is null)) ||
            (items.Count > 0 && !hasSnapshotIdentity) ||
            (hasSnapshotIdentity && items.Any(item =>
                item.Observation.TargetRevision != snapshotTargetRevision)) ||
            (nextCursor is not null &&
                (!hasSnapshotIdentity ||
                 nextCursor.TargetId != targetId ||
                 nextCursor.SnapshotRunId != snapshotRunId ||
                 nextCursor.SnapshotTargetRevision != snapshotTargetRevision)))
        {
            throw new ArgumentException("A database-file health page must be bounded and target-consistent.", nameof(items));
        }

        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        Collector = collector;
        _items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId? SnapshotRunId { get; }
    public ObservationTargetRevision? SnapshotTargetRevision { get; }
    public CollectorHealthProjection Collector { get; }
    public IReadOnlyList<DatabaseFileHealthItem> Items => _items;
    public DatabaseFileHealthCursor? NextCursor { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public interface IHealthProjectionRepositoryPort
{
    ValueTask<InstanceHealthProjection?> GetInstanceHealthAsync(
        GetInstanceHealthRepositoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<DatabaseHealthPage?> ListDatabaseHealthAsync(
        ListDatabaseHealthRepositoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<DatabaseFileHealthPage?> ListDatabaseFileHealthAsync(
        ListDatabaseFileHealthRepositoryRequest request,
        CancellationToken cancellationToken);
}

internal static class CollectionPortValidation
{
    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentException("Repository timestamps must be UTC and microsecond-aligned.", parameterName);
        }

        return value;
    }
}
