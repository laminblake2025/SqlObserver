using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IIncidentListQueryService
{
    ValueTask<IncidentListPage> ListAsync(IncidentListQuery query, CancellationToken cancellationToken);
}

/// <summary>Role-scoped, bounded discovery of incident identities without evidence payloads.</summary>
public sealed class IncidentListQueryService(IIncidentListProjectionRepositoryPort repository) : IIncidentListQueryService
{
    private readonly IIncidentListProjectionRepositoryPort repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async ValueTask<IncidentListPage> ListAsync(IncidentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Authorization);
        query.Authorization.RequireAny(query.TargetId, ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        if (query.Timeout is null || query.Limit is < 1 or > 100 || query.FromUtc.Offset != TimeSpan.Zero ||
            query.ToUtc.Offset != TimeSpan.Zero || query.ToUtc <= query.FromUtc || query.ToUtc - query.FromUtc > TimeSpan.FromDays(31) ||
            query.SnapshotUtc is { Offset: var offset } && offset != TimeSpan.Zero || query.TargetRevision is { Value: < 1 })
            throw new ArgumentException("Incident listing bounds are invalid.", nameof(query));

        if (query.Cursor is { } cursor && (cursor.TargetId != query.TargetId || cursor.FromUtc != query.FromUtc ||
            cursor.ToUtc != query.ToUtc || query.TargetRevision is { } revision && revision != cursor.TargetRevision ||
            query.SnapshotUtc is { } snapshot && snapshot != cursor.SnapshotUtc))
            throw new ArgumentException("Incident cursor does not match the query.", nameof(query));
        IncidentListQuery effective = query.Cursor is { } frozen
            ? query with { TargetRevision = frozen.TargetRevision, SnapshotUtc = frozen.SnapshotUtc }
            : query;
        IncidentListPage result = await repository.ListIncidentsAsync(effective, cancellationToken).ConfigureAwait(false);
        ValidateResult(result, effective);
        return result;
    }

    private static void ValidateResult(IncidentListPage result, IncidentListQuery query)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TargetId != query.TargetId || result.FromUtc != query.FromUtc || result.ToUtc != query.ToUtc ||
            result.TargetRevision is null or { Value: < 1 } || query.TargetRevision is { } revision && result.TargetRevision != revision ||
            result.SnapshotUtc.Offset != TimeSpan.Zero || query.SnapshotUtc is { } snapshot && result.SnapshotUtc != snapshot ||
            result.PublicationRevision < 0 || query.Cursor is { } requested && result.PublicationRevision != requested.PublicationRevision ||
            result.Items is null || result.Items.Count > query.Limit || result.Items.Count > 0 && result.PublicationRevision == 0 ||
            result.HasMore != (result.NextCursor is not null) || result.HasMore && result.Items.Count != query.Limit)
            throw new InvalidDataException("Incident listing exceeded its projection contract.");

        (DateTimeOffset At, Guid Id)? previous = query.Cursor is { } cursor ? (cursor.OpenedAtUtc, cursor.ThreadId) : null;
        foreach (IncidentListItem item in result.Items)
        {
            if (item is null || item.ThreadId == Guid.Empty || item.OpenedAtUtc.Offset != TimeSpan.Zero ||
                item.OpenedAtUtc < query.FromUtc || item.OpenedAtUtc >= query.ToUtc || item.OpenedAtUtc > result.SnapshotUtc ||
                item.GenerationCount < 0 || (item.GenerationCount == 0) != (item.LatestGenerationObservedAtUtc is null) ||
                item.LatestGenerationObservedAtUtc is { } latest && (latest.Offset != TimeSpan.Zero || latest > result.SnapshotUtc) ||
                previous is { } key && key.CompareTo((item.OpenedAtUtc, item.ThreadId)) >= 0)
                throw new InvalidDataException("Incident listing returned invalid or unordered metadata.");
            previous = (item.OpenedAtUtc, item.ThreadId);
        }
        if (result.NextCursor is { } next && (next.TargetId != result.TargetId || next.TargetRevision != result.TargetRevision ||
            next.FromUtc != result.FromUtc || next.ToUtc != result.ToUtc || next.SnapshotUtc != result.SnapshotUtc ||
            next.PublicationRevision != result.PublicationRevision || next.OpenedAtUtc != result.Items[^1].OpenedAtUtc ||
            next.ThreadId != result.Items[^1].ThreadId))
            throw new InvalidDataException("Incident listing continuation is invalid.");
        McpQueryValidation.EnsureResponseSize(result);
    }
}
