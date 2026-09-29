using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record OverviewQuery(AuthorizationContext Authorization, Guid? TargetId, DateTimeOffset FromUtc, DateTimeOffset ToUtc);
public sealed record OverviewTarget(Guid TargetId, string DisplayName, string Lifecycle);
public sealed record OverviewValue(double? Value, string State, DateTimeOffset? ObservedAtUtc);
public sealed record OverviewPoint(DateTimeOffset TimeUtc, double? Value, int Samples);
public sealed record OverviewSeries(Guid TargetId, string Label, string Metric, string Unit, string State, string? Dimension, IReadOnlyList<OverviewPoint> Points);
public sealed record OverviewIssue(Guid TargetId, string Server, string Title, string Detail, string Destination, int Priority, DateTimeOffset? ObservedAtUtc);
public sealed record OverviewResource(Guid TargetId, string Server, string Label, double? Value, string Unit, string State, DateTimeOffset? ObservedAtUtc);
public sealed record OverviewTargetEvidence(Guid TargetId, string DisplayName, string CollectionState, DateTimeOffset? LastObservedUtc,
    OverviewValue ActiveAlerts, OverviewValue BlockedSessions, OverviewValue Deadlocks,
    IReadOnlyList<OverviewIssue> Issues, IReadOnlyList<OverviewResource> Resources, IReadOnlyList<OverviewSeries> Series, IReadOnlyList<string> Gaps);
public sealed record OverviewSnapshot(DateTimeOffset RefreshedAtUtc, DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid? TargetId,
    IReadOnlyList<OverviewTarget> Targets, int ExcludedTargets, IReadOnlyList<OverviewTargetEvidence> Evidence);
public interface IOverviewQueryService
{
    Task<OverviewSnapshot> ReadAsync(OverviewQuery query, CancellationToken cancellationToken);
}

/// <summary>Current evidence published independently of any requested history window.</summary>
public sealed record OverviewLatestSource(string State, DateTimeOffset? ObservedAtUtc);
public sealed record OverviewLatestStatus(
    Guid TargetId, long TargetRevision, long Generation, DateTimeOffset PublishedAtUtc,
    string CollectionState, DateTimeOffset? LastSuccessAtUtc,
    OverviewValue ActiveAlerts, OverviewValue BlockedSessions,
    IReadOnlyDictionary<string, OverviewLatestSource> Sources,
    IReadOnlyList<OverviewIssue> Issues, IReadOnlyList<OverviewResource> Resources,
    IReadOnlyList<string> Gaps);

/// <summary>Reads one target-scoped, revision-bound latest-status row from the repository.</summary>
public interface IOverviewLatestStatusRepositoryPort
{
    Task<OverviewLatestStatus?> ReadAsync(
        MonitoredInstanceId targetId, long targetRevision, DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken);
}

public sealed record OverviewRefreshTarget(MonitoredInstanceId TargetId, long TargetRevision);

/// <summary>Fenced, bounded materialization of committed current Overview evidence.</summary>
public interface IOverviewLatestMaterializationPort
{
    ValueTask<IReadOnlyList<OverviewRefreshTarget>> ListDueAsync(
        WorkerLeaseIdentity lease, int limit, CancellationToken cancellationToken);
    ValueTask<bool> RefreshCoreAsync(
        OverviewRefreshTarget target, WorkerLeaseIdentity lease, CancellationToken cancellationToken);
}

/// <summary>Reads bounded overview history from persisted observations, never from a monitored server.</summary>
public interface IOverviewHistoryRepositoryPort
{
    Task<IReadOnlyList<OverviewSeries>> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset cutoffUtc, CancellationToken cancellationToken);

    /// <summary>Reads database-scoped user-session history for one explicitly selected server.</summary>
    Task<IReadOnlyList<OverviewSeries>> ReadDatabaseActivityAsync(MonitoredInstanceId targetId, long targetRevision,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
