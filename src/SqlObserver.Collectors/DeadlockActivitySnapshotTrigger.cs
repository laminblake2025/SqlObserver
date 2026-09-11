using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collectors;

/// <summary>Captures one bounded activity snapshot for newly observed deadlock evidence.</summary>
public interface IDeadlockActivitySnapshotTrigger
{
    Task TriggerAsync(
        CollectorDueWorkItem work,
        DeadlockObservationBatch deadlocks,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs event-triggered activity captures independently from the normal cadence lease.
/// The repository keeps the event identity unique, so retries and competing collector
/// processes cannot create a second durable snapshot for the same deadlock.
/// </summary>
public sealed class DeadlockActivitySnapshotTrigger(
    ILiveActivityCollector collector,
    ILiveActivityRepository repository,
    IWorkerLeasePort leases,
    WorkerExecutionId owner) : IDeadlockActivitySnapshotTrigger
{
    private const string CollectorId = "deadlocks.system-health";
    private const string LeasePrefix = "collector/live-activity-deadlock/";
    private const int MaximumSnapshotsPerRun = 4;
    private static readonly TimeSpan MaximumEventAge = TimeSpan.FromMinutes(5);
    private static readonly WorkerLeaseDuration LeaseDuration = new(TimeSpan.FromMinutes(2));
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly Meter Metrics = new("SqlObserver.Collectors.DeadlockActivitySnapshot");
    private static readonly Counter<long> TriggeredCount = Metrics.CreateCounter<long>(
        "sqlobserver.deadlock.activity_snapshot.triggered");
    private static readonly Counter<long> SkippedCount = Metrics.CreateCounter<long>(
        "sqlobserver.deadlock.activity_snapshot.skipped");
    private static readonly Counter<long> FailureCount = Metrics.CreateCounter<long>(
        "sqlobserver.deadlock.activity_snapshot.failures");

    private readonly ConcurrentDictionary<(Guid TargetId, long Revision, Guid EventId), byte> inFlight = new();

    public async Task TriggerAsync(
        CollectorDueWorkItem work,
        DeadlockObservationBatch deadlocks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(deadlocks);
        if (!string.Equals(work.CollectorId.Value, CollectorId, StringComparison.Ordinal) ||
            deadlocks.Items.Count == 0)
        {
            return;
        }

        LiveActivityTarget target = new(
            work.TargetId.Value,
            work.TargetRevision.Value,
            work.ConnectionPolicy);
        DeadlockObservation[] candidates = deadlocks.Items
            .Where(observation => observation.OccurredAtUtc >= work.RepositoryTimeUtc - MaximumEventAge)
            // Drain the oldest eligible events first so a burst cannot starve
            // an event at the edge of the bounded discovery window.
            .OrderBy(static observation => observation.OccurredAtUtc)
            .ThenBy(static observation => observation.EventId)
            .Take(MaximumSnapshotsPerRun)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        WorkerLeaseIdentity? lease = null;
        try
        {
            foreach (DeadlockObservation observation in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (target.Id, target.Revision, observation.EventId);
                if (!inFlight.TryAdd(key, 0))
                {
                    SkippedCount.Add(1, Tag(work));
                    continue;
                }

                try
                {
                    if (await repository.HasDeadlockSnapshotAsync(
                            target.Id,
                            observation.EventId,
                            cancellationToken).ConfigureAwait(false))
                    {
                        SkippedCount.Add(1, Tag(work));
                        continue;
                    }

                    if (lease is null)
                    {
                        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
                                new AcquireWorkerLeaseRequest(
                                    new WorkerLeaseKey(LeasePrefix + target.Id.ToString("D")),
                                    owner,
                                    LeaseDuration,
                                    RepositoryTimeout),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (acquired.Status != LeaseAcquisitionStatus.Acquired || acquired.Lease is null)
                        {
                            SkippedCount.Add(1, Tag(work));
                            return;
                        }

                        lease = acquired.Lease.Identity;
                    }

                    LiveActivityCapture capture = await collector
                        .CollectAsync(target, cancellationToken)
                        .ConfigureAwait(false);
                    capture = capture with
                    {
                        // The DMV query starts at the capture time. Preserve that the
                        // event snapshot was taken after the event, not at its occurrence.
                        ObservedUtc = DateTimeOffset.UtcNow,
                        DeadlockEventId = observation.EventId,
                        DeadlockOccurredUtc = observation.OccurredAtUtc,
                    };
                    await repository.CommitAsync(target, lease, capture, cancellationToken).ConfigureAwait(false);
                    TriggeredCount.Add(1, Tag(work));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A failed event snapshot must not turn a successfully committed
                    // deadlock observation into a failed collector run. The next due
                    // cycle retries the event while it remains within the trigger window.
                    FailureCount.Add(1, Tag(work));
                    return;
                }
                finally
                {
                    inFlight.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    await leases.ReleaseAsync(
                            new ReleaseWorkerLeaseRequest(lease, RepositoryTimeout),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    FailureCount.Add(1, Tag(work));
                }
            }
        }
    }

    private static KeyValuePair<string, object?>[] Tag(CollectorDueWorkItem work) =>
    [new("collector.id", work.CollectorId.Value)];
}
