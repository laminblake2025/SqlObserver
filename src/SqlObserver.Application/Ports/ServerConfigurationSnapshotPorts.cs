using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record ServerConfigurationSnapshotCursor(
    MonitoredInstanceId TargetId, long TargetRevision, Guid RunId,
    int ConfigurationId);

public sealed class ServerConfigurationSnapshotRequest
{
    public ServerConfigurationSnapshotRequest(MonitoredInstanceId targetId, int limit,
        RepositoryCallTimeout timeout, ServerConfigurationSnapshotCursor? cursor = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (cursor is not null && (cursor.TargetId != targetId ||
            cursor.TargetRevision < 1 || cursor.RunId == Guid.Empty ||
            cursor.ConfigurationId is < 1 or > 1_000_000))
            throw new ArgumentException("Configuration snapshot cursor does not match the request.",
                nameof(cursor));
        Limit = limit;
        Cursor = cursor;
    }

    public MonitoredInstanceId TargetId { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }
    public ServerConfigurationSnapshotCursor? Cursor { get; }
}

public sealed record ServerConfigurationSetting(
    int ConfigurationId, string ConfigurationName, string SourceType,
    string ConfiguredValue, string InUseValue, bool IsDynamic, bool IsAdvanced);

public sealed record ServerConfigurationSnapshotPage(
    MonitoredInstanceId TargetId, long TargetRevision, Guid? RunId,
    DateTimeOffset? ObservedAtUtc, short? ServerMajorVersion,
    bool CollectorEnabled, string? LastOutcome,
    IReadOnlyList<ServerConfigurationSetting> Items,
    ServerConfigurationSnapshotCursor? NextCursor);

public sealed class ServerConfigurationSnapshotCursorStaleException : Exception
{
    public ServerConfigurationSnapshotCursorStaleException(Exception innerException)
        : base("Configuration snapshot changed; restart paging from the first page.",
            innerException) { }
}

public interface IServerConfigurationSnapshotRepositoryPort
{
    ValueTask<ServerConfigurationSnapshotPage> ReadAsync(
        ServerConfigurationSnapshotRequest request, CancellationToken cancellationToken);
}
