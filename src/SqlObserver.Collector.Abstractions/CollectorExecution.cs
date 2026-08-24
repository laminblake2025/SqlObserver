using System.Collections.ObjectModel;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collector.Abstractions;

public sealed record CollectorAttemptNumber
{
    public const int Maximum = 2;

    public CollectorAttemptNumber(int value)
    {
        if (value is <= 0 or > Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public int Value { get; }
}

/// <summary>The remaining portion of the original collector deadline, never a reset deadline.</summary>
public sealed record CollectorExecutionTimeout
{
    public CollectorExecutionTimeout(TimeSpan value)
    {
        if (value < CollectorExecutionLimits.MinimumTimeout ||
            value > CollectorExecutionLimits.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public TimeSpan Value { get; }
}

public sealed class CollectorExecutionRequest
{
    public CollectorExecutionRequest(
        CollectorRunId runId,
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        SqlServerConnectionPolicy connectionPolicy,
        CapabilityProfile capabilityProfile,
        CollectorAttemptNumber attempt,
        CollectorExecutionTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        ArgumentNullException.ThrowIfNull(capabilityProfile);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(timeout);
        if (capabilityProfile.TargetId != targetId || capabilityProfile.TargetRevision != targetRevision)
        {
            throw new ArgumentException(
                "Collector execution requires a capability profile for the exact target revision.",
                nameof(capabilityProfile));
        }

        RunId = runId;
        TargetId = targetId;
        TargetRevision = targetRevision;
        ConnectionPolicy = connectionPolicy;
        CapabilityProfile = capabilityProfile;
        Attempt = attempt;
        Timeout = timeout;
    }

    public CollectorRunId RunId { get; }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public SqlServerConnectionPolicy ConnectionPolicy { get; }
    public CapabilityProfile CapabilityProfile { get; }
    public CollectorAttemptNumber Attempt { get; }
    public CollectorExecutionTimeout Timeout { get; }
}

public sealed class CollectorPayload
{
    private readonly ReadOnlyCollection<MetricSample> _metrics;

    public CollectorPayload(
        IReadOnlyList<MetricSample>? metrics = null,
        DatabaseObservationBatch? databases = null,
        DatabaseFileObservationBatch? databaseFiles = null,
        ActivitySessionObservationBatch? activitySessions = null,
        ActivityRequestObservationBatch? activityRequests = null,
        ServerWaitObservationBatch? serverWaits = null,
        BlockingEdgeObservationBatch? blockingEdges = null)
    {
        metrics ??= Array.Empty<MetricSample>();
        if (metrics.Count > IngestionLimits.MaximumItemCount)
        {
            throw new ArgumentException(
                $"A collector payload cannot contain more than {IngestionLimits.MaximumItemCount} metric samples.",
                nameof(metrics));
        }

        var metricCopy = new MetricSample[metrics.Count];
        for (int index = 0; index < metrics.Count; index++)
        {
            metricCopy[index] = metrics[index] ?? throw new ArgumentException(
                "A collector payload cannot contain null metric samples.",
                nameof(metrics));
        }

        _metrics = Array.AsReadOnly(metricCopy);
        Databases = databases ?? new DatabaseObservationBatch([]);
        DatabaseFiles = databaseFiles ?? new DatabaseFileObservationBatch([]);
        ActivitySessions = activitySessions ?? new ActivitySessionObservationBatch([]);
        ActivityRequests = activityRequests ?? new ActivityRequestObservationBatch([]);
        ServerWaits = serverWaits ?? new ServerWaitObservationBatch([]);
        BlockingEdges = blockingEdges ?? new BlockingEdgeObservationBatch([]);
    }

    public IReadOnlyList<MetricSample> Metrics => _metrics;
    public DatabaseObservationBatch Databases { get; }
    public DatabaseFileObservationBatch DatabaseFiles { get; }
    public ActivitySessionObservationBatch ActivitySessions { get; }
    public ActivityRequestObservationBatch ActivityRequests { get; }
    public ServerWaitObservationBatch ServerWaits { get; }
    public BlockingEdgeObservationBatch BlockingEdges { get; }
    public int ItemCount => checked(
        Metrics.Count +
        Databases.Items.Count +
        DatabaseFiles.Items.Count +
        ActivitySessions.Items.Count +
        ActivityRequests.Items.Count +
        ServerWaits.Items.Count +
        BlockingEdges.Items.Count);
    public int EstimatedSizeBytes => checked(
        Metrics.Sum(static item => item.EstimatedSizeBytes) +
        Databases.Items.Sum(static item => item.EstimatedSizeBytes) +
        DatabaseFiles.Items.Sum(static item => item.EstimatedSizeBytes) +
        ActivitySessions.Items.Sum(static item => item.EstimatedSizeBytes) +
        ActivityRequests.Items.Sum(static item => item.EstimatedSizeBytes) +
        ServerWaits.Items.Sum(static item => item.EstimatedSizeBytes) +
        BlockingEdges.Items.Sum(static item => item.EstimatedSizeBytes));

    public static CollectorPayload Empty { get; } = new();
}

/// <summary>A provider-safe attempt result. Provider exceptions and unrestricted text are excluded.</summary>
public sealed class CollectorExecutionResult
{
    public CollectorExecutionResult(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        CollectorId collectorId,
        int collectorManifestVersion,
        int outputSchemaVersion,
        CollectorRunOutcome outcome,
        CollectorRunReason reason,
        CollectorPayload payload,
        CollectorRunAccounting accounting,
        CollectorLossEvidence loss)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(accounting);
        ArgumentNullException.ThrowIfNull(loss);
        if (collectorManifestVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(collectorManifestVersion));
        }

        if (outputSchemaVersion is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSchemaVersion));
        }

        if (!Enum.IsDefined(outcome) || !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        bool payloadAccountingMatches =
            accounting.OutputItemsProduced == payload.ItemCount &&
            accounting.OutputBytes == payload.EstimatedSizeBytes;
        bool rejectedOutputWasAccounted =
            outcome == CollectorRunOutcome.OutputInvalid &&
            reason == CollectorRunReason.OutputValidationFailed &&
            loss.Kind == CollectorLossKind.OutputValidationFailure &&
            payload.ItemCount == 0 &&
            payload.EstimatedSizeBytes == 0;
        if (!payloadAccountingMatches && !rejectedOutputWasAccounted)
        {
            throw new ArgumentException(
                "Collector payload and output accounting must match unless invalid output was explicitly rejected.",
                nameof(accounting));
        }

        if (outcome == CollectorRunOutcome.Succeeded && (reason != CollectorRunReason.Completed || loss.HasLoss))
        {
            throw new ArgumentException("A successful collector result must be complete and loss-free.");
        }

        if (outcome == CollectorRunOutcome.Partial && !loss.HasLoss)
        {
            throw new ArgumentException("A partial collector result must expose sample loss.", nameof(loss));
        }

        if (outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial) && payload.ItemCount != 0)
        {
            throw new ArgumentException("A failed collector result cannot carry persistable output.", nameof(payload));
        }

        TargetId = targetId;
        TargetRevision = targetRevision;
        CollectorId = collectorId;
        CollectorManifestVersion = collectorManifestVersion;
        OutputSchemaVersion = outputSchemaVersion;
        Outcome = outcome;
        Reason = reason;
        Payload = payload;
        Accounting = accounting;
        Loss = loss;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorId CollectorId { get; }
    public int CollectorManifestVersion { get; }
    public int OutputSchemaVersion { get; }
    public CollectorRunOutcome Outcome { get; }
    public CollectorRunReason Reason { get; }
    public CollectorPayload Payload { get; }
    public CollectorRunAccounting Accounting { get; }
    public CollectorLossEvidence Loss { get; }
}

public sealed class CollectorMetricOutputContract
{
    private readonly ReadOnlyCollection<string> _allowedDimensionKeys;

