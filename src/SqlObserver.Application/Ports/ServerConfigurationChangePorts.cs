using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record ServerConfigurationChangeCursor(
    MonitoredInstanceId TargetId, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    long TargetRevision, long PublicationGeneration, DateTimeOffset DetectedAtUtc,
    Guid RunId, int ConfigurationId);

public sealed class ServerConfigurationChangeRequest
{
    public ServerConfigurationChangeRequest(MonitoredInstanceId targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit,
        RepositoryCallTimeout timeout, ServerConfigurationChangeCursor? cursor = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7) ||
            limit is < 1 or > 100)
            throw new ArgumentException("Configuration history requires a UTC window of at most seven days and a page of at most 100 changes.");
        if (cursor is not null && (cursor.TargetId != targetId ||
            cursor.FromUtc != fromUtc || cursor.ToUtc != toUtc ||
            cursor.TargetRevision < 1 || cursor.PublicationGeneration < 0 ||
            cursor.DetectedAtUtc < fromUtc || cursor.DetectedAtUtc >= toUtc ||
            cursor.RunId == Guid.Empty || cursor.ConfigurationId is < 1 or > 1_000_000))
            throw new ArgumentException("Configuration history cursor does not match the request.", nameof(cursor));
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Limit = limit;
        Cursor = cursor;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }
    public ServerConfigurationChangeCursor? Cursor { get; }
}

public sealed record ServerConfigurationChange(
    int ConfigurationId, string ConfigurationName, string SourceType,
    string PreviousConfiguredValue, string ConfiguredValue,
    string PreviousInUseValue, string InUseValue,
    bool ConfiguredChanged, bool InUseChanged,
    DateTimeOffset PreviousObservedAtUtc, DateTimeOffset ObservedAtUtc,
    DateTimeOffset DetectedAtUtc, Guid RunId);

public sealed record ServerConfigurationChangePage(
    MonitoredInstanceId TargetId, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    long TargetRevision, long PublicationGeneration,
    IReadOnlyList<ServerConfigurationChange> Items,
    ServerConfigurationChangeCursor? NextCursor);

public sealed class ServerConfigurationCursorStaleException : Exception
{
    public ServerConfigurationCursorStaleException(Exception innerException)
        : base("Configuration history changed; restart paging from the first page.", innerException) { }
}

public interface IServerConfigurationChangeRepositoryPort
{
    ValueTask<ServerConfigurationChangePage> ReadAsync(
        ServerConfigurationChangeRequest request, CancellationToken cancellationToken);
}
