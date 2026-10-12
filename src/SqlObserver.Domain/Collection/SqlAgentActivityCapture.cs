using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public enum SqlAgentActivityNativeQualification { Absent = 0 }
public enum SqlAgentActivityCandidateState { Unknown = 0, Running = 1, Queued = 2, Stopped = 3 }
public enum SqlAgentActivityGap
{
    None = 0, SourceTruncated = 1, ServiceMissing = 2, ServiceAmbiguous = 3,
    ServiceNotRunning = 4, ServiceEpochUnavailable = 5, ServiceEpochChanged = 6,
    SessionUnavailable = 7, SessionChanged = 8, ServiceStateUnavailable = 9, ServiceClockBoundary = 10,
}

/// <summary>Identified Agent service matches only; names, accounts and executable paths are not retained.</summary>
public sealed record SqlAgentServiceEvidence
{
    public SqlAgentServiceEvidence(int matchingServiceCount, int? status, int? processId, DateTimeOffset? startupAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(matchingServiceCount);
        if (status is < 1 or > 7 || processId is < 0 || startupAtUtc is { Year: < 1753 })
            throw new ArgumentException("Agent service source evidence is invalid.");
        MatchingServiceCount = matchingServiceCount; Status = status; ProcessId = processId;
        // The native datetimeoffset supplies this offset. This conversion is not applied to job-local dates.
        StartupAtUtc = startupAtUtc?.ToUniversalTime();
        if (StartupAtUtc is { Year: < 1753 }) throw new ArgumentException("Agent service UTC startup is outside the source date range.");
    }
    public int MatchingServiceCount { get; }
    public int? Status { get; }
    public int? ProcessId { get; }
    public DateTimeOffset? StartupAtUtc { get; }
}

/// <summary>The latest syssessions row, never MAX(sysjobactivity.session_id).</summary>
public sealed record SqlAgentSessionEvidence
{
    public SqlAgentSessionEvidence(int? sessionId, DateTime? agentStartLocal)
    {
        if (sessionId is <= 0) throw new ArgumentException("Agent session identity is invalid.", nameof(sessionId));
        SqlAgentActivitySourceValidation.Local(agentStartLocal);
        SessionId = sessionId; AgentStartLocal = agentStartLocal;
    }
    public int? SessionId { get; }
    public DateTime? AgentStartLocal { get; }
}

public sealed record SqlAgentActivityHeader
{
    public SqlAgentActivityHeader(DateTimeOffset sourceObservedAtUtc, SqlAgentServiceEvidence service, SqlAgentSessionEvidence session)
    {
        if (sourceObservedAtUtc.Offset != TimeSpan.Zero || sourceObservedAtUtc.Year < 1753)
            throw new ArgumentException("Agent capture requires actual UTC source time.", nameof(sourceObservedAtUtc));
        SourceObservedAtUtc = sourceObservedAtUtc;
        Service = service ?? throw new ArgumentNullException(nameof(service));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }
    public DateTimeOffset SourceObservedAtUtc { get; }
    public SqlAgentServiceEvidence Service { get; }
    public SqlAgentSessionEvidence Session { get; }
}

