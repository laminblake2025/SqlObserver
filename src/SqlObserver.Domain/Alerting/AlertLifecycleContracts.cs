using SqlObserver.Domain.Auditing;

namespace SqlObserver.Domain.Alerting;

/// <summary>Immutable received event metadata. Nulls never borrow values from current alert state.</summary>
public sealed record AlertLifecycleEvent(Guid EventId, Guid AlertId, Guid? EpisodeId, Guid? RuleId,
    string Kind, string TimeBasis, DateTimeOffset EventAtUtc, DateTimeOffset? RecordedAtUtc,
    string Source, string Provenance, long? SourceTargetRevision, long? StateRevision,
    string? RuleName, string? FromState, string? ToState, double? Value, string? Reason,
    string? ActorSid, bool? DeliverySuppressed, DateTimeOffset? SnoozedUntilUtc,
    string? AssigneeSid, string? AssigneeName, string? PreviousAssigneeSid, string? PreviousAssigneeName);

public sealed record AlertLifecycleCoverage(int EventCount, int CompleteEventCount,
    int LegacyPartialEventCount, int CycleCount, DateTimeOffset? ProvenanceBoundaryUtc, int? RetentionDays);

public static class AlertLifecycleValidation
{
    public const int MaximumEvents = 10_000;
    public const int MaximumCycles = 1_000;
    public const int MaximumPageSize = 100;
    public const long MaximumRevision = 9_007_199_254_740_991;
    public const int MaximumReasonUtf8Bytes = 8192;
    public static void Id(Guid value)
    { if (value == Guid.Empty) throw new ArgumentException("A nonempty resource UUID is required.", nameof(value)); }
    public static void Revision(long value)
    { if (value is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Utc(DateTimeOffset value) => DomainValidation.RequireUtcMicrosecondAligned(value, nameof(value));
    public static void Window(DateTimeOffset from, DateTimeOffset to)
    { Utc(from); Utc(to); if (from >= to || to - from > TimeSpan.FromDays(31)) throw new ArgumentException("A nonempty UTC window of at most 31 days is required."); }
    public static void Digest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A canonical SHA-256 digest is required.", nameof(value));
    }
    public static void Coverage(AlertLifecycleCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.EventCount is < 0 or > MaximumEvents || value.CompleteEventCount < 0 || value.CompleteEventCount > value.EventCount ||
            value.LegacyPartialEventCount < 0 || value.LegacyPartialEventCount > value.EventCount ||
            value.CompleteEventCount + value.LegacyPartialEventCount != value.EventCount ||
            value.CycleCount is < 0 or > MaximumCycles || value.CycleCount > value.EventCount ||
            (value.EventCount == 0) != (value.CycleCount == 0) || value.RetentionDays is <= 0)
            throw new ArgumentException("Lifecycle coverage counts disagree.", nameof(value));
        if (value.ProvenanceBoundaryUtc is { } boundary) Utc(boundary);
    }
    public static void Event(AlertLifecycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value); Id(value.EventId); Id(value.AlertId); Utc(value.EventAtUtc);
        if (value.EpisodeId is { } episode) Id(episode);
        if (value.RuleId is { } rule) Id(rule);
        if (value.SourceTargetRevision is { } targetRevision) Revision(targetRevision);
        if (value.StateRevision is { } stateRevision) Revision(stateRevision);
        if (value.RecordedAtUtc is { } recorded) Utc(recorded);
        if (value.SnoozedUntilUtc is { } deadline) Utc(deadline);
        if (value.Kind is not ("fired" or "acknowledged" or "resolved" or "snoozed" or "unsnoozed" or "assigned" or "reassigned" or "unassigned") ||
            value.Source is not ("evaluation" or "administrative" or "snooze" or "assignment") ||
            value.Provenance is not ("complete" or "legacy_partial") ||
            value.TimeBasis != (value.Source == "evaluation" ? "observed" : "applied") || value.Value is { } number && !double.IsFinite(number))
            throw new ArgumentException("Invalid lifecycle event vocabulary.", nameof(value));
        State(value.FromState); State(value.ToState);
        Text(value.RuleName, 128); Text(value.Reason, MaximumReasonUtf8Bytes);
        Text(value.AssigneeName, 128); Text(value.PreviousAssigneeName, 128);
        Sid(value.ActorSid); Sid(value.AssigneeSid); Sid(value.PreviousAssigneeSid);
        if ((value.AssigneeSid is null) != (value.AssigneeName is null) || (value.PreviousAssigneeSid is null) != (value.PreviousAssigneeName is null))
            throw new ArgumentException("Recorded owner identities and labels must agree.", nameof(value));
        bool transition = value.Kind is "fired" or "acknowledged" or "resolved";
        if (transition != (value.Source is "evaluation" or "administrative") ||
            value.Source == "snooze" && value.Kind is not ("snoozed" or "unsnoozed") ||
            value.Source == "assignment" && value.Kind is not ("assigned" or "reassigned" or "unassigned") ||
            transition && (value.ToState != (value.Kind == "fired" ? "firing" : value.Kind == "acknowledged" ? "acknowledged" : "resolved") ||
                value.Kind == "fired" && value.FromState is "firing" or "acknowledged" ||
                value.Kind == "acknowledged" && value.FromState is not (null or "firing") ||
                value.Kind == "resolved" && value.FromState is not (null or "firing" or "acknowledged")))
            throw new ArgumentException("Lifecycle kind does not match its source transition.", nameof(value));
        if (value.Kind == "snoozed" && value.SnoozedUntilUtc is null || value.Kind != "snoozed" && value.SnoozedUntilUtc is not null ||
            value.Kind is "assigned" or "reassigned" && value.AssigneeSid is null || value.Kind == "unassigned" && value.AssigneeSid is not null ||
            value.Kind == "reassigned" && value.PreviousAssigneeSid is null)
            throw new ArgumentException("Lifecycle operation metadata contradicts its verb.", nameof(value));
        if (value.Provenance == "complete" && (value.EpisodeId is null || value.RuleId is null || value.RuleName is null || value.StateRevision is null || value.RecordedAtUtc is null))
            throw new ArgumentException("Complete provenance is missing captured metadata.", nameof(value));
    }
    private static void State(string? value)
    { if (value is not (null or "normal" or "pending" or "firing" or "acknowledged" or "resolved")) throw new ArgumentException("Invalid recorded state."); }
    private static void Text(string? value, int bound)
    { if (value is not null) DomainValidation.RequireSafeText(value, nameof(value), bound); }
    private static void Sid(string? value)
    {
        if (value is not null) _ = new ActorSecurityIdentifier(value);
    }
}
