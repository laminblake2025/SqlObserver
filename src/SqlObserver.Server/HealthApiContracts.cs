namespace SqlObserver.Server;

/// <summary>A safe, target-scoped health snapshot evaluated using repository time.</summary>
public sealed record TargetHealthResponse(
    Guid InstanceId,
    string State,
    DateTimeOffset RepositoryTimeUtc,
    IReadOnlyList<CollectorHealthResponse> Collectors,
    IReadOnlyList<CoreMetricResponse> CoreMetrics);

/// <summary>A bounded normalized core-engine metric with non-sensitive dimensions.</summary>
public sealed record CoreMetricResponse(
    Guid SampleId,
    string MetricId,
    DateTimeOffset ObservedAtUtc,
    double Value,
    IReadOnlyList<MetricDimensionResponse> Dimensions);

/// <summary>A bounded non-sensitive metric dimension.</summary>
public sealed record MetricDimensionResponse(string Key, string Value);

/// <summary>A bounded keyset page of database health evidence.</summary>
public sealed record DatabaseHealthPageResponse(
    Guid InstanceId,
    DateTimeOffset RepositoryTimeUtc,
    CollectorHealthResponse Collector,
    IReadOnlyList<DatabaseHealthResponse> Items,
    string? NextCursor);

/// <summary>Safe database identity and state without connection or provider details.</summary>
public sealed record DatabaseHealthResponse(
    int DatabaseId,
    string Name,
    string State,
    string RecoveryModel,
    string UserAccess,
    bool IsReadOnly,
    int CompatibilityLevel,
    DateTimeOffset ObservedAtUtc,
    CollectorHealthResponse Collector);

/// <summary>A bounded keyset page of logical-file health evidence.</summary>
public sealed record DatabaseFileHealthPageResponse(
    Guid InstanceId,
    DateTimeOffset RepositoryTimeUtc,
    CollectorHealthResponse Collector,
    IReadOnlyList<DatabaseFileHealthResponse> Items,
    string? NextCursor);

/// <summary>
/// Safe logical-file capacity and cumulative I/O evidence. Physical paths are intentionally absent;
/// 64-bit counters are invariant strings so JavaScript clients cannot lose integer precision.
/// </summary>
public sealed record DatabaseFileHealthResponse(
    int DatabaseId,
    int FileId,
    string LogicalName,
    string FileType,
    string State,
    string SizeBytes,
    string? MaximumSizeBytes,
    string GrowthBytes,
    int GrowthPercent,
    string ReadCount,
    string WriteCount,
    string BytesRead,
    string BytesWritten,
    string IoStallMilliseconds,
    string? ReadStallMilliseconds,
    string? WriteStallMilliseconds,
    DateTimeOffset ObservedAtUtc,
    CollectorHealthResponse Collector);

/// <summary>
/// Bounded collector evidence. It deliberately excludes target connection details, SQL text,
/// provider errors, and sensitive diagnostic payloads.
/// </summary>
public sealed record CollectorHealthResponse(
    string CollectorId,
    int ManifestVersion,
    int OutputSchemaVersion,
    string State,
    string Reason,
    string? LastExecutionOutcome,
    string CircuitState,
    double? DurationMilliseconds,
    int RetryCount,
    long SourceRows,
    long OutputRows,
    long InsertedRows,
    long DuplicateRows,
    long RejectedRows,
    long ResponseBytes,
    long PersistedBytes,
    string? SampleLossKind,
    int? MinimumLostItems,
    bool? LossCountIsExact,
    int? MinimumLostBytes,
    bool HasVisibilityGap,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset NextDueAtUtc);
