using System.Collections.ObjectModel;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public enum ActivitySessionStatus
{
    Running = 1,
    Sleeping = 2,
    Dormant = 3,
    Preconnect = 4,
    Other = 5,
}

public enum ActivityRequestStatus
{
    Background = 1,
    Running = 2,
    Runnable = 3,
    Sleeping = 4,
    Suspended = 5,
    Other = 6,
}

public enum ActivityRequestCommand
{
    Select = 1,
    Insert = 2,
    Update = 3,
    Delete = 4,
    Merge = 5,
    Backup = 6,
    Restore = 7,
    Dbcc = 8,
    Other = 9,
}

/// <summary>A SQL Server-owned wait type token. Arbitrary resource descriptions are never represented.</summary>
public sealed record SqlServerWaitType
{
    public const int MaximumLength = 120;

    public SqlServerWaitType(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character =>
                DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character == '_',
            requireLeadingLetter: true).ToUpperInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed class ActivitySessionObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 160;

    public ActivitySessionObservation(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
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
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        RequireSessionId(sessionId, nameof(sessionId));
        RequireDatabaseId(databaseId, nameof(databaseId));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(openTransactionCount);
        RequireNonNegative(cpuMilliseconds, nameof(cpuMilliseconds));
        RequireNonNegative(memoryUsagePages, nameof(memoryUsagePages));
        RequireNonNegative(reads, nameof(reads));
        RequireNonNegative(writes, nameof(writes));
        RequireNonNegative(logicalReads, nameof(logicalReads));
        RequireNonNegative(totalElapsedMilliseconds, nameof(totalElapsedMilliseconds));

        TargetId = targetId;
        TargetRevision = targetRevision;
        SessionId = sessionId;
        Status = status;
        IsUserProcess = isUserProcess;
        DatabaseId = databaseId;
        OpenTransactionCount = openTransactionCount;
        CpuMilliseconds = cpuMilliseconds;
        MemoryUsagePages = memoryUsagePages;
        Reads = reads;
        Writes = writes;
        LogicalReads = logicalReads;
        TotalElapsedMilliseconds = totalElapsedMilliseconds;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public int SessionId { get; }
    public ActivitySessionStatus Status { get; }
    public bool IsUserProcess { get; }
    public int? DatabaseId { get; }
    public int OpenTransactionCount { get; }
    public long CpuMilliseconds { get; }
    public long MemoryUsagePages { get; }
    public long Reads { get; }
    public long Writes { get; }
    public long LogicalReads { get; }
    public long TotalElapsedMilliseconds { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes => FixedEstimatedBytes;

    internal static void RequireSessionId(int value, string parameterName)
    {
        if (value is <= 0 or > 32_767)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    internal static void RequireDatabaseId(int? value, string parameterName)
    {
        if (value is not null and (<= 0 or > 32_767))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    internal static void RequireNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public sealed class ActivityRequestObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 176;

    public ActivityRequestObservation(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
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
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ActivitySessionObservation.RequireSessionId(sessionId, nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(requestId);

        if (!Enum.IsDefined(status) || !Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ActivitySessionObservation.RequireDatabaseId(databaseId, nameof(databaseId));
        ActivitySessionObservation.RequireNonNegative(cpuMilliseconds, nameof(cpuMilliseconds));
        ActivitySessionObservation.RequireNonNegative(totalElapsedMilliseconds, nameof(totalElapsedMilliseconds));
        ActivitySessionObservation.RequireNonNegative(reads, nameof(reads));
        ActivitySessionObservation.RequireNonNegative(writes, nameof(writes));
        ActivitySessionObservation.RequireNonNegative(logicalReads, nameof(logicalReads));
        ActivitySessionObservation.RequireNonNegative(rowCount, nameof(rowCount));
        if (!double.IsFinite(percentComplete) || percentComplete is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentComplete));
        }

        TargetId = targetId;
        TargetRevision = targetRevision;
        SessionId = sessionId;
        RequestId = requestId;
        Status = status;
        Command = command;
        DatabaseId = databaseId;
        CpuMilliseconds = cpuMilliseconds;
        TotalElapsedMilliseconds = totalElapsedMilliseconds;
        Reads = reads;
        Writes = writes;
        LogicalReads = logicalReads;
        RowCount = rowCount;
        PercentComplete = percentComplete;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public int SessionId { get; }
    public int RequestId { get; }
    public ActivityRequestStatus Status { get; }
    public ActivityRequestCommand Command { get; }
    public int? DatabaseId { get; }
    public long CpuMilliseconds { get; }
    public long TotalElapsedMilliseconds { get; }
    public long Reads { get; }
    public long Writes { get; }
    public long LogicalReads { get; }
    public long RowCount { get; }
    public double PercentComplete { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes => FixedEstimatedBytes;
}

public sealed class ServerWaitObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 112;

    public ServerWaitObservation(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        SqlServerWaitType waitType,
        long waitingTasksCount,
        long waitTimeMilliseconds,
        long maximumWaitTimeMilliseconds,
        long signalWaitTimeMilliseconds,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(waitType);
        ActivitySessionObservation.RequireNonNegative(waitingTasksCount, nameof(waitingTasksCount));
        ActivitySessionObservation.RequireNonNegative(waitTimeMilliseconds, nameof(waitTimeMilliseconds));
        ActivitySessionObservation.RequireNonNegative(maximumWaitTimeMilliseconds, nameof(maximumWaitTimeMilliseconds));
        ActivitySessionObservation.RequireNonNegative(signalWaitTimeMilliseconds, nameof(signalWaitTimeMilliseconds));
        if (maximumWaitTimeMilliseconds > waitTimeMilliseconds || signalWaitTimeMilliseconds > waitTimeMilliseconds)
        {
            throw new ArgumentException("SQL Server wait counters are internally inconsistent.");
        }

        TargetId = targetId;
        TargetRevision = targetRevision;
        WaitType = waitType;
        WaitingTasksCount = waitingTasksCount;
        WaitTimeMilliseconds = waitTimeMilliseconds;
        MaximumWaitTimeMilliseconds = maximumWaitTimeMilliseconds;
        SignalWaitTimeMilliseconds = signalWaitTimeMilliseconds;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
        EstimatedSizeBytes = checked(FixedEstimatedBytes + waitType.Value.Length);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public SqlServerWaitType WaitType { get; }
    public long WaitingTasksCount { get; }
    public long WaitTimeMilliseconds { get; }
    public long MaximumWaitTimeMilliseconds { get; }
    public long SignalWaitTimeMilliseconds { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes { get; }
}

/// <summary>Delta evidence where a counter regression is explicitly represented as a reset.</summary>
public sealed record ServerWaitCounterDelta
{
    private ServerWaitCounterDelta(
        bool resetDetected,
        long? waitingTasks,
        long? waitTimeMilliseconds,
        long? signalWaitTimeMilliseconds)
    {
        ResetDetected = resetDetected;
        WaitingTasks = waitingTasks;
        WaitTimeMilliseconds = waitTimeMilliseconds;
        SignalWaitTimeMilliseconds = signalWaitTimeMilliseconds;
    }

    public bool ResetDetected { get; }
    public long? WaitingTasks { get; }
    public long? WaitTimeMilliseconds { get; }
    public long? SignalWaitTimeMilliseconds { get; }

    public static ServerWaitCounterDelta Calculate(
        ServerWaitObservation previous,
        ServerWaitObservation current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.TargetId != current.TargetId ||
            previous.TargetRevision != current.TargetRevision ||
            previous.WaitType != current.WaitType ||
            current.ObservedAtUtc <= previous.ObservedAtUtc)
        {
            throw new ArgumentException("Wait delta inputs must be ordered snapshots of the same target revision and wait type.");
        }

        bool reset = current.WaitingTasksCount < previous.WaitingTasksCount ||
            current.WaitTimeMilliseconds < previous.WaitTimeMilliseconds ||
            current.MaximumWaitTimeMilliseconds < previous.MaximumWaitTimeMilliseconds ||
            current.SignalWaitTimeMilliseconds < previous.SignalWaitTimeMilliseconds;
        return reset
            ? new ServerWaitCounterDelta(true, null, null, null)
            : new ServerWaitCounterDelta(
                false,
                checked(current.WaitingTasksCount - previous.WaitingTasksCount),
                checked(current.WaitTimeMilliseconds - previous.WaitTimeMilliseconds),
                checked(current.SignalWaitTimeMilliseconds - previous.SignalWaitTimeMilliseconds));
    }
}

public enum BlockingBlockerKind
{
    Session = 1,
    OrphanedDistributedTransaction = 2,
    DeferredRecovery = 3,
    Undetermined = 4,
    AsyncLatch = 5,
    Other = 6,
}

public enum BlockingChainState
{
    Resolved = 1,
    Cycle = 2,
    DepthLimit = 3,
    ExternalBlocker = 4,
}

public sealed class BlockingEdgeObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 128;

    public BlockingEdgeObservation(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
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
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(waitType);
        ActivitySessionObservation.RequireSessionId(blockedSessionId, nameof(blockedSessionId));
        if (!Enum.IsDefined(blockerKind) || !Enum.IsDefined(chainState))
        {
            throw new ArgumentOutOfRangeException(nameof(blockerKind));
        }

        if ((blockerKind == BlockingBlockerKind.Session) != (blockerSessionId is not null))
        {
            throw new ArgumentException("Only a session blocker carries a positive session identifier.", nameof(blockerSessionId));
        }

        if (blockerSessionId is { } blocker)
        {
            ActivitySessionObservation.RequireSessionId(blocker, nameof(blockerSessionId));
        }

        if (rootBlockerSessionId is { } root)
        {
            ActivitySessionObservation.RequireSessionId(root, nameof(rootBlockerSessionId));
        }

        ActivitySessionObservation.RequireNonNegative(waitingTaskCount, nameof(waitingTaskCount));
        ArgumentOutOfRangeException.ThrowIfZero(waitingTaskCount);

        ActivitySessionObservation.RequireNonNegative(waitDurationMilliseconds, nameof(waitDurationMilliseconds));
        if (chainDepth is < 1 or > BlockingChainLimits.MaximumDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(chainDepth));
        }

        TargetId = targetId;
        TargetRevision = targetRevision;
        BlockedSessionId = blockedSessionId;
        BlockerKind = blockerKind;
        BlockerSessionId = blockerSessionId;
        WaitType = waitType;
        WaitingTaskCount = waitingTaskCount;
        WaitDurationMilliseconds = waitDurationMilliseconds;
        RootBlockerSessionId = rootBlockerSessionId;
        ChainDepth = chainDepth;
        ChainState = chainState;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
        EstimatedSizeBytes = checked(FixedEstimatedBytes + waitType.Value.Length);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public int BlockedSessionId { get; }
    public BlockingBlockerKind BlockerKind { get; }
    public int? BlockerSessionId { get; }
    public SqlServerWaitType WaitType { get; }
    public long WaitingTaskCount { get; }
    public long WaitDurationMilliseconds { get; }
    public int? RootBlockerSessionId { get; }
    public int ChainDepth { get; }
    public BlockingChainState ChainState { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes { get; }
}

public static class BlockingChainLimits
{
    public const int MaximumDepth = 32;
    public const int MaximumNodes = 256;
}

public sealed class ActivitySessionObservationBatch : ObservationBatch<ActivitySessionObservation>
{
    public const int MaximumItems = 512;

    public ActivitySessionObservationBatch(IReadOnlyList<ActivitySessionObservation> items)
        : base(items, MaximumItems, static item => (item.SessionId, 0), "session")
    {
    }
}

public sealed class ActivityRequestObservationBatch : ObservationBatch<ActivityRequestObservation>
{
    public const int MaximumItems = 512;

    public ActivityRequestObservationBatch(IReadOnlyList<ActivityRequestObservation> items)
        : base(items, MaximumItems, static item => (item.SessionId, item.RequestId), "request")
    {
    }
}

public sealed class ServerWaitObservationBatch : ObservationBatch<ServerWaitObservation>
{
    public const int MaximumItems = 2_048;

    public ServerWaitObservationBatch(IReadOnlyList<ServerWaitObservation> items)
        : base(items, MaximumItems, static item => item.WaitType.Value, "server wait")
    {
    }
}

public sealed class BlockingEdgeObservationBatch : ObservationBatch<BlockingEdgeObservation>
{
    public const int MaximumItems = 1_024;

    public BlockingEdgeObservationBatch(IReadOnlyList<BlockingEdgeObservation> items)
        : base(
            items,
            MaximumItems,
            static item => (item.BlockedSessionId, item.BlockerKind, item.BlockerSessionId, item.WaitType.Value),
            "blocking edge")
    {
    }
}

public abstract class ObservationBatch<T>
    where T : class, IIngestionRecord
{
    private readonly ReadOnlyCollection<T> _items;

    private protected ObservationBatch(
        IReadOnlyList<T> items,
        int maximumItems,
        Func<T, object> identity,
        string description)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(identity);
        if (items.Count > maximumItems)
        {
            throw new ArgumentException($"A {description} observation batch cannot exceed {maximumItems} items.", nameof(items));
        }

        var copy = new T[items.Count];
        var identities = new HashSet<object>();
        for (int index = 0; index < items.Count; index++)
        {
            T item = items[index] ?? throw new ArgumentException(
                $"A {description} observation batch cannot contain null items.", nameof(items));
            if (!identities.Add(identity(item)))
            {
                throw new ArgumentException($"{description} observations must have unique identities.", nameof(items));
            }

            copy[index] = item;
        }

        _items = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<T> Items => _items;
}
