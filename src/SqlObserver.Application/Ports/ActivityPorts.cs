using System.Collections.ObjectModel;
using System.Globalization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

/// <summary>
/// Identifies the exact persisted collector snapshot represented by an activity page.
/// Counter values are exposed by the item DTOs as invariant strings so a JSON client
/// never loses precision by coercing SQL bigint values to IEEE-754 numbers.
/// </summary>
public sealed class ActivitySnapshotEvidence
{
    public ActivitySnapshotEvidence(
        MonitoredInstanceId targetId,
        CollectorRunId runId,
        ObservationTargetRevision targetRevision,
        CollectorId collectorId,
        CollectorRunOutcome outcome,
        CollectorRunReason reason,
        CollectorLossEvidence loss,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(loss);
        if (outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if ((outcome == CollectorRunOutcome.Succeeded && (reason != CollectorRunReason.Completed || loss.HasLoss)) ||
            (outcome == CollectorRunOutcome.Partial && !loss.HasLoss))
        {
            throw new ArgumentException("Activity snapshot outcome and loss evidence are inconsistent.", nameof(loss));
        }

        TargetId = targetId;
        RunId = runId;
        TargetRevision = targetRevision.Value.ToString(CultureInfo.InvariantCulture);
        CollectorId = collectorId;
        Outcome = outcome;
        Reason = reason;
        Loss = loss;
        CompletedAtUtc = CollectionPortValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId RunId { get; }
    public string TargetRevision { get; }
    public CollectorId CollectorId { get; }
    public CollectorRunOutcome Outcome { get; }
    public CollectorRunReason Reason { get; }
    public CollectorLossEvidence Loss { get; }
    public DateTimeOffset CompletedAtUtc { get; }
}

public sealed class ActivitySessionSnapshotItem
{
    public ActivitySessionSnapshotItem(
        int sessionId,
        ActivitySessionStatus status,
        bool isUserProcess,
        int? databaseId,
        int openTransactionCount,
        long cpuMilliseconds,
        long memoryUsagePages,
        long reads,
        long writes,
        long logicalReads,
        long totalElapsedMilliseconds,
        DateTimeOffset observedAtUtc)
    {
        if (sessionId is <= 0 or > 32_767 ||
            databaseId is not null and (<= 0 or > 32_767) ||
            openTransactionCount < 0 ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        SessionId = sessionId;
        Status = status;
        IsUserProcess = isUserProcess;
        DatabaseId = databaseId;
        OpenTransactionCount = openTransactionCount;
        CpuMilliseconds = ActivityPortValidation.Counter(cpuMilliseconds, nameof(cpuMilliseconds));
        MemoryUsagePages = ActivityPortValidation.Counter(memoryUsagePages, nameof(memoryUsagePages));
        Reads = ActivityPortValidation.Counter(reads, nameof(reads));
        Writes = ActivityPortValidation.Counter(writes, nameof(writes));
        LogicalReads = ActivityPortValidation.Counter(logicalReads, nameof(logicalReads));
        TotalElapsedMilliseconds = ActivityPortValidation.Counter(
            totalElapsedMilliseconds,
            nameof(totalElapsedMilliseconds));
        ObservedAtUtc = CollectionPortValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }

    public int SessionId { get; }
    public ActivitySessionStatus Status { get; }
    public bool IsUserProcess { get; }
    public int? DatabaseId { get; }
    public int OpenTransactionCount { get; }
    public string CpuMilliseconds { get; }
    public string MemoryUsagePages { get; }
    public string Reads { get; }
    public string Writes { get; }
    public string LogicalReads { get; }
    public string TotalElapsedMilliseconds { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public sealed class ActivityRequestSnapshotItem
{
    public ActivityRequestSnapshotItem(
        int sessionId,
        int requestId,
        ActivityRequestStatus status,
        ActivityRequestCommand command,
        int? databaseId,
        long cpuMilliseconds,
        long totalElapsedMilliseconds,
        long reads,
        long writes,
        long logicalReads,
        long rowCount,
        double percentComplete,
        DateTimeOffset observedAtUtc)
    {
        if (sessionId is <= 0 or > 32_767 || requestId < 0 ||
            databaseId is not null and (<= 0 or > 32_767) ||
            !Enum.IsDefined(status) || !Enum.IsDefined(command) ||
            !double.IsFinite(percentComplete) || percentComplete is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        SessionId = sessionId;
        RequestId = requestId;
        Status = status;
        Command = command;
        DatabaseId = databaseId;
        CpuMilliseconds = ActivityPortValidation.Counter(cpuMilliseconds, nameof(cpuMilliseconds));
        TotalElapsedMilliseconds = ActivityPortValidation.Counter(
            totalElapsedMilliseconds,
            nameof(totalElapsedMilliseconds));
        Reads = ActivityPortValidation.Counter(reads, nameof(reads));
        Writes = ActivityPortValidation.Counter(writes, nameof(writes));
        LogicalReads = ActivityPortValidation.Counter(logicalReads, nameof(logicalReads));
        RowCount = ActivityPortValidation.Counter(rowCount, nameof(rowCount));
        PercentComplete = percentComplete;
        ObservedAtUtc = CollectionPortValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }

    public int SessionId { get; }
    public int RequestId { get; }
    public ActivityRequestStatus Status { get; }
    public ActivityRequestCommand Command { get; }
    public int? DatabaseId { get; }
    public string CpuMilliseconds { get; }
    public string TotalElapsedMilliseconds { get; }
    public string Reads { get; }
    public string Writes { get; }
    public string LogicalReads { get; }
    public string RowCount { get; }
    public double PercentComplete { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public sealed class ServerWaitSummaryItem
{
    public ServerWaitSummaryItem(
        SqlServerWaitType waitType,
        long waitingTasksCount,
        long waitTimeMilliseconds,
        long maximumWaitTimeMilliseconds,
        long signalWaitTimeMilliseconds,
        bool baselineAvailable,
        bool resetDetected,
        long? waitingTasksDelta,
        long? waitTimeMillisecondsDelta,
        long? signalWaitTimeMillisecondsDelta,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(waitType);
        if (resetDetected && !baselineAvailable)
        {
            throw new ArgumentException("A wait-counter reset requires a preceding baseline snapshot.", nameof(resetDetected));
        }

        if ((!baselineAvailable || resetDetected) !=
            (waitingTasksDelta is null && waitTimeMillisecondsDelta is null && signalWaitTimeMillisecondsDelta is null))
        {
            throw new ArgumentException("Wait deltas must be absent exactly when no baseline exists or a reset was detected.");
        }

        WaitType = waitType;
        WaitingTasksCount = ActivityPortValidation.Counter(waitingTasksCount, nameof(waitingTasksCount));
        WaitTimeMilliseconds = ActivityPortValidation.Counter(waitTimeMilliseconds, nameof(waitTimeMilliseconds));
        MaximumWaitTimeMilliseconds = ActivityPortValidation.Counter(
            maximumWaitTimeMilliseconds,
            nameof(maximumWaitTimeMilliseconds));
        SignalWaitTimeMilliseconds = ActivityPortValidation.Counter(
            signalWaitTimeMilliseconds,
            nameof(signalWaitTimeMilliseconds));
        BaselineAvailable = baselineAvailable;
        ResetDetected = resetDetected;
        WaitingTasksDelta = ActivityPortValidation.OptionalCounter(waitingTasksDelta, nameof(waitingTasksDelta));
        WaitTimeMillisecondsDelta = ActivityPortValidation.OptionalCounter(
            waitTimeMillisecondsDelta,
            nameof(waitTimeMillisecondsDelta));
        SignalWaitTimeMillisecondsDelta = ActivityPortValidation.OptionalCounter(
            signalWaitTimeMillisecondsDelta,
            nameof(signalWaitTimeMillisecondsDelta));
        ObservedAtUtc = CollectionPortValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }

    public SqlServerWaitType WaitType { get; }
    public string WaitingTasksCount { get; }
    public string WaitTimeMilliseconds { get; }
    public string MaximumWaitTimeMilliseconds { get; }
    public string SignalWaitTimeMilliseconds { get; }
    public bool BaselineAvailable { get; }
    public bool ResetDetected { get; }
    public string? WaitingTasksDelta { get; }
    public string? WaitTimeMillisecondsDelta { get; }
    public string? SignalWaitTimeMillisecondsDelta { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public sealed class BlockingEdgeSnapshotItem
{
    public BlockingEdgeSnapshotItem(
        int blockedSessionId,
        BlockingBlockerKind blockerKind,
        int? blockerSessionId,
        SqlServerWaitType waitType,
        long waitingTaskCount,
        long waitDurationMilliseconds,
        int? rootBlockerSessionId,
        int chainDepth,
        BlockingChainState chainState,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(waitType);
        if (blockedSessionId is <= 0 or > 32_767 ||
            (blockerKind == BlockingBlockerKind.Session) != (blockerSessionId is not null) ||
            blockerSessionId is not null and (<= 0 or > 32_767) ||
            rootBlockerSessionId is not null and (<= 0 or > 32_767) ||
            chainDepth is < 1 or > BlockingChainLimits.MaximumDepth ||
            !Enum.IsDefined(blockerKind) || !Enum.IsDefined(chainState))
        {
            throw new ArgumentOutOfRangeException(nameof(blockedSessionId));
        }

        BlockedSessionId = blockedSessionId;
        BlockerKind = blockerKind;
        BlockerSessionId = blockerSessionId;
        WaitType = waitType;
        WaitingTaskCount = ActivityPortValidation.Counter(waitingTaskCount, nameof(waitingTaskCount), positive: true);
        WaitDurationMilliseconds = ActivityPortValidation.Counter(
            waitDurationMilliseconds,
            nameof(waitDurationMilliseconds));
        RootBlockerSessionId = rootBlockerSessionId;
        ChainDepth = chainDepth;
        ChainState = chainState;
        ObservedAtUtc = CollectionPortValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }

    public int BlockedSessionId { get; }
    public BlockingBlockerKind BlockerKind { get; }
    public int? BlockerSessionId { get; }
    public SqlServerWaitType WaitType { get; }
    public string WaitingTaskCount { get; }
    public string WaitDurationMilliseconds { get; }
    public int? RootBlockerSessionId { get; }
    public int ChainDepth { get; }
    public BlockingChainState ChainState { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public sealed record ActivitySessionCursor
{
    public ActivitySessionCursor(
        MonitoredInstanceId targetId,
        CollectorRunId snapshotRunId,
        ObservationTargetRevision snapshotTargetRevision,
        int sessionId)
    {
        ActivityPortValidation.Cursor(targetId, snapshotRunId, snapshotTargetRevision);
        if (sessionId is <= 0 or > 32_767)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        SessionId = sessionId;
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId SnapshotRunId { get; }
    public ObservationTargetRevision SnapshotTargetRevision { get; }
    public int SessionId { get; }
}

public sealed record ActivityRequestCursor
{
    public ActivityRequestCursor(
        MonitoredInstanceId targetId,
        CollectorRunId snapshotRunId,
        ObservationTargetRevision snapshotTargetRevision,
        int sessionId,
        int requestId)
    {
        ActivityPortValidation.Cursor(targetId, snapshotRunId, snapshotTargetRevision);
        if (sessionId is <= 0 or > 32_767 || requestId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        SessionId = sessionId;
        RequestId = requestId;
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId SnapshotRunId { get; }
    public ObservationTargetRevision SnapshotTargetRevision { get; }
    public int SessionId { get; }
    public int RequestId { get; }
}

public sealed record ServerWaitSummaryCursor
{
    public ServerWaitSummaryCursor(
        MonitoredInstanceId targetId,
        CollectorRunId snapshotRunId,
        CollectorRunId? baselineRunId,
        ObservationTargetRevision snapshotTargetRevision,
        SqlServerWaitType waitType)
    {
        ActivityPortValidation.Cursor(targetId, snapshotRunId, snapshotTargetRevision);
        ArgumentNullException.ThrowIfNull(waitType);
        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        BaselineRunId = baselineRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        WaitType = waitType;
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId SnapshotRunId { get; }
    public CollectorRunId? BaselineRunId { get; }
    public ObservationTargetRevision SnapshotTargetRevision { get; }
    public SqlServerWaitType WaitType { get; }
}

public sealed record BlockingEdgeCursor
{
    public BlockingEdgeCursor(
        MonitoredInstanceId targetId,
        CollectorRunId snapshotRunId,
        ObservationTargetRevision snapshotTargetRevision,
        int blockedSessionId,
        BlockingBlockerKind blockerKind,
        int? blockerSessionId,
        SqlServerWaitType waitType)
    {
        ActivityPortValidation.Cursor(targetId, snapshotRunId, snapshotTargetRevision);
        ArgumentNullException.ThrowIfNull(waitType);
        if (blockedSessionId is <= 0 or > 32_767 ||
            (blockerKind == BlockingBlockerKind.Session) != (blockerSessionId is not null) ||
            blockerSessionId is not null and (<= 0 or > 32_767))
        {
            throw new ArgumentOutOfRangeException(nameof(blockedSessionId));
        }

        TargetId = targetId;
        SnapshotRunId = snapshotRunId;
        SnapshotTargetRevision = snapshotTargetRevision;
        BlockedSessionId = blockedSessionId;
        BlockerKind = blockerKind;
        BlockerSessionId = blockerSessionId;
        WaitType = waitType;
    }

    public MonitoredInstanceId TargetId { get; }
    public CollectorRunId SnapshotRunId { get; }
    public ObservationTargetRevision SnapshotTargetRevision { get; }
    public int BlockedSessionId { get; }
    public BlockingBlockerKind BlockerKind { get; }
    public int? BlockerSessionId { get; }
    public SqlServerWaitType WaitType { get; }
}

public sealed record BlockingHistoryCursor
{
    public BlockingHistoryCursor(
        MonitoredInstanceId targetId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        DateTimeOffset observedAtUtc,
        CollectorRunId runId,
        int blockedSessionId,
        BlockingBlockerKind blockerKind,
        int? blockerSessionId,
        SqlServerWaitType waitType)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(waitType);
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        observedAtUtc = CollectionPortValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < fromUtc || observedAtUtc >= toUtc ||
            blockedSessionId is <= 0 or > 32_767 ||
            (blockerKind == BlockingBlockerKind.Session) != (blockerSessionId is not null) ||
            blockerSessionId is not null and (<= 0 or > 32_767))
        {
            throw new ArgumentOutOfRangeException(nameof(observedAtUtc));
        }

        TargetId = targetId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        ObservedAtUtc = observedAtUtc;
        RunId = runId;
        BlockedSessionId = blockedSessionId;
        BlockerKind = blockerKind;
        BlockerSessionId = blockerSessionId;
        WaitType = waitType;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public CollectorRunId RunId { get; }
    public int BlockedSessionId { get; }
    public BlockingBlockerKind BlockerKind { get; }
    public int? BlockerSessionId { get; }
    public SqlServerWaitType WaitType { get; }
}

public sealed record ServerWaitHistoryCursor
{
    public ServerWaitHistoryCursor(MonitoredInstanceId targetId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, DateTimeOffset observedAtUtc, CollectorRunId runId,
        SqlServerWaitType waitType)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        WaitType = waitType ?? throw new ArgumentNullException(nameof(waitType));
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        observedAtUtc = CollectionPortValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < fromUtc || observedAtUtc >= toUtc)
            throw new ArgumentOutOfRangeException(nameof(observedAtUtc));
        FromUtc = fromUtc;
        ToUtc = toUtc;
        ObservedAtUtc = observedAtUtc;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public CollectorRunId RunId { get; }
    public SqlServerWaitType WaitType { get; }
}

public sealed class ListActivitySessionsRepositoryRequest
    : ActivityRepositoryRequest<ActivitySessionCursor>
{
    public const int MaximumResults = 100;

    public ListActivitySessionsRepositoryRequest(
        MonitoredInstanceId targetId,
        int maxResults,
        ActivitySessionCursor? cursor,
        RepositoryCallTimeout timeout)
        : base(targetId, maxResults, MaximumResults, cursor, timeout)
    {
    }
}

public sealed class ListActivityRequestsRepositoryRequest
    : ActivityRepositoryRequest<ActivityRequestCursor>
{
    public const int MaximumResults = 100;

    public ListActivityRequestsRepositoryRequest(
        MonitoredInstanceId targetId,
        int maxResults,
        ActivityRequestCursor? cursor,
        RepositoryCallTimeout timeout)
        : base(targetId, maxResults, MaximumResults, cursor, timeout)
    {
    }
}

public sealed class ListServerWaitSummaryRepositoryRequest
    : ActivityRepositoryRequest<ServerWaitSummaryCursor>
{
    public const int MaximumResults = 100;

    public ListServerWaitSummaryRepositoryRequest(
        MonitoredInstanceId targetId,
        int maxResults,
        ServerWaitSummaryCursor? cursor,
        RepositoryCallTimeout timeout)
        : base(targetId, maxResults, MaximumResults, cursor, timeout)
    {
    }
}

public sealed class ListCurrentBlockingRepositoryRequest
    : ActivityRepositoryRequest<BlockingEdgeCursor>
{
    public const int MaximumResults = BlockingChainLimits.MaximumNodes;

    public ListCurrentBlockingRepositoryRequest(
        MonitoredInstanceId targetId,
        int maxResults,
        BlockingEdgeCursor? cursor,
        RepositoryCallTimeout timeout)
        : base(targetId, maxResults, MaximumResults, cursor, timeout)
    {
    }
}

public sealed class ListBlockingHistoryRepositoryRequest
{
    public const int MaximumResults = 100;
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromHours(24);

    public ListBlockingHistoryRepositoryRequest(
        MonitoredInstanceId targetId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxResults,
        BlockingHistoryCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        if (maxResults is <= 0 or > MaximumResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        if (cursor is not null &&
            (cursor.TargetId != targetId || cursor.FromUtc != fromUtc || cursor.ToUtc != toUtc))
        {
            throw new ArgumentException("A blocking-history cursor must retain the exact target and UTC window.", nameof(cursor));
        }

        TargetId = targetId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int MaxResults { get; }
    public BlockingHistoryCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ListServerWaitHistoryRepositoryRequest
{
    public const int MaximumResults = 100;
    public ListServerWaitHistoryRepositoryRequest(MonitoredInstanceId targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxResults,
        ServerWaitHistoryCursor? cursor, RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        if (maxResults is <= 0 or > MaximumResults)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (cursor is not null &&
            (cursor.TargetId != targetId || cursor.FromUtc != fromUtc || cursor.ToUtc != toUtc))
            throw new ArgumentException("A wait-history cursor must retain its target and UTC window.", nameof(cursor));
        FromUtc = fromUtc;
        ToUtc = toUtc;
        MaxResults = maxResults;
        Cursor = cursor;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int MaxResults { get; }
    public ServerWaitHistoryCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public abstract class ActivityRepositoryRequest<TCursor>
    where TCursor : class
{
    private protected ActivityRepositoryRequest(
        MonitoredInstanceId targetId,
        int maxResults,
        int maximumResults,
        TCursor? cursor,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        if (maxResults is <= 0 || maxResults > maximumResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        if (cursor is not null && ActivityPortValidation.CursorTarget(cursor) != targetId)
        {
            throw new ArgumentException("An activity cursor must belong to the requested target.", nameof(cursor));
        }

        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public TCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ActivitySessionPage : ActivityPage<ActivitySessionSnapshotItem, ActivitySessionCursor>
{
    public ActivitySessionPage(
        MonitoredInstanceId targetId,
        ActivitySnapshotEvidence? evidence,
        IReadOnlyList<ActivitySessionSnapshotItem> items,
        ActivitySessionCursor? nextCursor,
        DateTimeOffset repositoryTimeUtc)
        : base(targetId, evidence, items, nextCursor, ListActivitySessionsRepositoryRequest.MaximumResults, repositoryTimeUtc)
    {
    }
}

public sealed class ActivityRequestPage : ActivityPage<ActivityRequestSnapshotItem, ActivityRequestCursor>
{
    public ActivityRequestPage(
        MonitoredInstanceId targetId,
        ActivitySnapshotEvidence? evidence,
        IReadOnlyList<ActivityRequestSnapshotItem> items,
        ActivityRequestCursor? nextCursor,
        DateTimeOffset repositoryTimeUtc)
        : base(targetId, evidence, items, nextCursor, ListActivityRequestsRepositoryRequest.MaximumResults, repositoryTimeUtc)
    {
    }
}

public sealed class ServerWaitSummaryPage : ActivityPage<ServerWaitSummaryItem, ServerWaitSummaryCursor>
{
    public ServerWaitSummaryPage(
        MonitoredInstanceId targetId,
        ActivitySnapshotEvidence? evidence,
        CollectorRunId? baselineRunId,
        IReadOnlyList<ServerWaitSummaryItem> items,
        ServerWaitSummaryCursor? nextCursor,
        DateTimeOffset repositoryTimeUtc)
        : base(targetId, evidence, items, nextCursor, ListServerWaitSummaryRepositoryRequest.MaximumResults, repositoryTimeUtc)
    {
        if (nextCursor is not null && nextCursor.BaselineRunId != baselineRunId)
        {
            throw new ArgumentException("A wait-summary cursor must retain the exact baseline run.", nameof(nextCursor));
        }

        BaselineRunId = baselineRunId;
    }

    public CollectorRunId? BaselineRunId { get; }
}

public sealed class CurrentBlockingPage : ActivityPage<BlockingEdgeSnapshotItem, BlockingEdgeCursor>
{
    public CurrentBlockingPage(
        MonitoredInstanceId targetId,
        ActivitySnapshotEvidence? evidence,
        IReadOnlyList<BlockingEdgeSnapshotItem> items,
        BlockingEdgeCursor? nextCursor,
        DateTimeOffset repositoryTimeUtc)
        : base(targetId, evidence, items, nextCursor, ListCurrentBlockingRepositoryRequest.MaximumResults, repositoryTimeUtc)
    {
        if (items.Select(static item => item.BlockedSessionId)
            .Concat(items.Where(static item => item.BlockerSessionId.HasValue)
                .Select(static item => item.BlockerSessionId!.Value))
            .Distinct()
            .Count() > BlockingChainLimits.MaximumNodes)
        {
            throw new ArgumentException("A blocking page exceeds the bounded graph-node limit.", nameof(items));
        }
    }
}

public sealed class BlockingHistoryItem
{
    public BlockingHistoryItem(ActivitySnapshotEvidence evidence, BlockingEdgeSnapshotItem edge)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Edge = edge ?? throw new ArgumentNullException(nameof(edge));
        if (evidence.CollectorId.Value != "blocking.current")
        {
            throw new ArgumentException("Blocking history requires blocking.current run evidence.", nameof(evidence));
        }
    }

    public ActivitySnapshotEvidence Evidence { get; }
    public BlockingEdgeSnapshotItem Edge { get; }
}

public sealed class BlockingHistoryPage
{
    private readonly ReadOnlyCollection<BlockingHistoryItem> _items;

    public BlockingHistoryPage(
        MonitoredInstanceId targetId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<BlockingHistoryItem> items,
        BlockingHistoryCursor? nextCursor,
        DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(items);
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        if (items.Count > ListBlockingHistoryRepositoryRequest.MaximumResults ||
            items.Any(item => item is null || item.Evidence.TargetId != targetId ||
                item.Edge.ObservedAtUtc < fromUtc || item.Edge.ObservedAtUtc >= toUtc) ||
            (nextCursor is not null &&
                (nextCursor.TargetId != targetId || nextCursor.FromUtc != fromUtc || nextCursor.ToUtc != toUtc)))
        {
            throw new ArgumentException("A blocking-history page must be bounded and target/window scoped.", nameof(items));
        }

        TargetId = targetId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        _items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public IReadOnlyList<BlockingHistoryItem> Items => _items;
    public BlockingHistoryCursor? NextCursor { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public sealed class ServerWaitHistoryItem
{
    public ServerWaitHistoryItem(ActivitySnapshotEvidence evidence, CollectorRunId? baselineRunId,
        ServerWaitSummaryItem wait)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Wait = wait ?? throw new ArgumentNullException(nameof(wait));
        if (evidence.CollectorId.Value != "waits.server")
            throw new ArgumentException("Wait history requires waits.server run evidence.", nameof(evidence));
        BaselineRunId = baselineRunId;
    }

    public ActivitySnapshotEvidence Evidence { get; }
    public CollectorRunId? BaselineRunId { get; }
    public ServerWaitSummaryItem Wait { get; }
}

public sealed class ServerWaitHistoryPage
{
    private readonly ReadOnlyCollection<ServerWaitHistoryItem> _items;
    public ServerWaitHistoryPage(MonitoredInstanceId targetId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, IReadOnlyList<ServerWaitHistoryItem> items,
        ServerWaitHistoryCursor? nextCursor, DateTimeOffset repositoryTimeUtc)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        ArgumentNullException.ThrowIfNull(items);
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        if (items.Count > ListServerWaitHistoryRepositoryRequest.MaximumResults ||
            items.Any(item => item is null || item.Evidence.TargetId != targetId ||
                item.Wait.ObservedAtUtc < fromUtc || item.Wait.ObservedAtUtc >= toUtc) ||
            nextCursor is not null &&
            (nextCursor.TargetId != targetId || nextCursor.FromUtc != fromUtc ||
             nextCursor.ToUtc != toUtc || items.Count == 0 ||
             nextCursor.ObservedAtUtc != items[^1].Wait.ObservedAtUtc ||
             nextCursor.RunId != items[^1].Evidence.RunId ||
             nextCursor.WaitType != items[^1].Wait.WaitType))
            throw new ArgumentException("A wait-history page must be bounded and target/window scoped.", nameof(items));
        FromUtc = fromUtc;
        ToUtc = toUtc;
        _items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public IReadOnlyList<ServerWaitHistoryItem> Items => _items;
    public ServerWaitHistoryCursor? NextCursor { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public abstract class ActivityPage<TItem, TCursor>
    where TItem : class
    where TCursor : class
{
    private readonly ReadOnlyCollection<TItem> _items;

    private protected ActivityPage(
        MonitoredInstanceId targetId,
        ActivitySnapshotEvidence? evidence,
        IReadOnlyList<TItem> items,
        TCursor? nextCursor,
        int maximumResults,
        DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > maximumResults || items.Any(static item => item is null) ||
            (evidence is null && (items.Count > 0 || nextCursor is not null)) ||
            (evidence is not null && evidence.TargetId != targetId) ||
            (nextCursor is not null &&
                (ActivityPortValidation.CursorTarget(nextCursor) != targetId ||
                 ActivityPortValidation.CursorRun(nextCursor) != evidence?.RunId ||
                 ActivityPortValidation.CursorRevision(nextCursor).Value.ToString(CultureInfo.InvariantCulture) !=
                    evidence?.TargetRevision)))
        {
            throw new ArgumentException("An activity page must be bounded and bound to one target snapshot.", nameof(items));
        }

        TargetId = targetId;
        Evidence = evidence;
        _items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public ActivitySnapshotEvidence? Evidence { get; }
    public IReadOnlyList<TItem> Items => _items;
    public TCursor? NextCursor { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
}

public interface IActivityProjectionRepositoryPort
{
    ValueTask<ActivitySessionPage?> ListSessionsAsync(
        ListActivitySessionsRepositoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<ActivityRequestPage?> ListRequestsAsync(
        ListActivityRequestsRepositoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<ServerWaitSummaryPage?> ListWaitSummaryAsync(
        ListServerWaitSummaryRepositoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<CurrentBlockingPage?> ListCurrentBlockingAsync(
        ListCurrentBlockingRepositoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<BlockingHistoryPage?> ListBlockingHistoryAsync(
        ListBlockingHistoryRepositoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<ServerWaitHistoryPage?> ListServerWaitHistoryAsync(
        ListServerWaitHistoryRepositoryRequest request,
        CancellationToken cancellationToken);
}

internal static class ActivityPortValidation
{
    public static string Counter(long value, string parameterName, bool positive = false)
    {
        if (value < 0 || (positive && value == 0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static string? OptionalCounter(long? value, string parameterName) =>
        value is null ? null : Counter(value.Value, parameterName);

    public static void Cursor(
        MonitoredInstanceId targetId,
        CollectorRunId runId,
        ObservationTargetRevision revision)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(revision);
    }

    public static MonitoredInstanceId CursorTarget<TCursor>(TCursor cursor) => cursor switch
    {
        ActivitySessionCursor value => value.TargetId,
        ActivityRequestCursor value => value.TargetId,
        ServerWaitSummaryCursor value => value.TargetId,
        BlockingEdgeCursor value => value.TargetId,
        _ => throw new ArgumentException("Unknown activity cursor type.", nameof(cursor)),
    };

    public static CollectorRunId CursorRun<TCursor>(TCursor cursor) => cursor switch
    {
        ActivitySessionCursor value => value.SnapshotRunId,
        ActivityRequestCursor value => value.SnapshotRunId,
        ServerWaitSummaryCursor value => value.SnapshotRunId,
        BlockingEdgeCursor value => value.SnapshotRunId,
        _ => throw new ArgumentException("Unknown activity cursor type.", nameof(cursor)),
    };

    public static ObservationTargetRevision CursorRevision<TCursor>(TCursor cursor) => cursor switch
    {
        ActivitySessionCursor value => value.SnapshotTargetRevision,
        ActivityRequestCursor value => value.SnapshotTargetRevision,
        ServerWaitSummaryCursor value => value.SnapshotTargetRevision,
        BlockingEdgeCursor value => value.SnapshotTargetRevision,
        _ => throw new ArgumentException("Unknown activity cursor type.", nameof(cursor)),
    };

    public static void TimeWindow(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        fromUtc = CollectionPortValidation.RequireUtc(fromUtc, nameof(fromUtc));
        toUtc = CollectionPortValidation.RequireUtc(toUtc, nameof(toUtc));
        if (toUtc <= fromUtc || toUtc - fromUtc > ListBlockingHistoryRepositoryRequest.MaximumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(toUtc));
        }
    }
}