/// <summary>Offset-free activity dates and last-executed-step evidence, not current step or physical elapsed time.</summary>
public sealed record SqlAgentActivityJob
{
    public SqlAgentActivityJob(Guid jobId, int sessionId, DateTime? requestedLocal, DateTime? queuedLocal,
        DateTime? startExecutionLocal, DateTime? stopExecutionLocal, int? lastExecutedStepId, DateTime? lastExecutedStepLocal)
    {
        if (jobId == Guid.Empty || sessionId <= 0 || lastExecutedStepId is < 0)
            throw new ArgumentException("Agent activity identity is invalid.");
        SqlAgentActivitySourceValidation.Local(requestedLocal); SqlAgentActivitySourceValidation.Local(queuedLocal);
        SqlAgentActivitySourceValidation.Local(startExecutionLocal); SqlAgentActivitySourceValidation.Local(stopExecutionLocal);
        SqlAgentActivitySourceValidation.Local(lastExecutedStepLocal);
        // No ordering assertion is made between local dates: DST/clock changes can reverse them.
        JobId = jobId; SessionId = sessionId; RequestedLocal = requestedLocal; QueuedLocal = queuedLocal;
        StartExecutionLocal = startExecutionLocal; StopExecutionLocal = stopExecutionLocal;
        LastExecutedStepId = lastExecutedStepId; LastExecutedStepLocal = lastExecutedStepLocal;
    }
    public Guid JobId { get; }
    public int SessionId { get; }
    public DateTime? RequestedLocal { get; }
    public DateTime? QueuedLocal { get; }
    public DateTime? StartExecutionLocal { get; }
    public DateTime? StopExecutionLocal { get; }
    public int? LastExecutedStepId { get; }
    public DateTime? LastExecutedStepLocal { get; }
}

internal static class SqlAgentActivitySourceValidation
{
    internal static void Local(DateTime? value)
    {
        if (value is { } local && (local.Kind != DateTimeKind.Unspecified || local.Year < 1753))
            throw new ArgumentException("Agent source local time must be an offset-free SQL datetime value.");
    }
}

/// <summary>Staged bounded source bracket. Completeness never admits a production running-job metric.</summary>
public sealed class SqlAgentActivityCapture
{
    public const int SourceSchemaVersion = 1;
    public const int MaximumJobs = 512;
    public const int MaximumSourceRows = MaximumJobs + 1;
    public static readonly TimeSpan MaximumBracket = TimeSpan.FromSeconds(10);

    public SqlAgentActivityCapture(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, CollectorRunId runId,
        SqlAgentActivityHeader before, IReadOnlyList<SqlAgentActivityJob> jobs, SqlAgentActivityHeader after, int sourceRowsRead, bool truncated)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        Before = before ?? throw new ArgumentNullException(nameof(before)); After = after ?? throw new ArgumentNullException(nameof(after));
        ArgumentNullException.ThrowIfNull(jobs);
        int jobCount = jobs.Count;
        if (jobCount is < 0 or > MaximumJobs) throw new ArgumentException("Agent job retention exceeds its source bound.", nameof(jobs));
        SqlAgentActivityJob[] copiedJobs = jobs.ToArray();
        if (after.SourceObservedAtUtc < before.SourceObservedAtUtc || after.SourceObservedAtUtc - before.SourceObservedAtUtc > MaximumBracket)
            throw new ArgumentException("Agent source capture clock order or original bracket bound is invalid.");
        if (sourceRowsRead is < 0 or > MaximumSourceRows || copiedJobs.Length != Math.Min(sourceRowsRead, MaximumJobs) ||
            truncated != (sourceRowsRead > MaximumJobs)) throw new ArgumentException("Agent source accounting is invalid.");
        string? previous = null;
        foreach (SqlAgentActivityJob job in copiedJobs)
        {
            if (job is null || job.SessionId != before.Session.SessionId)
                throw new ArgumentException("Agent activity row is missing or not bound to the captured latest session.");
            string key = job.JobId.ToString("D");
            if (previous is not null && string.CompareOrdinal(previous, key) >= 0)
                throw new ArgumentException("Agent job rows must be unique and ordered by canonical UUID text.");
            previous = key;
        }
        Jobs = Array.AsReadOnly(copiedJobs); SourceRowsRead = sourceRowsRead; Truncated = truncated;
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public SqlAgentActivityHeader Before { get; }
    public SqlAgentActivityHeader After { get; }
    public IReadOnlyList<SqlAgentActivityJob> Jobs { get; }
    public int SourceRowsRead { get; }
    public bool Truncated { get; }
    public SqlAgentActivityNativeQualification NativeQualification { get; } = SqlAgentActivityNativeQualification.Absent;
    public bool ProductionAdmitted { get; }
    public bool ServiceSessionAssociationQualified { get; }
}

