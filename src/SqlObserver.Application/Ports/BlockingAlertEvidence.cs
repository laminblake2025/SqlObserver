using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public enum BlockingAlertEvidenceReason
{
    Current, CollectorDisabled, SourceUnavailable, SourceStale, SourceIncomplete, SourceInconsistent,
}

/// <summary>A complete current snapshot has a value; absent evidence never means zero blocking.</summary>
public sealed record BlockingAlertEvidence(MonitoredInstanceId TargetId, long TargetRevision,
    DateTimeOffset ReadAtUtc, DateTimeOffset? ValidUntilUtc, long? ScheduleRevision, Guid? RunId,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset? SourceObservedAtUtc, int? RetainedEdges,
    long? MaximumWaitMilliseconds, BlockingAlertEvidenceReason Reason, string? CompletionDigest, string SourceDigest)
{
    public double? MaximumWaitSeconds => MaximumWaitMilliseconds / 1000d;
    public void Validate()
    {
        if (TargetId is null || TargetRevision<=0 || ReadAtUtc.Offset!=TimeSpan.Zero || !Enum.IsDefined(Reason) ||
            ScheduleRevision<=0 || RunId==Guid.Empty || RetainedEdges is < 0 or > 1025 ||
            MaximumWaitMilliseconds is < 0 or > 9007199254740991 ||
            ValidUntilUtc is { } until && (until.Offset!=TimeSpan.Zero || until<=ReadAtUtc || until>ReadAtUtc.AddMinutes(2)) ||
            CompletedAtUtc is { Offset: var completedOffset } && completedOffset!=TimeSpan.Zero ||
            SourceObservedAtUtc is { Offset: var observedOffset } && observedOffset!=TimeSpan.Zero ||
            CompletionDigest is not null && !Digest(CompletionDigest) || !Digest(SourceDigest) ||
            (Reason==BlockingAlertEvidenceReason.Current) != (MaximumWaitMilliseconds is not null) ||
            Reason!=BlockingAlertEvidenceReason.Current && ValidUntilUtc is not null ||
            Reason==BlockingAlertEvidenceReason.Current && (RunId is null || ScheduleRevision is null ||
                CompletedAtUtc is null || CompletedAtUtc>ReadAtUtc || ValidUntilUtc is null ||
                RetainedEdges is null or > 1024 || CompletionDigest is null ||
                RetainedEdges==0 && (MaximumWaitMilliseconds!=0 || SourceObservedAtUtc is not null) ||
                RetainedEdges>0 && (SourceObservedAtUtc is null || SourceObservedAtUtc>ReadAtUtc)))
            throw new InvalidDataException("Blocking alert evidence has an invalid source binding.");
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public interface IBlockingAlertEvidencePort
{
    ValueTask<BlockingAlertEvidence> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}
