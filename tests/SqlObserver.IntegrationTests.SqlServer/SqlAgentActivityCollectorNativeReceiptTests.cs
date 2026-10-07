using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Private finite native capture export for a separately qualified repository replay.
/// This is not a public API DTO, enrollment command, repository receipt or admission token.</summary>
internal static class SqlAgentActivityCollectorNativeReceipt
{
    internal const string Marker = "SQL_AGENT_ACTIVITY_COLLECTOR_PRIVATE_RECEIPT_V1:";
    internal const int MaximumReceiptBytes = 530000;

    internal static string Serialize(CollectorExecutionResult result, CollectorRunId runId)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CollectorId.Value != "sql-agent.activity-observations" || result.CollectorManifestVersion != 1 ||
            result.OutputSchemaVersion != 1 || result.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial) ||
            result.Reason != (result.Outcome == CollectorRunOutcome.Partial ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed))
            throw new InvalidDataException("Native collector receipt contract is invalid; private values withheld.");
        SqlAgentActivityObservationsOutputValidation.Validate(result.Payload, result.TargetId, result.TargetRevision,
            runId, result.Outcome, result.Accounting, result.Loss);
        SqlAgentModuleCaptureResult value = result.Payload.SqlAgentModuleCapture!;
        SqlAgentActivityCapture capture = value.Capture;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, diagnosticOnly = true, actualMonitorCollector = true,
            repositoryResolverQualified = false, productionAdmitted = false,
            serviceSessionAssociationQualified = false, nativeQualification = "Absent",
            targetId = result.TargetId.Value.ToString("D"), targetRevision = result.TargetRevision.Value,
            runId = runId.Value.ToString("D"), collectorId = result.CollectorId.Value,
            collectorManifestVersion = result.CollectorManifestVersion, outputSchemaVersion = result.OutputSchemaVersion,
            outcome = result.Outcome == CollectorRunOutcome.Partial ? "partial" : "succeeded",
            reason = result.Outcome == CollectorRunOutcome.Partial ? "source_row_limit" : "completed",
            accounting = new
            {
                sourceRowsRead = result.Accounting.SourceRowsRead, outputItemsProduced = result.Accounting.OutputItemsProduced,
                responseBytes = result.Accounting.ResponseBytes, outputBytes = result.Accounting.OutputBytes,
            },
            loss = new
            {
                kind = result.Loss.Kind == CollectorLossKind.None ? "none" : "source_row_limit",
                minimumLostItems = result.Loss.MinimumLostItems, countIsExact = result.Loss.CountIsExact,
                minimumLostBytes = result.Loss.MinimumLostBytes,
            },
            observation = new
            {
                sourceSchemaVersion = 1, enrollmentRevision = value.EnrollmentRevision,
                enrollmentId = value.EnrollmentId.ToString("D"), evidenceSha256 = value.EvidenceSha256,
                before = Header(capture.Before), after = Header(capture.After),
                jobs = capture.Jobs.Select(job => new
                {
                    jobId = job.JobId.ToString("D"), sessionId = job.SessionId,
                    requestedLocal = Local(job.RequestedLocal), queuedLocal = Local(job.QueuedLocal),
                    startExecutionLocal = Local(job.StartExecutionLocal), stopExecutionLocal = Local(job.StopExecutionLocal),
                    lastExecutedStepId = job.LastExecutedStepId, lastExecutedStepLocal = Local(job.LastExecutedStepLocal),
                }).ToArray(),
                sourceRowsRead = capture.SourceRowsRead, truncated = capture.Truncated,
                productionAdmitted = false, serviceSessionAssociationQualified = false, nativeQualification = "Absent",
            },
        });
        if (bytes.Length > MaximumReceiptBytes)
            throw new InvalidDataException("Native collector receipt bound exceeded; private values withheld.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static object Header(SqlAgentActivityHeader header) => new
    {
        sourceObservedAtUtc = Utc(header.SourceObservedAtUtc),
        service = new
        {
            matchingServiceCount = header.Service.MatchingServiceCount, status = header.Service.Status,
            processId = header.Service.ProcessId, startupAtUtc = header.Service.StartupAtUtc is { } startup ? Utc(startup) : null,
        },
        session = new { sessionId = header.Session.SessionId, agentStartLocal = Local(header.Session.AgentStartLocal) },
    };

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string? Local(DateTime? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
}

public sealed class SqlAgentActivityCollectorNativeReceiptTests
{
    [Theory]
    [InlineData(0)] [InlineData(512)] [InlineData(513)]
    public void ClosedPrivateReceiptPreservesFullSourceAndLogicalAccountingWithinBound(int sourceRows)
    {
        var (result, runId) = Result(sourceRows);
        string receipt = SqlAgentActivityCollectorNativeReceipt.Serialize(result, runId);
        Assert.InRange(Encoding.UTF8.GetByteCount(receipt), 1, SqlAgentActivityCollectorNativeReceipt.MaximumReceiptBytes);
        using var parsed = JsonDocument.Parse(receipt);
        JsonElement root = parsed.RootElement;
        Assert.Equal(18, root.EnumerateObject().Count());
        Assert.True(root.GetProperty("diagnosticOnly").GetBoolean()); Assert.True(root.GetProperty("actualMonitorCollector").GetBoolean());
        Assert.False(root.GetProperty("repositoryResolverQualified").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("serviceSessionAssociationQualified").GetBoolean());
        Assert.Equal("Absent", root.GetProperty("nativeQualification").GetString());
        Assert.Equal(sourceRows > 512 ? "partial" : "succeeded", root.GetProperty("outcome").GetString());
        Assert.Equal(4096 + sourceRows * 768, root.GetProperty("accounting").GetProperty("responseBytes").GetInt64());
        Assert.Equal(4096 + Math.Min(sourceRows, 512) * 768, root.GetProperty("accounting").GetProperty("outputBytes").GetInt64());
        var observation = root.GetProperty("observation");
        Assert.Equal(12, observation.EnumerateObject().Count());
        Assert.Equal(Math.Min(sourceRows, 512), observation.GetProperty("jobs").GetArrayLength());
        Assert.Equal(sourceRows > 512, observation.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, observation.GetProperty("sourceSchemaVersion").GetInt32());
        Assert.Equal(1, observation.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal("22222222-2222-4222-8222-222222222222", observation.GetProperty("enrollmentId").GetString());
        Assert.Equal(new string('c', 64), observation.GetProperty("evidenceSha256").GetString());
        Assert.Equal("2026-10-05T12:00:00.0000000Z", observation.GetProperty("before").GetProperty("sourceObservedAtUtc").GetString());
        Assert.Equal("2026-10-05T12:00:00.0000001Z", observation.GetProperty("after").GetProperty("sourceObservedAtUtc").GetString());
        Assert.Equal(3, observation.GetProperty("before").EnumerateObject().Count());
        Assert.Equal(4, observation.GetProperty("before").GetProperty("service").EnumerateObject().Count());
        Assert.Equal(2, observation.GetProperty("before").GetProperty("session").EnumerateObject().Count());
        Assert.DoesNotContain("expected-server", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("publicCertificate", receipt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionPolicy", receipt, StringComparison.OrdinalIgnoreCase);
        foreach (var job in observation.GetProperty("jobs").EnumerateArray())
        {
            Assert.Equal(8, job.EnumerateObject().Count());
            Assert.Equal("2026-10-05T01:30:00.1234567", job.GetProperty("startExecutionLocal").GetString());
            Assert.Equal("2026-10-05T01:29:59.1234567", job.GetProperty("stopExecutionLocal").GetString());
            Assert.False(job.TryGetProperty("elapsedSeconds", out _)); Assert.False(job.TryGetProperty("currentStep", out _));
            Assert.False(job.TryGetProperty("startedAtUtc", out _));
        }
    }

    [Fact]
    public void ReceiptRefusesRunSubstitutionInsteadOfRebindingNativeCapture()
    {
        var (result, _) = Result(1);
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityCollectorNativeReceipt.Serialize(result, new(Guid.NewGuid())));
    }

    [Fact]
    public void ReceiptRejectsFailureAndForeignCollectorEvenWithOtherwiseValidPayload()
    {
        var (valid, runId) = Result(0);
        var foreign = new CollectorExecutionResult(valid.TargetId, valid.TargetRevision, new("engine.core"), 1, 1,
            valid.Outcome, valid.Reason, valid.Payload, valid.Accounting, valid.Loss);
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityCollectorNativeReceipt.Serialize(foreign, runId));
        var failed = new CollectorExecutionResult(valid.TargetId, valid.TargetRevision, valid.CollectorId, 1, 1,
            CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing,
            CollectorPayload.Empty, new(0, 0, 0, 0), CollectorLossEvidence.None);
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityCollectorNativeReceipt.Serialize(failed, runId));
    }

    private static (CollectorExecutionResult Result, CollectorRunId RunId) Result(int sourceRows)
    {
        var input = SqlAgentProductionModuleQualificationEnrollment.Load(SqlAgentActivityNativeFixture.ValidEnvironment().GetValueOrDefault, false);
        var runId = new CollectorRunId(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var utc = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var local = new DateTime(2026, 10, 5, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(1234567);
        var service = new SqlAgentServiceEvidence(1, 1, 0, null);
        var session = new SqlAgentSessionEvidence(1, local);
        var before = new SqlAgentActivityHeader(utc, service, session);
        var after = new SqlAgentActivityHeader(utc.AddTicks(1), service, session);
        var jobs = Enumerable.Range(1, Math.Min(sourceRows, 512)).Select(index =>
            new SqlAgentActivityJob(Guid.Parse("00000000-0000-0000-0000-" + index.ToString("000000000000", CultureInfo.InvariantCulture)),
                1, local, null, local, local.AddSeconds(-1), 2, local)).ToArray();
        var capture = new SqlAgentActivityCapture(input.Enrollment.TargetId, input.Enrollment.TargetRevision, runId,
            before, jobs, after, sourceRows, sourceRows > 512);
        var value = new SqlAgentModuleCaptureResult(capture, input.Enrollment);
        var payload = new CollectorPayload(sqlAgentModuleCapture: value);
        var result = new CollectorExecutionResult(input.Enrollment.TargetId, input.Enrollment.TargetRevision, new("sql-agent.activity-observations"), 1, 1,
            sourceRows > 512 ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
            sourceRows > 512 ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed,
            payload, new(sourceRows, 1, 4096 + sourceRows * 768, payload.EstimatedSizeBytes),
            sourceRows > 512 ? new(CollectorLossKind.SourceRowLimit, 1, false) : CollectorLossEvidence.None);
        return (result, runId);
    }
}
