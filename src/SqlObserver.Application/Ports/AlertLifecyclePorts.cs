using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Ports;

public sealed record AlertLifecycleCursor(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc, string ReadEpoch,
    int Limit, DateTimeOffset AfterEventAtUtc, Guid AfterEventId);
public sealed record AlertLifecycleListRequest(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, int Limit, AlertLifecycleCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record AlertLifecycleExactRequest(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, Guid EventId, Guid AlertId, Guid? EpisodeId, DateTimeOffset EventAtUtc, RepositoryCallTimeout Timeout);
public sealed record AlertLifecyclePage(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch, DateTimeOffset RepositoryTimeUtc,
    string State, string Reason, AlertLifecycleCoverage Coverage, IReadOnlyList<AlertLifecycleEvent> Items, AlertLifecycleCursor? NextAnchor);
public sealed record AlertLifecycleExactPage(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch, DateTimeOffset RepositoryTimeUtc,
    string State, string Reason, AlertLifecycleEvent? Event);
public interface IAlertLifecycleReadPort
{
    ValueTask<AlertLifecyclePage?> ListAsync(AlertLifecycleListRequest request, CancellationToken cancellationToken);
    ValueTask<AlertLifecycleExactPage?> GetAsync(AlertLifecycleExactRequest request, CancellationToken cancellationToken);
}
public interface IAlertLifecycleService
{
    ValueTask<AlertLifecyclePage?> ListAsync(AuthorizationContext authorization, AlertLifecycleListRequest request, CancellationToken cancellationToken);
    ValueTask<AlertLifecycleExactPage?> GetAsync(AuthorizationContext authorization, AlertLifecycleExactRequest request, CancellationToken cancellationToken);
}
public sealed class AlertLifecycleReadException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public static class AlertLifecycleRequestValidation
{
    public static void Context(Guid targetId, long revision, DateTimeOffset from, DateTimeOffset to)
    { AlertLifecycleValidation.Id(targetId); AlertLifecycleValidation.Revision(revision); AlertLifecycleValidation.Window(from, to); }
    public static void Cursor(AlertLifecycleCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId, value.TargetRevision, value.FromUtc, value.ToUtc);
        Limit(value.Limit); AlertLifecycleValidation.Utc(value.CutoffUtc); AlertLifecycleValidation.Utc(value.IssuedAtUtc);
        AlertLifecycleValidation.Digest(value.ReadEpoch); AlertLifecycleValidation.Id(value.AfterEventId); AlertLifecycleValidation.Utc(value.AfterEventAtUtc);
        if (value.CutoffUtc > value.IssuedAtUtc || value.AfterEventAtUtc < value.FromUtc || value.AfterEventAtUtc >= value.ToUtc)
            throw new ArgumentException("The continuation timing is invalid.");
    }
    public static void Limit(int value)
    { if (value is < 1 or > AlertLifecycleValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Bind(AlertLifecycleListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc); Limit(request.Limit);
        if (request.Cursor is not { } c) return;
        Cursor(c);
        if (c.TargetId != request.TargetId || c.TargetRevision != request.TargetRevision || c.FromUtc != request.FromUtc || c.ToUtc != request.ToUtc || c.Limit != request.Limit)
            throw new ArgumentException("The continuation belongs to another target, revision, window or page size.");
    }
    public static void Exact(AlertLifecycleExactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc);
        AlertLifecycleValidation.Id(request.EventId); AlertLifecycleValidation.Id(request.AlertId); AlertLifecycleValidation.Utc(request.EventAtUtc);
        if (request.EpisodeId is { } episode) AlertLifecycleValidation.Id(episode);
        if (request.EventAtUtc < request.FromUtc || request.EventAtUtc >= request.ToUtc) throw new ArgumentException("The selected event is outside this window.");
    }
}
