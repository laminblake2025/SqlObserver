namespace SqlObserver.Server;
public sealed record DeadlockSummaryResponse(Guid EventId, DateTimeOffset OccurredAtUtc, string Fingerprint, int ParticipantCount, int RelationCount, bool ParseTruncated, DateTimeOffset CollectedAtUtc);
public sealed record DeadlockPageResponse(Guid TargetId, DateTimeOffset RepositoryTimeUtc, IReadOnlyList<DeadlockSummaryResponse> Items, string? NextCursor);
public sealed record DeadlockParticipantResponse(int SessionId, bool IsVictim);
public sealed record DeadlockRelationResponse(int BlockerSessionId, int WaiterSessionId, string ResourceCategory, string LockMode);
public sealed record DeadlockDetailResponse(DeadlockSummaryResponse Summary, IReadOnlyList<DeadlockParticipantResponse> Participants, IReadOnlyList<DeadlockRelationResponse> Relations);
