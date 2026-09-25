using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class SqlVolumeReadCursor
{
    public SqlVolumeReadCursor(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        CollectorRunId runId, string afterVolumeKey)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(afterVolumeKey);
        if (afterVolumeKey.Length != 64 || afterVolumeKey.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A SQL volume cursor requires an opaque lowercase key.", nameof(afterVolumeKey));
        TargetId = targetId;
        TargetRevision = targetRevision;
        RunId = runId;
        AfterVolumeKey = afterVolumeKey;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public string AfterVolumeKey { get; }
}

public sealed class SqlVolumeReadRequest
{
    public const int MaximumResults = 100;

    public SqlVolumeReadRequest(MonitoredInstanceId targetId, int maxResults,
        SqlVolumeReadCursor? cursor, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        if (maxResults is < 1 or > MaximumResults) throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (cursor is not null && cursor.TargetId != targetId)
            throw new ArgumentException("The SQL volume cursor belongs to a different target.", nameof(cursor));
        TargetId = targetId;
        MaxResults = maxResults;
        Cursor = cursor;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public int MaxResults { get; }
    public SqlVolumeReadCursor? Cursor { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public enum SqlVolumeEvidenceState { Current, Stale, Partial, Superseded, Unavailable }

public sealed class SqlVolumeReadPage
{
    public SqlVolumeReadPage(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CollectorRunId? snapshotRunId,
        SqlVolumeEvidenceState state, string reason, DateTimeOffset? completedAtUtc,
        string? lossKind, long? minimumLostItems, IReadOnlyList<SqlVolumeObservation> items,
        SqlVolumeReadCursor? nextCursor, DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(items);
        if (!Enum.IsDefined(state) || reason.Length is < 1 or > 128 ||
            reason.Any(static character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
            items.Count > SqlVolumeReadRequest.MaximumResults ||
            items.Any(item => item is null || item.TargetId != targetId || item.TargetRevision != targetRevision) ||
            (snapshotRunId is null && (completedAtUtc is not null || items.Count != 0 || nextCursor is not null)) ||
            (nextCursor is not null && (nextCursor.TargetId != targetId ||
                nextCursor.TargetRevision != targetRevision || nextCursor.RunId != snapshotRunId ||
                items.Count == 0 || nextCursor.AfterVolumeKey != items[^1].VolumeKey)) ||
            (completedAtUtc is not null && completedAtUtc.Value.Offset != TimeSpan.Zero) ||
            repositoryTimeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("SQL volume page evidence is inconsistent.", nameof(items));
        TargetId = targetId;
        TargetRevision = targetRevision;
        SnapshotRunId = snapshotRunId;
        State = state;
        Reason = reason;
        CompletedAtUtc = completedAtUtc;
        LossKind = lossKind;
        MinimumLostItems = minimumLostItems;
        Items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
        RepositoryTimeUtc = repositoryTimeUtc;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId? SnapshotRunId { get; }
    public SqlVolumeEvidenceState State { get; }
    public string Reason { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public string? LossKind { get; }
    public long? MinimumLostItems { get; }
    public IReadOnlyList<SqlVolumeObservation> Items { get; }
    public SqlVolumeReadCursor? NextCursor { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
    public bool HasVisibilityGap => State != SqlVolumeEvidenceState.Current || LossKind is not null and not "none";
}

public interface ISqlVolumeReadRepositoryPort
{
    ValueTask<SqlVolumeReadPage?> ReadAsync(SqlVolumeReadRequest request, CancellationToken cancellationToken);
}

public interface ISqlVolumeReadService
{
    ValueTask<SqlVolumeReadPage?> ReadAsync(AuthorizationContext authorization,
        SqlVolumeReadRequest request, CancellationToken cancellationToken);
}

public sealed class SqlVolumeReadService(ISqlVolumeReadRepositoryPort repository) : ISqlVolumeReadService
{
    public async ValueTask<SqlVolumeReadPage?> ReadAsync(AuthorizationContext authorization,
        SqlVolumeReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        SqlVolumeReadPage? page = await repository.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is not null && (page.TargetId != request.TargetId ||
            page.Items.Count > request.MaxResults ||
            request.Cursor is not null &&
            (page.SnapshotRunId != request.Cursor.RunId ||
             page.TargetRevision != request.Cursor.TargetRevision)))
            throw new InvalidDataException("SQL volume repository returned evidence outside the requested page.");
        return page;
    }
}
