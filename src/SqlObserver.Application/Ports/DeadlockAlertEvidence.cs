using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public enum DeadlockAlertEvidenceReason
{
    Current, CollectorDisabled, SourceUnavailable, SourceStale, SourceIncomplete, SourceInconsistent,
}

/// <summary>Unique events in the five minutes ending at a complete committed scan.</summary>
public sealed record DeadlockAlertEvidence(
    MonitoredInstanceId TargetId, long TargetRevision, DateTimeOffset ReadAtUtc,
    DateTimeOffset? ValidUntilUtc, long? ScheduleRevision, Guid? RunId,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset? WindowFromUtc, DateTimeOffset? WindowToUtc,
    int? RetainedEvents, int? DeadlockCount, DeadlockAlertEvidenceReason Reason,
    string? CompletionDigest, string SourceDigest)
{
    public void Validate()
    {
        bool current = Reason == DeadlockAlertEvidenceReason.Current;
        if (TargetId is null || TargetRevision <= 0 || ReadAtUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(Reason) ||
            ScheduleRevision <= 0 || RunId == Guid.Empty || RetainedEvents is < 0 or > 256 ||
            DeadlockCount is < 0 or > 256 || DeadlockCount > RetainedEvents ||
            !Utc(CompletedAtUtc) || !Utc(WindowFromUtc) || !Utc(WindowToUtc) || !Utc(ValidUntilUtc) ||
            CompletionDigest is not null && !Digest(CompletionDigest) || !Digest(SourceDigest) ||
            current != (DeadlockCount is not null) ||
            !current && (ValidUntilUtc is not null || WindowFromUtc is not null || WindowToUtc is not null) ||
            current && (RunId is null || ScheduleRevision is null || RetainedEvents is null ||
                CompletionDigest is null || CompletedAtUtc is null || CompletedAtUtc > ReadAtUtc ||
                WindowToUtc != CompletedAtUtc || WindowFromUtc != CompletedAtUtc.Value.AddMinutes(-5) ||
                ValidUntilUtc is null || ValidUntilUtc <= ReadAtUtc || ValidUntilUtc > ReadAtUtc.AddMinutes(2)))
            throw new InvalidDataException("Deadlock alert evidence has an invalid source binding.");
    }

    private static bool Utc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero;
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public interface IDeadlockAlertEvidencePort
{
    ValueTask<DeadlockAlertEvidence> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}
