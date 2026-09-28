using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IBufferNodePleWindowQueryService
{
    ValueTask<BufferNodePleWindowResult> ReadAsync(AuthorizationContext authorization,
        BufferNodePleWindowRequest request, CancellationToken cancellationToken);
}

public sealed class BufferNodePleWindowQueryService(IBufferNodePleWindowRepositoryPort repository)
    : IBufferNodePleWindowQueryService
{
    public async ValueTask<BufferNodePleWindowResult> ReadAsync(AuthorizationContext authorization,
        BufferNodePleWindowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        BufferNodePleWindowResult result = await repository.ReadAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (result.TargetId != request.TargetId || result.FromUtc != request.FromUtc ||
            result.ToUtc != request.ToUtc || result.VisibilityGapCount < 0 ||
            result.Nodes.Count > 128 || result.Points.Count > 169 ||
            result.AggregatePoints.Count > 169 ||
            (result.SelectedNodeId is null && result.Points.Count > 0) ||
            result.SelectedNodeId != (request.NodeId ??
                (result.Nodes.Count == 0 ? null : result.Nodes[0].NodeId)) ||
            result.Nodes.Select(static node => node.NodeId).Distinct().Count() != result.Nodes.Count ||
            result.Nodes.Any(node => node.NodeId is < 0 or > 1023 || node.SampleCount <= 0 ||
                node.LastObservedAtUtc.Offset != TimeSpan.Zero ||
                node.LastObservedAtUtc < request.FromUtc || node.LastObservedAtUtc >= request.ToUtc) ||
            InvalidPoints(result.Points, request) ||
            InvalidPoints(result.AggregatePoints, request))
            throw new InvalidDataException("Buffer Node PLE exceeded its target, window, or gauge bounds.");
        return result;
    }

    private static bool InvalidPoints(IReadOnlyList<BufferNodePlePoint> points,
        BufferNodePleWindowRequest request) =>
        points.Any(point => point.BucketStartUtc.Offset != TimeSpan.Zero ||
            point.BucketStartUtc < request.FromUtc.AddHours(-1) ||
            point.BucketStartUtc >= request.ToUtc || point.SampleCount <= 0 ||
            !double.IsFinite(point.MinimumSeconds) ||
            !double.IsFinite(point.AverageSeconds) ||
            !double.IsFinite(point.MaximumSeconds) ||
            point.MinimumSeconds < 0 || point.MinimumSeconds > point.AverageSeconds ||
            point.AverageSeconds > point.MaximumSeconds) ||
        points.Zip(points.Skip(1)).Any(static pair =>
            pair.First.BucketStartUtc >= pair.Second.BucketStartUtc);
}
