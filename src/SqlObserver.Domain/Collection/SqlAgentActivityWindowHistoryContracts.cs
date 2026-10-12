using System.Globalization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Counts retained source completeness; does not establish running jobs or native qualification.</summary>
public sealed record SqlAgentActivityWindowHistoryCoverage(int RunCount, int RetainedReceiptCount,
    int RetainedCaptureCount, int CompleteSourceRunCount, int GapRunCount, int FailedRunCount,
    int LostRunCount, int MissingPayloadRunCount, bool HasVisibilityGap);

/// <summary>Canonical attempt summary. Full source headers and jobs are available only through exact detail.</summary>
public sealed record SqlAgentActivityWindowHistoryReceipt(Guid RunId, long SampleTargetRevision,
    DateTimeOffset CompletedAtUtc, string Outcome, string CanonicalReason, string LossKind,
    int MinimumLostItems, bool CountIsExact, bool ReceiptRetained, bool CapturePresent,
    int? SourceRowsRead, int? RetainedJobCount, bool? Truncated, string State, string Reason);

public sealed record SqlAgentActivityWindowHistoryServiceEvidence(int MatchingServiceCount, int? Status,
    int? ProcessId, string? StartupAtUtc);
public sealed record SqlAgentActivityWindowHistorySession(int? SessionId, string? AgentStartLocal);
public sealed record SqlAgentActivityWindowHistoryHeader(string SourceObservedAtUtc,
    SqlAgentActivityWindowHistoryServiceEvidence Service, SqlAgentActivityWindowHistorySession Session);
public sealed record SqlAgentActivityWindowHistoryJob(Guid JobId, int SessionId, string? RequestedLocal,
    string? QueuedLocal, string? StartExecutionLocal, string? StopExecutionLocal,
    int? LastExecutedStepId, string? LastExecutedStepLocal, string CandidateState);

/// <summary>Whitelisted retained source copy. Enrollment identity/digests and derived live/time metrics are excluded.</summary>
public sealed record SqlAgentActivityWindowHistoryCapture(int SourceSchemaVersion, long EnrollmentRevision,
    SqlAgentActivityWindowHistoryHeader Before, SqlAgentActivityWindowHistoryHeader After,
    IReadOnlyList<SqlAgentActivityWindowHistoryJob> Jobs, int SourceRowsRead, bool Truncated, string Gap)
{
    public bool ProductionAdmitted { get; init; }
    public bool ServiceSessionAssociationQualified { get; init; }
    public string NativeQualification { get; init; } = "Absent";
}

