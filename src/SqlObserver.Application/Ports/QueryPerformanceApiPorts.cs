using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record QueryPerformanceStatusRequest(MonitoredInstanceId TargetId, DateTimeOffset FromUtc, DateTimeOffset ToUtc, RepositoryCallTimeout Timeout);
public sealed record TopQueryRequest(MonitoredInstanceId TargetId, DateTimeOffset FromUtc, DateTimeOffset ToUtc, QueryPerformanceMetric Metric, int Limit, QueryPerformanceCursorEnvelope? Cursor, RepositoryCallTimeout Timeout);
public sealed record QueryHistoryRequest(MonitoredInstanceId TargetId, QueryOpaqueIdentity Query, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, QueryPerformanceCursorEnvelope? Cursor, RepositoryCallTimeout Timeout);
public sealed record QueryPlanMetadataRequest(MonitoredInstanceId TargetId, PlanOpaqueIdentity Plan, RepositoryCallTimeout Timeout);
public sealed record TopQueryPage(IReadOnlyList<TopQueryDto> Items, bool HasMore, DateTimeOffset SnapshotUtc);
public sealed record QueryHistoryPage(IReadOnlyList<QueryHistoryDto> Items, bool HasMore, DateTimeOffset SnapshotUtc);
public interface IQueryPerformanceApiRepositoryPort
{
    ValueTask<QueryPerformanceStatusDto?> GetStatusAsync(QueryPerformanceStatusRequest request, CancellationToken cancellationToken);
    ValueTask<TopQueryPage> GetTopAsync(TopQueryRequest request, CancellationToken cancellationToken);
    ValueTask<QueryHistoryPage> GetHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken);
    ValueTask<QueryPlanMetadataDto?> GetPlanAsync(QueryPlanMetadataRequest request, CancellationToken cancellationToken);
}
public interface IQueryPerformanceApiQueryService
{
    ValueTask<QueryPerformanceStatusDto?> GetStatusAsync(AuthorizationContext authorization, QueryPerformanceStatusRequest request, CancellationToken cancellationToken);
    ValueTask<TopQueryPage> GetTopAsync(AuthorizationContext authorization, TopQueryRequest request, CancellationToken cancellationToken);
    ValueTask<QueryHistoryPage> GetHistoryAsync(AuthorizationContext authorization, QueryHistoryRequest request, CancellationToken cancellationToken);
    ValueTask<QueryPlanMetadataDto?> GetPlanAsync(AuthorizationContext authorization, QueryPlanMetadataRequest request, CancellationToken cancellationToken);
}
