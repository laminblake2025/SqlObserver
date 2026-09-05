using SqlObserver.Domain.Authorization;
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

/// <summary>Reads bounded SQL core history from persisted observations, never from a monitored server.</summary>
public interface IOverviewHistoryRepositoryPort
{
    Task<IReadOnlyList<OverviewSeries>> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