public static class SqlAgentActivityWindowHistoryValidation
{
    public const int MaximumPageSize = 100;
    public const int MaximumRuns = 10_000;
    public const long MaximumRevision = 9_007_199_254_740_991;
    public static TimeSpan CursorLifetime => TimeSpan.FromMinutes(15);
    private static readonly Guid ValidationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly HashSet<string> Outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
    private static readonly HashSet<string> Reasons = ["completed", "source_row_limit", "response_byte_limit", "deadline_exceeded", "transient_target_failure", "permanent_target_failure", "required_permission_missing", "target_unsupported", "output_validation_failed", "lease_ownership_lost", "circuit_currently_open", "capability_profile_missing", "capability_profile_stale", "capability_missing", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "visibility_incomplete", "target_revision_changed"];
    private static readonly HashSet<string> Losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "visibility_incomplete"];
    public static void Id(Guid value) => CpuCaptureWindowHistoryValidation.Id(value);
    public static void Revision(long value) => CpuCaptureWindowHistoryValidation.Revision(value);
    public static void Utc(DateTimeOffset value) => CpuCaptureWindowHistoryValidation.Utc(value);
    public static void Window(DateTimeOffset from, DateTimeOffset to) => CpuCaptureWindowHistoryValidation.Window(from, to);
    public static void Digest(string value) => CpuCaptureWindowHistoryValidation.Digest(value);
    public static bool IsFailure(string outcome) => outcome is not ("succeeded" or "partial");
    public static void Qualification(bool productionAdmitted, bool associationQualified, string nativeQualification)
    {
        if (productionAdmitted || associationQualified || nativeQualification != "Absent")
            throw new ArgumentException("Retained Agent evidence does not admit live-state qualification.");
    }
    public static void Coverage(SqlAgentActivityWindowHistoryCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RunCount is < 0 or > MaximumRuns || value.RetainedReceiptCount < 0 || value.RetainedReceiptCount > value.RunCount ||
            value.RetainedCaptureCount < 0 || value.RetainedCaptureCount > value.RetainedReceiptCount ||
            value.CompleteSourceRunCount < 0 || value.CompleteSourceRunCount > value.RetainedCaptureCount ||
            value.GapRunCount < 0 || value.CompleteSourceRunCount + value.GapRunCount != value.RunCount ||
            value.FailedRunCount < 0 || value.FailedRunCount > value.GapRunCount || value.LostRunCount < 0 || value.LostRunCount > value.GapRunCount ||
            value.MissingPayloadRunCount < 0 || value.MissingPayloadRunCount > value.GapRunCount ||
            value.GapRunCount > 0 && !value.HasVisibilityGap)
            throw new ArgumentException("Agent history coverage is inconsistent.");
    }
    public static void Receipt(SqlAgentActivityWindowHistoryReceipt value)
    {
        ArgumentNullException.ThrowIfNull(value); Id(value.RunId); Revision(value.SampleTargetRevision); Utc(value.CompletedAtUtc);
        if (!Outcomes.Contains(value.Outcome) || !Reasons.Contains(value.CanonicalReason) || !Losses.Contains(value.LossKind) ||
            value.MinimumLostItems is < 0 or > 513 || value.LossKind == "none" && (value.MinimumLostItems != 0 || !value.CountIsExact) ||
            value.Outcome == "succeeded" && (value.CanonicalReason != "completed" || value.LossKind != "none") ||
            value.Outcome == "partial" && (value.CanonicalReason != "source_row_limit" || value.LossKind != "source_row_limit" || value.MinimumLostItems != 1 || value.CountIsExact) ||
            value.CapturePresent && (!value.ReceiptRetained || IsFailure(value.Outcome)) ||
            value.CapturePresent != (value.SourceRowsRead is not null && value.RetainedJobCount is not null && value.Truncated is not null) ||
            !value.CapturePresent && (value.SourceRowsRead is not null || value.RetainedJobCount is not null || value.Truncated is not null))
            throw new ArgumentException("Agent canonical receipt contradicts retained evidence.");
        if (value.CapturePresent)
        {
            if (value.SourceRowsRead is < 0 or > 513 || value.RetainedJobCount != Math.Min(value.SourceRowsRead!.Value, 512) ||
                value.Truncated != (value.SourceRowsRead > 512) || value.Truncated != (value.Outcome == "partial") ||
                value.State != (value.Truncated == true ? "partial" : "available") ||
                value.Reason != (value.Truncated == true ? "source_truncated" : "source_complete"))
                throw new ArgumentException("Agent retained source accounting is invalid.");
        }
        else if (value.State != "unavailable" || value.Reason != (IsFailure(value.Outcome) ? "collection_failed" : "payload_missing"))
            throw new ArgumentException("Missing Agent payload was presented as retained source evidence.");
    }
    public static void Capture(SqlAgentActivityWindowHistoryCapture value)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.Jobs);
        Qualification(value.ProductionAdmitted, value.ServiceSessionAssociationQualified, value.NativeQualification);
        Revision(value.EnrollmentRevision);
        if (value.SourceSchemaVersion != 1) throw new ArgumentException("Unqualified Agent source schema.");
        var source = new SqlAgentActivityCapture(new MonitoredInstanceId(ValidationId), new ObservationTargetRevision(1), new CollectorRunId(ValidationId),
            SourceHeader(value.Before), value.Jobs.Select(SourceJob).ToArray(), SourceHeader(value.After), value.SourceRowsRead, value.Truncated);
        var evaluation = SqlAgentActivityEvaluator.Evaluate(source);
        if (value.Gap != evaluation.Gap.ToString() || value.Jobs.Where((job, index) => job.CandidateState != evaluation.Jobs[index].CandidateState.ToString()).Any())
            throw new ArgumentException("Agent candidate state exceeds its retained service/session evidence.");
    }
    public static SqlAgentActivityWindowHistoryCapture Project(SqlAgentActivityCapture source, long enrollmentRevision)
    {
        ArgumentNullException.ThrowIfNull(source); Revision(enrollmentRevision);
        var evaluation = SqlAgentActivityEvaluator.Evaluate(source);
        return new(1, enrollmentRevision, ProjectHeader(source.Before), ProjectHeader(source.After),
            Array.AsReadOnly(evaluation.Jobs.Select(job => new SqlAgentActivityWindowHistoryJob(job.Source.JobId, job.Source.SessionId,
                LocalText(job.Source.RequestedLocal), LocalText(job.Source.QueuedLocal), LocalText(job.Source.StartExecutionLocal),
                LocalText(job.Source.StopExecutionLocal), job.Source.LastExecutedStepId, LocalText(job.Source.LastExecutedStepLocal), job.CandidateState.ToString())).ToArray()),
            source.SourceRowsRead, source.Truncated, evaluation.Gap.ToString());
    }
    private static SqlAgentActivityWindowHistoryHeader ProjectHeader(SqlAgentActivityHeader header) => new(UtcText(header.SourceObservedAtUtc),
        new(header.Service.MatchingServiceCount, header.Service.Status, header.Service.ProcessId,
            header.Service.StartupAtUtc is { } startup ? UtcText(startup) : null),
        new(header.Session.SessionId, LocalText(header.Session.AgentStartLocal)));
    private static string UtcText(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string? LocalText(DateTime? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    private static SqlAgentActivityHeader SourceHeader(SqlAgentActivityWindowHistoryHeader value)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.Service); ArgumentNullException.ThrowIfNull(value.Session);
        return new(SourceUtc(value.SourceObservedAtUtc), new(value.Service.MatchingServiceCount, value.Service.Status,
            value.Service.ProcessId, value.Service.StartupAtUtc is { } utc ? SourceUtc(utc) : null),
            new(value.Session.SessionId, Local(value.Session.AgentStartLocal)));
    }
    private static SqlAgentActivityJob SourceJob(SqlAgentActivityWindowHistoryJob value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value.JobId, value.SessionId, Local(value.RequestedLocal), Local(value.QueuedLocal),
            Local(value.StartExecutionLocal), Local(value.StopExecutionLocal), value.LastExecutedStepId, Local(value.LastExecutedStepLocal));
    }
    private static DateTimeOffset SourceUtc(string value)
    {
        if (value is null || value.Length != 28 || !DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) || parsed.Year < 1753)
            throw new ArgumentException("Exact seven-digit source UTC time is required.");
        return parsed;
    }
    private static DateTime? Local(string? value)
    {
        if (value is null) return null;
        if (value.Length != 27 || !DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) || parsed.Year < 1753 || parsed.Kind != DateTimeKind.Unspecified)
            throw new ArgumentException("Exact offset-free seven-digit SQL local time is required.");
        return parsed;
    }
}