/// <summary>Candidate source-column state only. Service/session association and actual live state remain unqualified.
/// Stopped requires both local start and stop; it does not imply an issued stop command.</summary>
public sealed record SqlAgentActivityJobEvaluation(SqlAgentActivityJob Source, SqlAgentActivityCandidateState CandidateState)
{
    public DateTimeOffset? StartedAtUtc { get; }
    public long? ElapsedSeconds { get; }
    public bool? LongRunning { get; }
}

public sealed class SqlAgentActivityEvaluation
{
    internal SqlAgentActivityEvaluation(SqlAgentActivityGap gap, IEnumerable<SqlAgentActivityJobEvaluation> jobs)
    { Gap = gap; Jobs = Array.AsReadOnly(jobs.ToArray()); }
    public SqlAgentActivityGap Gap { get; }
    public IReadOnlyList<SqlAgentActivityJobEvaluation> Jobs { get; }
    public SqlAgentActivityNativeQualification NativeQualification { get; } = SqlAgentActivityNativeQualification.Absent;
    public bool ProductionAdmitted { get; }
    public bool ServiceSessionAssociationQualified { get; }
}

public static class SqlAgentActivityEvaluator
{
    public static SqlAgentActivityEvaluation Evaluate(SqlAgentActivityCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        SqlAgentActivityGap gap = ContextGap(capture);
        return new(gap, capture.Jobs.Select(job => new SqlAgentActivityJobEvaluation(job,
            gap != SqlAgentActivityGap.None ? SqlAgentActivityCandidateState.Unknown :
            job.StartExecutionLocal is not null && job.StopExecutionLocal is null ? SqlAgentActivityCandidateState.Running :
            job.StartExecutionLocal is null && job.StopExecutionLocal is null && (job.RequestedLocal is not null || job.QueuedLocal is not null)
                ? SqlAgentActivityCandidateState.Queued : job.StartExecutionLocal is not null && job.StopExecutionLocal is not null
                    ? SqlAgentActivityCandidateState.Stopped : SqlAgentActivityCandidateState.Unknown)));
    }
    private static SqlAgentActivityGap ContextGap(SqlAgentActivityCapture capture)
    {
        var before = capture.Before; var after = capture.After;
        if (capture.Truncated) return SqlAgentActivityGap.SourceTruncated;
        if (before.Service.MatchingServiceCount == 0 || after.Service.MatchingServiceCount == 0) return SqlAgentActivityGap.ServiceMissing;
        if (before.Service.MatchingServiceCount != 1 || after.Service.MatchingServiceCount != 1) return SqlAgentActivityGap.ServiceAmbiguous;
        if (before.Service.Status is null || after.Service.Status is null) return SqlAgentActivityGap.ServiceStateUnavailable;
        if (before.Service.Status != 4 || after.Service.Status != 4) return SqlAgentActivityGap.ServiceNotRunning;
        if (before.Service.ProcessId is null or 0 || after.Service.ProcessId is null or 0 || before.Service.StartupAtUtc is null || after.Service.StartupAtUtc is null)
            return SqlAgentActivityGap.ServiceEpochUnavailable;
        if (before.Service.ProcessId != after.Service.ProcessId || before.Service.StartupAtUtc != after.Service.StartupAtUtc)
            return SqlAgentActivityGap.ServiceEpochChanged;
        if (before.Service.StartupAtUtc > before.SourceObservedAtUtc || after.Service.StartupAtUtc > after.SourceObservedAtUtc)
            return SqlAgentActivityGap.ServiceClockBoundary;
        if (before.Session.SessionId is null || after.Session.SessionId is null || before.Session.AgentStartLocal is null || after.Session.AgentStartLocal is null)
            return SqlAgentActivityGap.SessionUnavailable;
        if (before.Session.SessionId != after.Session.SessionId || before.Session.AgentStartLocal != after.Session.AgentStartLocal)
            return SqlAgentActivityGap.SessionChanged;
        return SqlAgentActivityGap.None;
    }
}
