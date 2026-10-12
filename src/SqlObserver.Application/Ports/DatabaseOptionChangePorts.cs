using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class DatabaseOptionChangeRequest
{
    public DatabaseOptionChangeRequest(MonitoredInstanceId targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit,
        RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7) ||
            limit is < 1 or > 200)
            throw new ArgumentException("Database option history requires a UTC window of at most seven days and at most 200 changes.");
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Limit = limit;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed record DatabaseOptionChange(int DatabaseId, string DatabaseName,
    string OptionKey, string PreviousValue, string CurrentValue,
    DateTimeOffset PreviousObservedAtUtc, DateTimeOffset ObservedAtUtc,
    DateTimeOffset DetectedAtUtc, Guid CollectionRunId);

public sealed record DatabaseOptionChangeResult(MonitoredInstanceId TargetId,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    IReadOnlyList<DatabaseOptionChange> Items);

public interface IDatabaseOptionChangeRepositoryPort
{
    ValueTask<DatabaseOptionChangeResult> ReadAsync(
        DatabaseOptionChangeRequest request, CancellationToken cancellationToken);
}
