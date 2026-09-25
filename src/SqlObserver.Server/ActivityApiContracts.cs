namespace SqlObserver.Server;

/// <summary>
/// Safe evidence for the exact persisted collector snapshot behind an activity page.
/// The API deliberately omits provider messages, SQL text, login, host, application,
/// network, wait-resource, and resource-description data.
/// </summary>
public sealed record ActivitySnapshotEvidenceResponse(
    Guid RunId,
    string TargetRevision,
    string CollectorId,
    string Freshness,
    string Outcome,
    string Reason,
    bool IsPartial,
    ActivityLossResponse? Loss,
    DateTimeOffset CompletedAtUtc);

/// <summary>Conservative evidence for known or minimum activity-sample loss.</summary>
public sealed record ActivityLossResponse(
    string Kind,
    int MinimumLostItems,
    bool CountIsExact,
    int MinimumLostBytes);

/// <summary>A bounded snapshot-bound page of SQL Server sessions.</summary>
public sealed record ActivitySessionPageResponse(
    Guid InstanceId,
    DateTimeOffset RepositoryTimeUtc,
    ActivitySnapshotEvidenceResponse? Evidence,
    IReadOnlyList<ActivitySessionResponse> Items,
    string? NextCursor);

/// <summary>Safe session activity. Every SQL bigint counter is a decimal string.</summary>
public sealed record ActivitySessionResponse(
    int SessionId,
    string Status,
    bool IsUserProcess,
    int? DatabaseId,
    int OpenTransactionCount,
    string CpuMilliseconds,
    string MemoryUsagePages,
    string Reads,
    string Writes,
    string LogicalReads,
    string TotalElapsedMilliseconds,
    DateTimeOffset ObservedAtUtc);

/// <summary>A bounded snapshot-bound page of active SQL Server requests.</summary>
public sealed record ActivityRequestPageResponse(
    Guid InstanceId,
    DateTimeOffset RepositoryTimeUtc,
    ActivitySnapshotEvidenceResponse? Evidence,
    IReadOnlyList<ActivityRequestResponse> Items,
    string? NextCursor);

/// <summary>
/// Safe request activity without SQL text, query handles, plans, principals, clients,
/// applications, resource descriptions, or network data. Bigint counters remain strings.
/// </summary>
public sealed record ActivityRequestResponse(
    int SessionId,
    int RequestId,
    string Status,
    string Command,
    int? DatabaseId,
    string CpuMilliseconds,
    string TotalElapsedMilliseconds,
    string Reads,
    string Writes,
    string LogicalReads,
    string RowCount,
    double PercentComplete,
    DateTimeOffset ObservedAtUtc);

/// <summary>A bounded snapshot-bound page of server wait counters and deltas.</summary>
public sealed record ServerWaitSummaryPageResponse(
    Guid InstanceId,
    DateTimeOffset RepositoryTimeUtc,
    ActivitySnapshotEvidenceResponse? Evidence,
    Guid? BaselineRunId,
    IReadOnlyList<ServerWaitSummaryResponse> Items,
    string? NextCursor);

/// <summary>Safe aggregate wait evidence. Counter values and deltas are exact strings.</summary>
public sealed record ServerWaitSummaryResponse(
    string WaitType,
    string WaitingTasksCount,
    string WaitTimeMilliseconds,
    string MaximumWaitTimeMilliseconds,
    string SignalWaitTimeMilliseconds,
    bool BaselineAvailable,
    bool ResetDetected,
    string? WaitingTasksDelta,
    string? WaitTimeMillisecondsDelta,
    string? SignalWaitTimeMillisecondsDelta,
    DateTimeOffset ObservedAtUtc);

/// <summary>Bounded newest-first server-wait history over an exact UTC window.</summary>
public sealed record ServerWaitHistoryPageResponse(
    Guid InstanceId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset RepositoryTimeUtc,
    IReadOnlyList<ServerWaitHistoryResponse> Items,
    string? NextCursor);

public sealed record ServerWaitHistoryResponse(
    ActivitySnapshotEvidenceResponse Evidence,
    Guid? BaselineRunId,
    ServerWaitSummaryResponse Wait);

/// <summary>A bounded snapshot-bound page of current blocking edges.</summary>
public sealed record CurrentBlockingPageResponse(
    Guid InstanceId,
    DateTimeOffset RepositoryTimeUtc,
    ActivitySnapshotEvidenceResponse? Evidence,
    int MaximumChainDepth,
    int MaximumGraphNodes,
    IReadOnlyList<BlockingEdgeResponse> Items,
    string? NextCursor);

/// <summary>
/// A safe blocking edge without arbitrary resource details, SQL text, principals,
/// client identity, or network data. Wait counters are exact strings.
/// </summary>
public sealed record BlockingEdgeResponse(
    int BlockedSessionId,
    string BlockerKind,
    int? BlockerSessionId,
    string WaitType,
    string WaitingTaskCount,
    string WaitDurationMilliseconds,
    int? RootBlockerSessionId,
    int ChainDepth,
    string ChainState,
    DateTimeOffset ObservedAtUtc);

/// <summary>A bounded newest-first history page over an exact UTC time window.</summary>
public sealed record BlockingHistoryPageResponse(
    Guid InstanceId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset RepositoryTimeUtc,
    IReadOnlyList<BlockingHistoryResponse> Items,
    string? NextCursor);

/// <summary>One historical blocking edge with its exact persisted run evidence.</summary>
public sealed record BlockingHistoryResponse(
    ActivitySnapshotEvidenceResponse Evidence,
    BlockingEdgeResponse Edge);
