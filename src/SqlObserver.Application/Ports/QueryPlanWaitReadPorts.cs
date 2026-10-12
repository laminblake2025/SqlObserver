using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record QueryPlanWaitSnapshot(
    IReadOnlyList<QueryWaitCategory> Categories, DateTimeOffset CapturedAtUtc);

public sealed record QueryPlanWaitReadResult(
    string Status, IReadOnlyList<QueryWaitCategory>? Categories,
    DateTimeOffset? CapturedAtUtc);

public interface IQueryPlanWaitReadRepositoryPort
{
    ValueTask<QueryPlanWaitSnapshot?> ReadAsync(
        QueryPlanReadRequest request, CancellationToken cancellationToken);
}

public interface IQueryPlanWaitReadService
{
    ValueTask<QueryPlanWaitReadResult> ReadAsync(
        AuthorizationContext authorization, QueryPlanReadRequest request,
        CancellationToken cancellationToken);
}
