using System.Text.Json;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record SaveIncidentRequest(Guid TargetId, long ExpectedTargetRevision, Guid OperationId,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Title, string Notes);

public sealed record SavedIncidentEvidence(Guid PacketId, DateTimeOffset OccurredAtUtc, string EvidenceKind,
    Guid? SourceRunId, string SourceSha256, string IdentitySha256, string SourceCutoffSha256,
    DateTimeOffset? SourceCutoffUtc, string VisibilityState, decimal? Confidence, JsonElement Evidence);

public sealed record SavedIncident(Guid TargetId, long TargetRevision, Guid ThreadId, Guid OperationId,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset SavedAtUtc, string Title, string Notes,
    bool EvidenceTruncated, IReadOnlyList<SavedIncidentEvidence> Evidence);

public sealed record SavedIncidentSummary(Guid TargetId, long TargetRevision, Guid ThreadId,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset SavedAtUtc, string Title,
    int EvidenceCount, bool EvidenceTruncated);

public sealed record SavedIncidentListCursor(Guid TargetId, long PublicationRevision, DateTimeOffset SnapshotUtc,
    DateTimeOffset SavedAtUtc, Guid ThreadId, int Limit);

public sealed record SavedIncidentListRequest(Guid TargetId, int Limit, SavedIncidentListCursor? Cursor = null);

public sealed record SavedIncidentListPage(Guid TargetId, long PublicationRevision, DateTimeOffset SnapshotUtc,
    IReadOnlyList<SavedIncidentSummary> Items, bool HasMore);

public interface ISavedIncidentRepositoryPort
{
    ValueTask<SavedIncident> SaveAsync(SaveIncidentRequest request, string actorSid, Guid correlationId,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<SavedIncident?> ReadAsync(Guid targetId, Guid threadId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken);
    ValueTask<SavedIncidentListPage> ListAsync(SavedIncidentListRequest request, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken);
}

public interface ISavedIncidentService
{
    ValueTask<SavedIncident> SaveAsync(AuthorizationContext authorization, SaveIncidentRequest request,
        Guid correlationId, CancellationToken cancellationToken);
    ValueTask<SavedIncident?> ReadAsync(AuthorizationContext authorization, Guid targetId, Guid threadId,
        CancellationToken cancellationToken);
    ValueTask<SavedIncidentListPage> ListAsync(AuthorizationContext authorization, SavedIncidentListRequest request,
        CancellationToken cancellationToken);
}

public sealed class SavedIncidentConflictException : Exception
{
    public SavedIncidentConflictException() : base("The incident operation or target revision changed.") { }
}
