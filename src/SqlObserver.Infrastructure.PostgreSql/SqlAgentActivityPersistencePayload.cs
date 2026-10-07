using System.Globalization;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Closed source projection. Private enrollment authority and endpoint pins never enter receipts.</summary>
internal static class SqlAgentActivityPersistencePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static object Build(CommitCollectorRunRequest request, string outcome, string reason) => new
    {
        schemaVersion = 1,
        targetId = request.Work.TargetId.Value,
        targetRevision = request.Work.TargetRevision.Value,
        runId = request.Summary.RunId.Value,
        outcome,
        reason,
        sourceRowsRead = request.Summary.Accounting.SourceRowsRead,
        truncated = request.Payload.SqlAgentModuleCapture?.Capture.Truncated ?? false,
        observation = Observation(request.Payload.SqlAgentModuleCapture),
    };

    public static byte[] SerializeObservation(SqlAgentModuleCaptureResult value) =>
        JsonSerializer.SerializeToUtf8Bytes(Observation(value), Options);

    private static string Utc(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string? Local(DateTime? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static object? Observation(SqlAgentModuleCaptureResult? result) => result is null ? null : new
    {
        sourceSchemaVersion = SqlAgentActivityCapture.SourceSchemaVersion,
        result.EnrollmentRevision,
        enrollmentId = result.EnrollmentId.ToString("D"),
        result.EvidenceSha256,
        before = Header(result.Capture.Before),
        after = Header(result.Capture.After),
        jobs = result.Capture.Jobs.Select(static job => new
        {
            jobId = job.JobId.ToString("D"),
            job.SessionId,
            requestedLocal = Local(job.RequestedLocal),
            queuedLocal = Local(job.QueuedLocal),
            startExecutionLocal = Local(job.StartExecutionLocal),
            stopExecutionLocal = Local(job.StopExecutionLocal),
            job.LastExecutedStepId,
            lastExecutedStepLocal = Local(job.LastExecutedStepLocal),
        }).ToArray(),
        result.Capture.SourceRowsRead,
        result.Capture.Truncated,
        productionAdmitted = false,
        serviceSessionAssociationQualified = false,
        nativeQualification = "Absent",
    };

    private static object Header(SqlAgentActivityHeader value) => new
    {
        sourceObservedAtUtc = Utc(value.SourceObservedAtUtc),
        service = new
        {
            value.Service.MatchingServiceCount,
            value.Service.Status,
            value.Service.ProcessId,
            startupAtUtc = value.Service.StartupAtUtc is { } startup ? Utc(startup) : null,
        },
        session = new
        {
            value.Session.SessionId,
            agentStartLocal = Local(value.Session.AgentStartLocal),
        },
    };
}
