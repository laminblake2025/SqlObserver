namespace SqlObserver.Collector;

internal enum M10MaintenanceAttemptState
{
    Skipped = 0,
    Succeeded = 1,
    Failed = 2,
}

internal readonly record struct M10MaintenanceAttempt(
    M10MaintenanceAttemptState State,
    int? CheckedCount,
    string? FailureType);

/// <summary>Uses monotonic time so M10 maintenance is not reset by UTC clock changes.</summary>
internal sealed class M10PartitionMaintenanceCadence
{
    internal static readonly TimeSpan SuccessInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private long? _lastResultTimestamp;
    private bool _lastResultSucceeded;

    internal M10PartitionMaintenanceCadence(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal bool IsDue()
    {
        if (_lastResultTimestamp is null) return true;
        TimeSpan interval = _lastResultSucceeded ? SuccessInterval : FailureRetryInterval;
        return _timeProvider.GetElapsedTime(_lastResultTimestamp.Value) >= interval;
    }

    internal void RecordSuccess()
    {
        _lastResultTimestamp = _timeProvider.GetTimestamp();
        _lastResultSucceeded = true;
    }

    internal void RecordFailure()
    {
        _lastResultTimestamp = _timeProvider.GetTimestamp();
        _lastResultSucceeded = false;
    }
}

/// <summary>Runs one bounded M10 attempt and converts non-cancellation failures into a safe result.</summary>
internal sealed class M10PartitionMaintenanceCoordinator
{
    private readonly M10PartitionMaintenanceCadence _cadence;

    internal M10PartitionMaintenanceCoordinator(TimeProvider timeProvider) =>
        _cadence = new M10PartitionMaintenanceCadence(timeProvider);

    internal async ValueTask<M10MaintenanceAttempt> TryRunAsync(
        Func<CancellationToken, ValueTask<int>> maintenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_cadence.IsDue()) return new(M10MaintenanceAttemptState.Skipped, null, null);

        try
        {
            int checkedCount = await maintenance(cancellationToken).ConfigureAwait(false);
            _cadence.RecordSuccess();
            return new(M10MaintenanceAttemptState.Succeeded, checkedCount, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _cadence.RecordFailure();
            return new(M10MaintenanceAttemptState.Failed, null, exception.GetType().Name);
        }
    }
}
