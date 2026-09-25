using System.Collections.ObjectModel;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

/// <summary>A hard server-side timeout bound for one repository operation.</summary>
public sealed class RepositoryCallTimeout
{
    public static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan Maximum = TimeSpan.FromMinutes(30);

    public RepositoryCallTimeout(TimeSpan value)
    {
        if (value < Minimum || value > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"A repository call timeout must be between {Minimum} and {Maximum}.");
        }

        Value = value;
    }

    public TimeSpan Value { get; }
}

public sealed class MigrationApplyRequest
{
    public MigrationApplyRequest(
        int maxMigrations,
        RepositoryCallTimeout timeout)
    {
        if (maxMigrations is <= 0 or > MigrationBatchResult.MaximumResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMigrations),
                $"The request must permit between 1 and {MigrationBatchResult.MaximumResults} migrations.");
        }

        ArgumentNullException.ThrowIfNull(timeout);
        MaxMigrations = maxMigrations;
        Timeout = timeout;
    }

    public int MaxMigrations { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public interface IMigrationPort
{
    ValueTask<MigrationBatchResult> ApplyPendingAsync(
        MigrationApplyRequest request,
        CancellationToken cancellationToken);
}

public sealed record PostgreSqlVersion
{
    public PostgreSqlVersion(int major, int update)
    {
        if (major is <= 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        if (update is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(update));
        }

        Major = major;
        Update = update;
    }

    public int Major { get; }

    public int Update { get; }

    public override string ToString() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{Major}.{Update}");
}

public enum PostgreSqlCompatibilityStatus
{
    Compatible = 1,
    UnsupportedMajorVersion = 2,
    RequiredCapabilityMissing = 3,
}

public sealed class PostgreSqlCompatibilityResult
{
    public PostgreSqlCompatibilityResult(
        PostgreSqlVersion serverVersion,
        PostgreSqlCompatibilityStatus status,
        DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ServerVersion = serverVersion;
        Status = status;
        CheckedAtUtc = RequireUtc(checkedAtUtc, nameof(checkedAtUtc));
    }

    public PostgreSqlVersion ServerVersion { get; }

    public PostgreSqlCompatibilityStatus Status { get; }

    public DateTimeOffset CheckedAtUtc { get; }

    public bool IsCompatible => Status == PostgreSqlCompatibilityStatus.Compatible;

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", parameterName);
        }

        return value;
    }
}

public sealed class PostgreSqlCompatibilityRequest
{
    public PostgreSqlCompatibilityRequest(RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        Timeout = timeout;
    }

    public RepositoryCallTimeout Timeout { get; }
}

public interface IPostgreSqlCompatibilityPort
{
    ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(
        PostgreSqlCompatibilityRequest request,
        CancellationToken cancellationToken);
}

public sealed class PartitionCareRequest
{
    public const int MaximumPartitionsAhead = 31;

