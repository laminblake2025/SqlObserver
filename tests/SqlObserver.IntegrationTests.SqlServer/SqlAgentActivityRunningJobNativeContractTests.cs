using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using DomainEnrollment = SqlObserver.Domain.Collection.SqlAgentModuleEnrollment;

namespace SqlObserver.IntegrationTests.SqlServer;

[Trait("Category", "AgentNativeRunningJobContract")]
public sealed class SqlAgentActivityRunningJobNativeContractTests
{
    private static readonly Guid Fixture = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Job = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Utc = new(2026, 10, 5, 12, 0, 1, TimeSpan.Zero);
    private static readonly DateTime Local = new DateTime(2026, 10, 5, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(1234567);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")]
    public void ExplicitOptInIsRequired(string? value)
    {
        var environment = Valid("running"); environment[SqlAgentActivityRunningJobNativeInput.Prefix + "QUALIFICATION"] = value;
        RequireInputFailure(environment);
    }

    [Theory]
    [InlineData("running")] [InlineData("completed")]
    public void ExactClosedInputsPreserveIndependentFieldsWithoutClaimingWitnessAuthority(string phase)
    {
        var environment = Valid(phase); environment["UNRELATED_SETTING"] = "ignored-private-value";
        var input = Load(environment, phase);
        Assert.Equal(Fixture, input.FixtureId); Assert.Equal(Original().EnrollmentId, input.DeploymentId);
        Assert.Equal(Job, input.JobId); Assert.Equal(phase, input.Phase);
        Assert.Equal(42, input.AgentSessionId); Assert.Equal(1234, input.AgentProcessId);
        Assert.Equal(Local, input.AgentStartLocal); Assert.Equal(DateTimeKind.Unspecified, input.AgentStartLocal.Kind);
        Assert.Equal(Utc.AddSeconds(-1), input.AgentStartupUtc); Assert.Equal(TimeSpan.Zero, input.AgentStartupUtc.Offset);
        Assert.Equal(new string('a', 64), input.WitnessBindingSha256);
        Assert.Equal("Private finite Agent running-job qualification input", input.ToString());
    }

    [Theory]
    [InlineData("FIXTURE_ID", null)] [InlineData("FIXTURE_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("FIXTURE_ID", "11111111111141118111111111111111")]
    [InlineData("FIXTURE_ID", "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    [InlineData("DEPLOYMENT_ID", " private-deployment")]
    [InlineData("JOB_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("PHASE", "completed")] [InlineData("PHASE", "Running")]
    [InlineData("AGENT_SESSION_ID", "0")] [InlineData("AGENT_SESSION_ID", "-1")]
    [InlineData("AGENT_SESSION_ID", "042")] [InlineData("AGENT_SESSION_ID", " 42")]
    [InlineData("AGENT_PROCESS_ID", "0")] [InlineData("AGENT_PROCESS_ID", "2147483648")]
    [InlineData("AGENT_START_LOCAL", "2026-10-05T01:30:00.1234567Z")]
    [InlineData("AGENT_START_LOCAL", "2026-10-05T01:30:00.123")]
    [InlineData("AGENT_START_LOCAL", "1752-10-05T01:30:00.1234567")]
    [InlineData("AGENT_STARTUP_UTC", "2026-10-05T12:00:00.0000000+00:00")]
    [InlineData("AGENT_STARTUP_UTC", "2026-10-05T12:00:00.000Z")]
    [InlineData("WITNESS_BINDING_SHA256", "private-unbounded-input")]
    [InlineData("WITNESS_BINDING_SHA256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MalformedMissingOrCrossPhaseInputFailsWithoutEcho(string field, string? value)
    {
        var environment = Valid("running"); environment[SqlAgentActivityRunningJobNativeInput.Prefix + field] = value;
        RequireInputFailure(environment);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("case")] [InlineData("missing")]
    public void PrefixInventoryIsClosedAndCaseSensitive(string mutation)
    {
        var environment = Valid("running"); string name = SqlAgentActivityRunningJobNativeInput.Prefix + "JOB_ID";
        if (mutation == "unknown") environment[SqlAgentActivityRunningJobNativeInput.Prefix + "COLLECTOR_CONNECTION"] = "private-value";
        else if (mutation == "case") { environment[name.ToLowerInvariant()] = environment[name]; environment.Remove(name); }
        else environment.Remove(name);
        RequireInputFailure(environment);
    }

    [Theory]
    [InlineData("fixture")] [InlineData("deployment")]
    public void ExpectedFixtureAndOriginalAuthorizationAreBoundIndependently(string changed)
    {
        var environment = Valid("running");
        environment[SqlAgentActivityRunningJobNativeInput.Prefix + (changed == "fixture" ? "FIXTURE_ID" : "DEPLOYMENT_ID")] =
            "99999999-9999-4999-8999-999999999999";
        var input = Load(environment, "running");
        var error = Assert.Throws<InvalidOperationException>(() => input.RequireBindings(Repository(), Original()));
        Assert.Null(error.InnerException); Assert.DoesNotContain("99999999", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("running", 1)] [InlineData("completed", 1)] [InlineData("running", 512)] [InlineData("completed", 512)]
    public void ClosedReceiptPreservesFullSourceAndOriginalProvenanceWithNoAdmissionOrElapsed(string phase, int rows)
    {
        var input = Load(Valid(phase), phase); var (result, run) = Result(phase, rows);
        var original = Original(); var repository = Repository();
        string existing = SqlAgentActivityRepositoryNativeReceipt.Capture(repository, original, result, run, 3);
        string receipt = SqlAgentActivityRunningJobNativeReceipt.Serialize(input, repository, original, result, run, 3);
        using var prior = JsonDocument.Parse(existing); using var current = JsonDocument.Parse(receipt);
        var root = current.RootElement;
        Assert.Equal(2, prior.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32()); Assert.Equal(20, root.EnumerateObject().Count());
        foreach (string field in new[] { "observation", "accounting", "loss", "repository", "targetId", "targetRevision", "runId", "outcome", "reason" })
            Assert.Equal(prior.RootElement.GetProperty(field).GetRawText(), root.GetProperty(field).GetRawText());
        var binding = root.GetProperty("runningFixture"); Assert.Equal(10, binding.EnumerateObject().Count());
        Assert.Equal(Fixture.ToString("D"), binding.GetProperty("fixtureId").GetString());
        Assert.Equal(original.EnrollmentId.ToString("D"), binding.GetProperty("deploymentId").GetString());
        Assert.Equal(phase, binding.GetProperty("phase").GetString()); Assert.Equal(Job.ToString("D"), binding.GetProperty("jobId").GetString());
        Assert.Equal(phase == "running" ? "Running" : "Stopped", binding.GetProperty("candidateState").GetString());
        Assert.Equal("2026-10-05T01:30:00.1234567", binding.GetProperty("agentStartLocal").GetString());
        Assert.Equal("2026-10-05T12:00:00.0000000Z", binding.GetProperty("agentStartupUtc").GetString());
        Assert.Equal(new string('a', 64), binding.GetProperty("witnessBindingSha256").GetString());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("serviceSessionAssociationQualified").GetBoolean());
        Assert.Equal("Absent", root.GetProperty("nativeQualification").GetString());
        Assert.False(root.GetProperty("repository").GetProperty("continuousDeploymentQualified").GetBoolean());
        Assert.Equal(3, root.GetProperty("repository").GetProperty("authorizationResolutions").GetInt32());
        Assert.False(root.GetProperty("repository").GetProperty("resolverCached").GetBoolean());
        Assert.False(root.GetProperty("repository").GetProperty("finiteResolverUsed").GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(receipt), 1, SqlAgentActivityRunningJobNativeReceipt.MaximumReceiptBytes);
        foreach (string forbidden in new[] { "not-a-real-password", "native_test_login", "so_agent_native_", original.ExpectedWindowsSid,
            "ElapsedSeconds", "StartedAtUtc", "LongRunning", "witnessQualified", "connectionString" })
            Assert.DoesNotContain(forbidden, receipt, StringComparison.Ordinal);
        var evaluation = SqlAgentActivityEvaluator.Evaluate(result.Payload.SqlAgentModuleCapture!.Capture);
        Assert.All(evaluation.Jobs, job => { Assert.Null(job.ElapsedSeconds); Assert.Null(job.StartedAtUtc); Assert.Null(job.LongRunning); });
        if (phase == "completed")
        {
            var job = root.GetProperty("observation").GetProperty("jobs")[0];
            Assert.Equal("2026-10-05T01:30:00.1234567", job.GetProperty("startExecutionLocal").GetString());
            Assert.Equal("2026-10-05T01:29:59.1234567", job.GetProperty("stopExecutionLocal").GetString());
        }
    }

    [Theory]
    [InlineData("before-process")] [InlineData("after-process")] [InlineData("before-status")] [InlineData("after-startup")]
    [InlineData("after-session")] [InlineData("after-local")]
    [InlineData("owned-job-missing")] [InlineData("phase")] [InlineData("expired")]
    [InlineData("accounting")]
    public void NativeCaptureMustMatchBothSourceHeadersOwnedCandidateAndOriginalWindow(string changed)
    {
        var input = Load(Valid("running"), "running"); var (result, run) = Result("running", 1, changed == "expired" ? null : changed);
        var original = Original();
        if (changed == "expired") original = new(original.TargetId, original.TargetRevision, original.Revision,
            original.EnrollmentId, original.ExpectedWindowsSid, original.Binding, original.NotBeforeUtc, Utc, original.EvidenceSha256);
        Assert.Throws<InvalidDataException>(() => input.RequireCapture(result, original, run));
    }

    [Theory]
    [InlineData("outcome")] [InlineData("reason")]
    public void DomainRejectsIncoherentCollectorResultsBeforeNativeCaptureValidation(string changed) =>
        Assert.Throws<ArgumentException>(() => Result("running", 1, changed));

    [Fact]
    public void ValidFailedCollectorWithoutOutputCannotQualifyRunningCapture()
    {
        var input = Load(Valid("running"), "running"); var original = Original();
        var run = new CollectorRunId(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var empty = new CollectorPayload();
        var failed = new CollectorExecutionResult(original.TargetId, original.TargetRevision,
            new("sql-agent.activity-observations"), 1, 1, CollectorRunOutcome.PermissionDenied,
            CollectorRunReason.RequiredPermissionMissing, empty, new(0, 0, 0, 0), CollectorLossEvidence.None);
        Assert.Throws<InvalidDataException>(() => input.RequireCapture(failed, original, run));
    }

    [Theory]
    [InlineData(0)] [InlineData(513)]
    public void EmptyOrTruncatedInventoryCannotProveTheOwnedJob(int rows)
    {
        var input = Load(Valid("running"), "running"); var (result, run) = Result("running", rows);
        Assert.Throws<InvalidDataException>(() => input.RequireCapture(result, Original(), run));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void ReceiptRequiresThreeAuthoritativeRepositoryAnswers(int calls)
    {
        var input = Load(Valid("running"), "running"); var (result, run) = Result("running", 1);
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityRunningJobNativeReceipt.Serialize(input, Repository(), Original(), result, run, calls));
    }

    private static void RequireInputFailure(Dictionary<string, string?> environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(environment, "running"));
        Assert.Equal("Native running-job qualification input or binding is invalid; private values withheld.", error.Message);
        Assert.Null(error.InnerException);
    }
    private static SqlAgentActivityRunningJobNativeInput Load(Dictionary<string, string?> values, string phase) =>
        SqlAgentActivityRunningJobNativeInput.Load(values, phase);
    private static Dictionary<string, string?> Valid(string phase) => new(StringComparer.Ordinal)
    {
        [SqlAgentActivityRunningJobNativeInput.Prefix + "QUALIFICATION"] = "1",
        [SqlAgentActivityRunningJobNativeInput.Prefix + "FIXTURE_ID"] = Fixture.ToString("D"),
        [SqlAgentActivityRunningJobNativeInput.Prefix + "DEPLOYMENT_ID"] = Original().EnrollmentId.ToString("D"),
        [SqlAgentActivityRunningJobNativeInput.Prefix + "PHASE"] = phase,
        [SqlAgentActivityRunningJobNativeInput.Prefix + "JOB_ID"] = Job.ToString("D"),
        [SqlAgentActivityRunningJobNativeInput.Prefix + "AGENT_SESSION_ID"] = "42",
        [SqlAgentActivityRunningJobNativeInput.Prefix + "AGENT_START_LOCAL"] = Local.ToString(SqlAgentActivityRunningJobNativeInput.LocalFormat, CultureInfo.InvariantCulture),
        [SqlAgentActivityRunningJobNativeInput.Prefix + "AGENT_PROCESS_ID"] = "1234",
        [SqlAgentActivityRunningJobNativeInput.Prefix + "AGENT_STARTUP_UTC"] = Utc.AddSeconds(-1).ToString(SqlAgentActivityRunningJobNativeInput.UtcFormat, CultureInfo.InvariantCulture),
        [SqlAgentActivityRunningJobNativeInput.Prefix + "WITNESS_BINDING_SHA256"] = new string('a', 64),
    };
    private static DomainEnrollment Original() => SqlAgentProductionModuleQualificationEnrollment.Load(
        SqlAgentActivityNativeFixture.ValidEnvironment().GetValueOrDefault, false).Enrollment;
    private static SqlAgentActivityRepositoryNativeQualificationInput Repository()
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = 5432, Database = SqlAgentActivityRepositoryNativeQualificationInput.DatabaseName(Fixture),
            Username = "native_test_login", Password = "not-a-real-password", Options = "-c role=sqlobserver_collector", Timeout = 5, CommandTimeout = 5,
        };
        var values = new Dictionary<string, string?>
        {
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "QUALIFICATION"] = "1",
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "FIXTURE_ID"] = Fixture.ToString("D"),
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "DATABASE"] = connection.Database,
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "EXPECTED_LOGIN"] = connection.Username,
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "COLLECTOR_CONNECTION"] = connection.ConnectionString,
        };
        return SqlAgentActivityRepositoryNativeQualificationInput.Load(values.GetValueOrDefault);
    }
    private static (CollectorExecutionResult Result, CollectorRunId Run) Result(string phase, int rows, string? changed = null)
    {
        var original = Original(); var run = new CollectorRunId(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var service = new SqlAgentServiceEvidence(1, changed == "before-status" ? 1 : 4,
            changed == "before-process" ? 1235 : 1234, Utc.AddSeconds(-1));
        var afterService = new SqlAgentServiceEvidence(1, 4, changed == "after-process" ? 1235 : 1234,
            changed == "after-startup" ? Utc.AddTicks(-1) : Utc.AddSeconds(-1));
        var beforeSession = new SqlAgentSessionEvidence(42, Local);
        var afterSession = new SqlAgentSessionEvidence(changed == "after-session" ? 43 : 42, changed == "after-local" ? Local.AddTicks(1) : Local);
        DateTimeOffset observed = Utc;
        var jobs = Enumerable.Range(1, Math.Min(rows, 512)).Select(index => new SqlAgentActivityJob(
            changed == "owned-job-missing" ? Guid.Parse("99999999-9999-4999-8999-999999999999") :
                Guid.Parse("00000000-0000-0000-0000-" + index.ToString("000000000000", CultureInfo.InvariantCulture)),
            42, Local, null, Local, phase == "completed" || changed == "phase" ? Local.AddSeconds(-1) : null, null, null)).ToArray();
        var capture = new SqlAgentActivityCapture(original.TargetId, original.TargetRevision, run,
            new(observed, service, beforeSession), jobs, new(observed.AddTicks(1), afterService, afterSession), rows, rows > 512);
        var payload = new CollectorPayload(sqlAgentModuleCapture: new SqlAgentModuleCaptureResult(capture, original));
        var result = new CollectorExecutionResult(original.TargetId, original.TargetRevision, new("sql-agent.activity-observations"), 1, 1,
            changed == "outcome" ? CollectorRunOutcome.PermissionDenied : rows > 512 ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
            changed == "reason" ? CollectorRunReason.RequiredPermissionMissing : rows > 512 ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed,
            payload, new(rows, 1, 4096 + rows * 768 + (changed == "accounting" ? 1 : 0), payload.EstimatedSizeBytes),
            rows > 512 ? new(CollectorLossKind.SourceRowLimit, 1, false) : CollectorLossEvidence.None);
        return (result, run);
    }
}
