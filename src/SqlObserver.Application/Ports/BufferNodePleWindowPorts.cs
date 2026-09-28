using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class BufferNodePleWindowRequest
{
    public BufferNodePleWindowRequest(MonitoredInstanceId targetId, int? nodeId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        if (nodeId is < 0 or > 1023 || fromUtc.Offset != TimeSpan.Zero ||
            toUtc.Offset != TimeSpan.Zero || toUtc <= fromUtc ||
            toUtc - fromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Buffer Node PLE requires a UTC window of at most seven days and a valid node.");
        NodeId = nodeId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
    }

    public MonitoredInstanceId TargetId { get; }
    public int? NodeId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed record BufferNodePleNode(int NodeId, long SampleCount,
    DateTimeOffset LastObservedAtUtc);

public sealed record BufferNodePlePoint(DateTimeOffset BucketStartUtc, long SampleCount,
    double MinimumSeconds, double AverageSeconds, double MaximumSeconds);

public sealed record BufferNodePleWindowResult(MonitoredInstanceId TargetId,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int? SelectedNodeId,
    long VisibilityGapCount, IReadOnlyList<BufferNodePleNode> Nodes,
    IReadOnlyList<BufferNodePlePoint> Points,
    IReadOnlyList<BufferNodePlePoint> AggregatePoints);

public interface IBufferNodePleWindowRepositoryPort
{
    ValueTask<BufferNodePleWindowResult> ReadAsync(BufferNodePleWindowRequest request,
        CancellationToken cancellationToken);
}
