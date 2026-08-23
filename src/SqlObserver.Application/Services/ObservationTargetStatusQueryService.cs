using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class GetObservationTargetStatusQuery
{
    public GetObservationTargetStatusQuery(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        Authorization = authorization;
        TargetId = targetId;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }

    public MonitoredInstanceId TargetId { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ObservationTargetStatusSnapshot
{
    public ObservationTargetStatusSnapshot(
        ObservationTarget target,
        CapabilityProfile? latestCapabilityProfile)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (latestCapabilityProfile is not null && latestCapabilityProfile.TargetId != target.TargetId)
        {
            throw new ArgumentException("The capability profile must belong to the target.", nameof(latestCapabilityProfile));
        }

        Target = target;
        LatestCapabilityProfile = latestCapabilityProfile;
    }

    public ObservationTarget Target { get; }

    public CapabilityProfile? LatestCapabilityProfile { get; }

    public bool CapabilityProfileIsCurrent =>
        LatestCapabilityProfile?.TargetRevision == Target.Revision;
}

public interface IObservationTargetStatusQueryService
{
    ValueTask<ObservationTargetStatusSnapshot?> GetAsync(
        GetObservationTargetStatusQuery query,
        CancellationToken cancellationToken);

    ValueTask<ObservationTargetStatusPage> ListAsync(
        ListObservationTargetsQuery query,
        CancellationToken cancellationToken);
}

public sealed class ObservationTargetStatusPage
{
    private readonly IReadOnlyList<ObservationTargetStatusSnapshot> _targets;

    public ObservationTargetStatusPage(
        IReadOnlyList<ObservationTargetStatusSnapshot> targets,
        ObservationTargetListCursor? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count > ListObservationTargetsRequest.MaximumResults)
        {
            throw new ArgumentException(
                $"A target-status page cannot exceed {ListObservationTargetsRequest.MaximumResults} entries.",
                nameof(targets));
        }

        var copy = new ObservationTargetStatusSnapshot[targets.Count];
        for (int index = 0; index < targets.Count; index++)
        {
            copy[index] = targets[index] ?? throw new ArgumentException(
                "A target-status page cannot contain null entries.",
                nameof(targets));
        }

        _targets = Array.AsReadOnly(copy);
        NextCursor = nextCursor;
    }

    public IReadOnlyList<ObservationTargetStatusSnapshot> Targets => _targets;

    public ObservationTargetListCursor? NextCursor { get; }
}

public sealed class ObservationTargetStatusQueryService : IObservationTargetStatusQueryService
{
    private readonly IObservationTargetRepositoryPort _targets;
    private readonly ICapabilityProfileRepositoryPort _profiles;

    public ObservationTargetStatusQueryService(
        IObservationTargetRepositoryPort targets,
        ICapabilityProfileRepositoryPort profiles)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public async ValueTask<ObservationTargetStatusSnapshot?> GetAsync(
        GetObservationTargetStatusQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        TargetAuthorizationScope authorizedScope = ObservationTargetQueryService.GetAuthorizedScope(
            query.Authorization,
            includeRetired: false);
        if (!authorizedScope.Contains(query.TargetId))
        {
            throw new UnauthorizedAccessException("The principal is not authorized for this target projection.");
        }

        ObservationTarget? target = await _targets.GetAsync(
                new GetObservationTargetRequest(query.TargetId, query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return null;
        }

        if (target.TargetId != query.TargetId)
        {
            throw new InvalidDataException("The target repository returned a different target than requested.");
        }

        if (target.Lifecycle == ObservationTargetLifecycle.Retired &&
            !ObservationTargetQueryService
                .GetAuthorizedScope(query.Authorization, includeRetired: true)
                .Contains(target.TargetId))
        {
            throw new UnauthorizedAccessException("The principal is not authorized for this target projection.");
        }

        CapabilityProfile? profile = await _profiles.GetLatestAsync(
                new GetLatestCapabilityProfileRequest(query.TargetId, query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);
        return new ObservationTargetStatusSnapshot(target, profile);
    }

    public async ValueTask<ObservationTargetStatusPage> ListAsync(
        ListObservationTargetsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        TargetAuthorizationScope authorizedScope = ObservationTargetQueryService.GetAuthorizedScope(
            query.Authorization,
            query.IncludeRetired);

        ObservationTargetPage page = await _targets.ListObservationTargetsAsync(
                new ListObservationTargetsRequest(
                    authorizedScope,
                    query.MaxResults,
                    query.Cursor,
                    query.IncludeRetired,
                    query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);

        if (page.Targets.Any(target => !authorizedScope.Contains(target.TargetId)))
        {
            throw new InvalidDataException("The target repository returned a target outside the authorized scope.");
        }

        if (page.Targets.Count == 0)
        {
            return new ObservationTargetStatusPage([], page.NextCursor);
        }

        CapabilityProfileBatch batch = await _profiles.GetLatestForTargetsAsync(
                new GetLatestCapabilityProfilesRequest(
                    page.Targets.Select(static target => target.TargetId).ToArray(),
                    query.Timeout),
                cancellationToken)
            .ConfigureAwait(false);
        var profilesByTarget = batch.Profiles.ToDictionary(
            static profile => profile.TargetId.Value);

        if (profilesByTarget.Keys.Any(targetId =>
                page.Targets.All(target => target.TargetId.Value != targetId)))
        {
            throw new InvalidDataException("The profile repository returned a target outside the requested page.");
        }

        ObservationTargetStatusSnapshot[] snapshots = page.Targets
            .Select(target => new ObservationTargetStatusSnapshot(
                target,
                profilesByTarget.GetValueOrDefault(target.TargetId.Value)))
            .ToArray();
        return new ObservationTargetStatusPage(snapshots, page.NextCursor);
    }

}