    public PartitionCareRequest(
        PartitionSetName setName,
        PartitionGranularity granularity,
        DateTimeOffset anchorUtc,
        int partitionsAhead,
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(setName);
        ArgumentNullException.ThrowIfNull(lease);

        if (!Enum.IsDefined(granularity))
        {
            throw new ArgumentOutOfRangeException(nameof(granularity));
        }

        if (anchorUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", nameof(anchorUtc));
        }

        if (partitionsAhead is < 0 or > MaximumPartitionsAhead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partitionsAhead),
                $"The request cannot create more than {MaximumPartitionsAhead} future partitions.");
        }

        ArgumentNullException.ThrowIfNull(timeout);
        SetName = setName;
        Granularity = granularity;
        AnchorUtc = anchorUtc;
        PartitionsAhead = partitionsAhead;
        Lease = lease;
        Timeout = timeout;
    }

    public PartitionSetName SetName { get; }

    public PartitionGranularity Granularity { get; }

    public DateTimeOffset AnchorUtc { get; }

    public int PartitionsAhead { get; }

    public WorkerLeaseIdentity Lease { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class PartitionCareResult
{
    public const int MaximumPartitions = PartitionCareRequest.MaximumPartitionsAhead + 1;

    private readonly ReadOnlyCollection<PartitionRange> _partitions;

    public PartitionCareResult(
        IReadOnlyList<PartitionRange> partitions,
        int createdCount,
        int existingCount,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(partitions);

        if (partitions.Count is 0 or > MaximumPartitions)
        {
            throw new ArgumentException(
                $"A partition-care result must contain between 1 and {MaximumPartitions} partitions.",
                nameof(partitions));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(createdCount);
        ArgumentOutOfRangeException.ThrowIfNegative(existingCount);

        if (checked(createdCount + existingCount) != partitions.Count)
        {
            throw new ArgumentException("Created and existing counts must account for every partition.");
        }

        if (completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", nameof(completedAtUtc));
        }

        var copy = new PartitionRange[partitions.Count];

        for (int index = 0; index < partitions.Count; index++)
        {
            copy[index] = partitions[index] ?? throw new ArgumentException(
                "A partition result cannot contain a null range.",
                nameof(partitions));
        }

        _partitions = Array.AsReadOnly(copy);
        CreatedCount = createdCount;
        ExistingCount = existingCount;
        CompletedAtUtc = completedAtUtc;
    }

    public IReadOnlyList<PartitionRange> Partitions => _partitions;

    public int CreatedCount { get; }

    public int ExistingCount { get; }

    public DateTimeOffset CompletedAtUtc { get; }
}

public sealed class RetentionPreviewRequest
{
    public const int MaximumEntries = 1_024;

    public RetentionPreviewRequest(
        PartitionSetName setName,
        DateTimeOffset cutoffUtc,
        int maxEntries,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(setName);

        if (cutoffUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", nameof(cutoffUtc));
        }

        if (maxEntries is <= 0 or > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries),
                $"A retention preview must request between 1 and {MaximumEntries} entries.");
        }

        ArgumentNullException.ThrowIfNull(timeout);
        SetName = setName;
        CutoffUtc = cutoffUtc;
        MaxEntries = maxEntries;
        Timeout = timeout;
    }

    public PartitionSetName SetName { get; }

    public DateTimeOffset CutoffUtc { get; }

    public int MaxEntries { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class RetentionPreview
{
    private readonly ReadOnlyCollection<RetentionPreviewEntry> _entries;

    public RetentionPreview(IReadOnlyList<RetentionPreviewEntry> entries, DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count > RetentionPreviewRequest.MaximumEntries)
        {
            throw new ArgumentException(
                $"A retention preview cannot contain more than {RetentionPreviewRequest.MaximumEntries} entries.",
                nameof(entries));
        }

        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", nameof(evaluatedAtUtc));
        }

        var copy = new RetentionPreviewEntry[entries.Count];

        for (int index = 0; index < entries.Count; index++)
        {
            copy[index] = entries[index] ?? throw new ArgumentException(
                "A retention preview cannot contain a null entry.",
                nameof(entries));
        }

        _entries = Array.AsReadOnly(copy);
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    public IReadOnlyList<RetentionPreviewEntry> Entries => _entries;

    public DateTimeOffset EvaluatedAtUtc { get; }
}

public interface IPartitionMaintenancePort
{
    ValueTask<PartitionCareResult> EnsurePartitionsAsync(
        PartitionCareRequest request,
        CancellationToken cancellationToken);

    ValueTask<RetentionPreview> PreviewRetentionAsync(
        RetentionPreviewRequest request,
        CancellationToken cancellationToken);
}

public sealed class TelemetryIngestionRequest
{
    public TelemetryIngestionRequest(
        TelemetryBatch batch,
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        Batch = batch;
        Lease = lease;
        Timeout = timeout;
    }

    public TelemetryBatch Batch { get; }

    public WorkerLeaseIdentity Lease { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public interface ITelemetryIngestionPort
{
    ValueTask<IngestionResult> IngestTelemetryAsync(
        TelemetryIngestionRequest request,
        CancellationToken cancellationToken);
}

public sealed class DiagnosticEventIngestionRequest
{
    public DiagnosticEventIngestionRequest(
        DiagnosticEventBatch batch,
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        Batch = batch;
        Lease = lease;
        Timeout = timeout;
    }

    public DiagnosticEventBatch Batch { get; }

    public WorkerLeaseIdentity Lease { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public interface IDiagnosticEventIngestionPort
{
    ValueTask<IngestionResult> IngestDiagnosticEventsAsync(
        DiagnosticEventIngestionRequest request,
        CancellationToken cancellationToken);
}

public sealed class SensitivePayloadGetOrAddRequest
{
    public SensitivePayloadGetOrAddRequest(
        MonitoredInstanceId targetId,
        ProtectedSensitivePayload payload,
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetId = targetId;
        Payload = payload;
        Lease = lease;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }

    public ProtectedSensitivePayload Payload { get; }

    public WorkerLeaseIdentity Lease { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public interface ISensitivePayloadPort
{
    ValueTask<SensitivePayloadReference> GetOrAddAsync(
        SensitivePayloadGetOrAddRequest request,
        CancellationToken cancellationToken);
}

public sealed class AcquireWorkerLeaseRequest
{
    public AcquireWorkerLeaseRequest(
        WorkerLeaseKey key,
        WorkerExecutionId owner,
        WorkerLeaseDuration duration,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(duration);
        ArgumentNullException.ThrowIfNull(timeout);
        Key = key;
        Owner = owner;
        Duration = duration;
        Timeout = timeout;
    }

    public WorkerLeaseKey Key { get; }

    public WorkerExecutionId Owner { get; }

    public WorkerLeaseDuration Duration { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class RenewWorkerLeaseRequest
{
    public RenewWorkerLeaseRequest(
        WorkerLeaseIdentity identity,
        WorkerLeaseDuration duration,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(duration);
        ArgumentNullException.ThrowIfNull(timeout);
        Identity = identity;
        Duration = duration;
        Timeout = timeout;
    }

    public WorkerLeaseIdentity Identity { get; }

    public WorkerLeaseDuration Duration { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ReleaseWorkerLeaseRequest
{
    public ReleaseWorkerLeaseRequest(WorkerLeaseIdentity identity, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(timeout);
        Identity = identity;
        Timeout = timeout;
    }

    public WorkerLeaseIdentity Identity { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class AssertWorkerLeaseRequest
{
    public AssertWorkerLeaseRequest(WorkerLeaseIdentity identity, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(timeout);
        Identity = identity;
        Timeout = timeout;
    }

    public WorkerLeaseIdentity Identity { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public interface IWorkerLeasePort
{
    ValueTask<LeaseAcquisitionResult> AcquireAsync(
        AcquireWorkerLeaseRequest request,
        CancellationToken cancellationToken);

    ValueTask<LeaseRenewalResult> RenewAsync(
        RenewWorkerLeaseRequest request,
        CancellationToken cancellationToken);

    ValueTask<LeaseReleaseStatus> ReleaseAsync(
        ReleaseWorkerLeaseRequest request,
        CancellationToken cancellationToken);

    ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(
        AssertWorkerLeaseRequest request,
        CancellationToken cancellationToken);
}