    public CollectorMetricOutputContract(MetricId metricId, IReadOnlyList<string> allowedDimensionKeys)
    {
        ArgumentNullException.ThrowIfNull(metricId);
        ArgumentNullException.ThrowIfNull(allowedDimensionKeys);
        if (allowedDimensionKeys.Count > MetricSample.MaximumDimensionCount)
        {
            throw new ArgumentException("A metric output contract declares too many dimensions.", nameof(allowedDimensionKeys));
        }

        var keys = new string[allowedDimensionKeys.Count];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < allowedDimensionKeys.Count; index++)
        {
            string key = allowedDimensionKeys[index] ?? throw new ArgumentException(
                "A metric output contract cannot contain null dimension keys.",
                nameof(allowedDimensionKeys));
            _ = new MetricDimension(key, "contract");
            if (!unique.Add(key))
            {
                throw new ArgumentException("Metric output dimension keys must be unique.", nameof(allowedDimensionKeys));
            }

            keys[index] = key;
        }

        MetricId = metricId;
        _allowedDimensionKeys = Array.AsReadOnly(keys);
    }

    public MetricId MetricId { get; }
    public IReadOnlyList<string> AllowedDimensionKeys => _allowedDimensionKeys;
}

public sealed class CollectorOutputContract
{
    private readonly ReadOnlyCollection<CollectorMetricOutputContract> _metrics;

