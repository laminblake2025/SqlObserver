using SqlObserver.Domain.Coordination;

namespace SqlObserver.Application.Ports;

public enum SystemRetentionStepOutcome
{
    Idle,
    Detached,
    Dropped,
    Reattached,
    GraceStarted,
    Cancelled,
    Retry,
    Failed,
}

public enum QueryRunRetentionStepOutcome
{
    Idle,
    Scanned,
    Held,
    Oversize,
    Retired,
    Missing,
}

public enum OriginalCopyRetentionStepOutcome
{
    Idle,
    Scanned,
    Retired,
    Blocked,
}

/// <summary>Bounded retention operations, each fenced by the dedicated maintenance lease.</summary>
public interface IRetentionMaintenancePort
{
    ValueTask<SystemRetentionStepOutcome> RunSystemRetentionStepAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<QueryRunRetentionStepOutcome> RunQueryRunRetentionStepAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<OriginalCopyRetentionStepOutcome> RunOriginalCopyRetentionStepAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<int> PruneOrphanQueryTextPayloadsAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<int> PruneOrphanQueryPlanPayloadsAsync(WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}
