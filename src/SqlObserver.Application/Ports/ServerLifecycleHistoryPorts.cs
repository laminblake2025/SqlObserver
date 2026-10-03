using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record ServerLifecycleHistoryCursor(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc, string ReadEpoch,
    int Limit, DateTimeOffset AfterDetectedAtUtc, Guid AfterEventId);
public sealed record ServerLifecycleHistoryListRequest(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, int Limit, ServerLifecycleHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record ServerLifecycleHistoryExactRequest(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, Guid EventId, long SampleTargetRevision, Guid AfterRunId, DateTimeOffset DetectedAtUtc,
    RepositoryCallTimeout Timeout);
public sealed record ServerLifecycleHistoryPage(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch, DateTimeOffset RepositoryTimeUtc,
    string State, string Reason, ServerLifecycleHistoryCoverage Coverage,
    IReadOnlyList<ServerLifecycleHistoryEvent> Items, ServerLifecycleHistoryCursor? NextAnchor);
public sealed record ServerLifecycleHistoryExactPage(Guid TargetId, long TargetRevision, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch, DateTimeOffset RepositoryTimeUtc,
    string State, string Reason, ServerLifecycleHistoryEvent? Event,
    ServerLifecycleHistoryEngineProof? BeforeEngine, ServerLifecycleHistoryEngineProof? AfterEngine);
public interface IServerLifecycleHistoryReadPort
{
    ValueTask<ServerLifecycleHistoryPage?> ListAsync(ServerLifecycleHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<ServerLifecycleHistoryExactPage?> GetAsync(ServerLifecycleHistoryExactRequest request, CancellationToken cancellationToken);
}
public interface IServerLifecycleHistoryService
{
    ValueTask<ServerLifecycleHistoryPage?> ListAsync(AuthorizationContext authorization, ServerLifecycleHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<ServerLifecycleHistoryExactPage?> GetAsync(AuthorizationContext authorization, ServerLifecycleHistoryExactRequest request, CancellationToken cancellationToken);
}
public sealed class ServerLifecycleHistoryReadException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public static class ServerLifecycleHistoryRequestValidation
{
    public static void Context(Guid target, long revision, DateTimeOffset from, DateTimeOffset to)
    { ServerLifecycleHistoryValidation.Id(target); ServerLifecycleHistoryValidation.Revision(revision); ServerLifecycleHistoryValidation.Window(from, to); }
    public static void Limit(int value)
    { if (value is < 1 or > ServerLifecycleHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Cursor(ServerLifecycleHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId, value.TargetRevision, value.FromUtc, value.ToUtc);
        Limit(value.Limit); ServerLifecycleHistoryValidation.Utc(value.CutoffUtc); ServerLifecycleHistoryValidation.Utc(value.IssuedAtUtc);
        ServerLifecycleHistoryValidation.Digest(value.ReadEpoch); ServerLifecycleHistoryValidation.Id(value.AfterEventId);
        ServerLifecycleHistoryValidation.Utc(value.AfterDetectedAtUtc);
        if (value.CutoffUtc > value.IssuedAtUtc || value.AfterDetectedAtUtc < value.FromUtc ||
            value.AfterDetectedAtUtc >= value.ToUtc || value.AfterDetectedAtUtc > value.CutoffUtc)
            throw new ArgumentException("The lifecycle continuation timing is invalid.");
    }
    public static void Bind(ServerLifecycleHistoryListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout);
        Context(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc); Limit(request.Limit);
        if (request.Cursor is not { } cursor) return;
        Cursor(cursor);
        if (cursor.TargetId != request.TargetId || cursor.TargetRevision != request.TargetRevision ||
            cursor.FromUtc != request.FromUtc || cursor.ToUtc != request.ToUtc || cursor.Limit != request.Limit)
            throw new ArgumentException("The continuation belongs to another target, revision, window or page size.");
    }
    public static void Exact(ServerLifecycleHistoryExactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout);
        Context(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc);
        ServerLifecycleHistoryValidation.Id(request.EventId); ServerLifecycleHistoryValidation.Id(request.AfterRunId);
        ServerLifecycleHistoryValidation.Revision(request.SampleTargetRevision); ServerLifecycleHistoryValidation.Utc(request.DetectedAtUtc);
        if (request.SampleTargetRevision > request.TargetRevision || request.DetectedAtUtc < request.FromUtc || request.DetectedAtUtc >= request.ToUtc)
            throw new ArgumentException("The selected lifecycle event is outside its target revision or window.");
    }
}