    public CollectorOutputContract(
        CollectorOutputSchemaVersion schemaVersion,
        IReadOnlyList<CollectorMetricOutputContract> metrics,
        int maxMetricSamples,
        int maxDatabaseObservations,
        int maxDatabaseFileObservations,
        int maxActivitySessionObservations = 0,
        int maxActivityRequestObservations = 0,
        int maxServerWaitObservations = 0,
        int maxBlockingEdgeObservations = 0)
    {
        ArgumentNullException.ThrowIfNull(schemaVersion);
        ArgumentNullException.ThrowIfNull(metrics);
        if (maxMetricSamples is < 0 or > IngestionLimits.MaximumItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMetricSamples));
        }

        if (maxDatabaseObservations is < 0 or > DatabaseObservationBatch.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDatabaseObservations));
        }

        if (maxDatabaseFileObservations is < 0 or > DatabaseFileObservationBatch.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDatabaseFileObservations));
        }

        if (maxActivitySessionObservations is < 0 or > ActivitySessionObservationBatch.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActivitySessionObservations));
        }

        if (maxActivityRequestObservations is < 0 or > ActivityRequestObservationBatch.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActivityRequestObservations));
        }

        if (maxServerWaitObservations is < 0 or > ServerWaitObservationBatch.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxServerWaitObservations));
        }

        if (maxBlockingEdgeObservations is < 0 or > BlockingEdgeObservationBatch.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBlockingEdgeObservations));
        }

        var copy = new CollectorMetricOutputContract[metrics.Count];
        var metricIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < metrics.Count; index++)
        {
            CollectorMetricOutputContract metric = metrics[index] ?? throw new ArgumentException(
                "A collector output contract cannot contain null metric declarations.",
                nameof(metrics));
            if (!metricIds.Add(metric.MetricId.Value))
            {
                throw new ArgumentException("Collector output metric identifiers must be unique.", nameof(metrics));
            }

            copy[index] = metric;
        }

        if ((maxMetricSamples == 0) != (copy.Length == 0))
        {
            throw new ArgumentException("Metric declarations and the metric-sample limit must either both be empty or both be present.");
        }

        SchemaVersion = schemaVersion;
        _metrics = Array.AsReadOnly(copy);
        MaxMetricSamples = maxMetricSamples;
        MaxDatabaseObservations = maxDatabaseObservations;
        MaxDatabaseFileObservations = maxDatabaseFileObservations;
        MaxActivitySessionObservations = maxActivitySessionObservations;
        MaxActivityRequestObservations = maxActivityRequestObservations;
        MaxServerWaitObservations = maxServerWaitObservations;
        MaxBlockingEdgeObservations = maxBlockingEdgeObservations;
    }

    public CollectorOutputSchemaVersion SchemaVersion { get; }
    public IReadOnlyList<CollectorMetricOutputContract> Metrics => _metrics;
    public int MaxMetricSamples { get; }
    public int MaxDatabaseObservations { get; }
    public int MaxDatabaseFileObservations { get; }
    public int MaxActivitySessionObservations { get; }
    public int MaxActivityRequestObservations { get; }
    public int MaxServerWaitObservations { get; }
    public int MaxBlockingEdgeObservations { get; }
}

public interface ICollectorOutputValidator
{
    CollectorOutputContract Contract { get; }

    void Validate(
        CollectorManifest manifest,
        CollectorExecutionRequest request,
        CollectorExecutionResult result);
}

/// <summary>A fixed, passive SQL Server collector selected through capability evidence.</summary>
public interface ISqlServerCollector
{
    CollectorManifest Manifest { get; }

    ValueTask<CollectorExecutionResult> CollectAsync(
        CollectorExecutionRequest request,
        CancellationToken cancellationToken);
}
