using System.Text.Json.Serialization;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Application.Ports;

public sealed record LiveActivityTarget(Guid Id, long Revision, SqlServerConnectionPolicy Connection);
public sealed record LiveActivityRow
{
    public required string Identity { get; init; }
    public string? EngineStartup { get; init; }
    public string? SessionLogin { get; init; }
    public string? RequestStart { get; init; }
    public int SessionId { get; init; }
    public int? RequestId { get; init; }
    public int? DatabaseId { get; init; }
    public string? DatabaseName { get; init; }
    public string? Login { get; init; }
    public string? ClientHost { get; init; }
    public string? Application { get; init; }
    public required string Status { get; init; }
    public string? Command { get; init; }
    public bool IsUser { get; init; }
    public string? WaitType { get; init; }
    public int? Blocker { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long CpuMs { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long MemoryBytes { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long Reads { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long Writes { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long LogicalReads { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long ElapsedMs { get; init; }
    public Guid? QueryId { get; init; }
    public string QueryState { get; init; } = "not_active";
    public LiveActivityDelta? Delta { get; init; }
}
public sealed record LiveActivityDelta(double Seconds, string CpuMs, string Reads, string Writes, string LogicalReads);
public sealed record LiveActivityPayload(Guid Id, ProtectedSensitivePayload Protected);
public sealed record LiveActivityCapture(Guid Id, DateTimeOffset ObservedUtc, bool Truncated,
    IReadOnlyList<LiveActivityRow> Rows, IReadOnlyList<LiveActivityPayload> Payloads,
    IReadOnlyList<LiveActivityDatabase>? Databases = null, Guid? DeadlockEventId = null,
    DateTimeOffset? DeadlockOccurredUtc = null);
public sealed record LiveActivitySnapshot(Guid Id, DateTimeOffset ObservedUtc, bool Truncated,
    Guid? DeadlockEventId = null, DateTimeOffset? DeadlockOccurredUtc = null);
public sealed record LiveActivityDatabase(int Id, string Name);
public sealed record LiveActivityFilter(int? DatabaseId = null, string? Login = null, string? Application = null,
    string? Status = null, bool BlockedOnly = false, bool IncludeIdle = false, bool IncludeSystem = false,
    string Sort = "cpu", bool Descending = true);
public sealed record LiveActivityRead(Guid TargetId, Guid? SnapshotId, LiveActivityFilter Filter, string? Cursor = null);
public sealed record LiveActivityPage(Guid? SnapshotId, DateTimeOffset? ObservedUtc, DateTimeOffset RepositoryTimeUtc,
    string State, bool Truncated, IReadOnlyList<LiveActivityRow> Rows, IReadOnlyList<LiveActivityDatabase> Databases, string? NextCursor);
public sealed record LiveActivityQueryDetail(string State, string? Text);

public interface ILiveActivityProtector
{
    bool IsAvailable { get; }
    ProtectedSensitivePayload Protect(Guid targetId, ReadOnlySpan<byte> text);
    byte[] Unprotect(Guid targetId, ProtectedSensitivePayload payload);
}
public interface ILiveActivityCollector
{
    Task<LiveActivityCapture> CollectAsync(LiveActivityTarget target, CancellationToken cancellationToken);
}
public interface ILiveActivityRepository
{
    Task<IReadOnlyList<LiveActivityTarget>> TargetsAsync(CancellationToken cancellationToken);
    Task CommitAsync(LiveActivityTarget target, WorkerLeaseIdentity lease, LiveActivityCapture capture, CancellationToken cancellationToken);
    Task<bool> HasDeadlockSnapshotAsync(Guid targetId, Guid eventId, CancellationToken cancellationToken);
    Task FailedAsync(Guid targetId, WorkerLeaseIdentity lease, CancellationToken cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken);
    Task<LiveActivityPage> ReadAsync(LiveActivityRead request, CancellationToken cancellationToken);
    Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(Guid targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<ProtectedSensitivePayload?> QueryAsync(Guid targetId, Guid snapshotId, string identity, CancellationToken cancellationToken);
    Task AuditAsync(Guid targetId, Guid snapshotId, string actor, string outcome, CancellationToken cancellationToken);
}
public interface ILiveActivityQueryService
{
    Task<LiveActivityPage> ReadAsync(AuthorizationContext authorization, LiveActivityRead request, CancellationToken cancellationToken);
    Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(AuthorizationContext authorization, Guid targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<LiveActivityQueryDetail> QueryAsync(AuthorizationContext authorization, Guid targetId, Guid snapshotId, string identity, CancellationToken cancellationToken);
}
