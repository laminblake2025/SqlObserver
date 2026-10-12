using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using DomainEnrollment = SqlObserver.Domain.Collection.SqlAgentModuleEnrollment;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Finite actual-monitor candidates. The independent coordinator owns the SQL job,
/// execution witness and Agent process epoch; this monitor never controls either service or job.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivityProductionModule")]
[Trait("Category", "RequiresSqlAgentActivityRepositoryNative")]
[Trait("Category", "RequiresSqlAgentActivityRunningJobNative")]
public sealed class SqlAgentActivityRunningJobNativeQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public Task ActualMonitorCapturesOwnedRunningCandidateThroughRepository() => CaptureAsync("running");

    [Fact]
    public Task ActualMonitorCapturesOwnedCompletedCandidateThroughRepository() => CaptureAsync("completed");

    private Task CaptureAsync(string phase) =>
        new SqlAgentActivityNativeQualificationHarness(output).RunAsync(false, async (context, token) =>
        {
            var input = SqlAgentActivityRunningJobNativeInput.LoadEnvironment(phase);
            var repositoryInput = SqlAgentActivityRepositoryNativeQualificationInput.Load(Environment.GetEnvironmentVariable);
            input.RequireBindings(repositoryInput, context.Input.Enrollment);
            await using var dataSource = repositoryInput.CreateDataSource();
            await repositoryInput.RequireRepositoryIdentityAsync(dataSource, token);
            var resolver = new AuthoritativeResolver(new PostgreSqlSqlAgentModuleEnrollmentPort(dataSource), context.Input.Enrollment);
            var collector = new SqlAgentActivityObservationsCollector(new SqlServerAgentActivityModuleCaptureReader(resolver));
            long started = Stopwatch.GetTimestamp();
            CollectorExecutionResult result = await collector.CollectAsync(context.Request, token);
            Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
            Assert.Equal(3, resolver.Calls);
            input.RequireCapture(result, context.Input.Enrollment, context.Request.RunId);
            await repositoryInput.RequireRepositoryIdentityAsync(dataSource, token);
            // Independent witness digest only binds the coordinator's private comparison. It
            // supplies no enrollment authority and does not qualify association or elapsed time.
            context.DeferredRunningJobReceipt = SqlAgentActivityRunningJobNativeReceipt.Serialize(input,
                repositoryInput, context.Input.Enrollment, result, context.Request.RunId, resolver.Calls);
        });

    private sealed class AuthoritativeResolver(PostgreSqlSqlAgentModuleEnrollmentPort repository,
        DomainEnrollment expected) : ISqlAgentModuleEnrollmentResolver
    {
        internal int Calls { get; private set; }
        public async ValueTask<SqlAgentModuleEnrollmentSnapshot?> ResolveAsync(MonitoredInstanceId targetId,
            ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
        {
            Assert.Equal(expected.TargetId, targetId); Assert.Equal(expected.TargetRevision, targetRevision);
            SqlAgentModuleEnrollmentSnapshot? snapshot = await repository.ResolveAsync(targetId, targetRevision, cancellationToken);
            Calls++;
            Assert.NotNull(snapshot); Assert.Equal(expected.Revision, snapshot.Revision);
            Assert.True(expected.SameAuthorization(snapshot.Enrollment), "Repository authorization differs; private values withheld.");
            return snapshot;
        }
    }
}

internal sealed class SqlAgentActivityRunningJobNativeInput
{
    internal const string Prefix = "SQLOBSERVER_AGENT_NATIVE_RUNNING_";
    internal const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    internal const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly string[] Names = ["QUALIFICATION", "FIXTURE_ID", "DEPLOYMENT_ID", "PHASE", "JOB_ID",
        "AGENT_SESSION_ID", "AGENT_START_LOCAL", "AGENT_PROCESS_ID", "AGENT_STARTUP_UTC", "WITNESS_BINDING_SHA256"];
    private SqlAgentActivityRunningJobNativeInput(Guid fixtureId, Guid deploymentId, string phase, Guid jobId,
        int session, DateTime local, int process, DateTimeOffset startup, string digest)
    {
        FixtureId = fixtureId; DeploymentId = deploymentId; Phase = phase; JobId = jobId;
        AgentSessionId = session; AgentStartLocal = local; AgentProcessId = process;
        AgentStartupUtc = startup; WitnessBindingSha256 = digest;
    }
    internal Guid FixtureId { get; }
    internal Guid DeploymentId { get; }
    internal string Phase { get; }
    internal Guid JobId { get; }
    internal int AgentSessionId { get; }
    internal DateTime AgentStartLocal { get; }
    internal int AgentProcessId { get; }
    internal DateTimeOffset AgentStartupUtc { get; }
    internal string WitnessBindingSha256 { get; }
    public override string ToString() => "Private finite Agent running-job qualification input";

    internal static SqlAgentActivityRunningJobNativeInput LoadEnvironment(string expectedPhase) =>
        Load(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(
            item => (string)item.Key, item => (string?)item.Value, StringComparer.Ordinal), expectedPhase);

    internal static SqlAgentActivityRunningJobNativeInput Load(IReadOnlyDictionary<string, string?> environment, string expectedPhase)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (expectedPhase is not ("running" or "completed") ||
            !environment.TryGetValue(Prefix + "QUALIFICATION", out string? enabled) || enabled != "1") throw Invalid();
        var supplied = environment.Keys.Where(name => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!supplied.Order(StringComparer.Ordinal).SequenceEqual(Names.Select(name => Prefix + name).Order(StringComparer.Ordinal))) throw Invalid();
        string phase = Required("PHASE", 9);
        if (phase != expectedPhase) throw Invalid();
        string localText = Required("AGENT_START_LOCAL", 27), utcText = Required("AGENT_STARTUP_UTC", 28);
        if (!DateTime.TryParseExact(localText, LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local) ||
            local.Kind != DateTimeKind.Unspecified || local.Year < 1753 || local.ToString(LocalFormat, CultureInfo.InvariantCulture) != localText ||
            !DateTimeOffset.TryParseExact(utcText, UtcFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset startup) ||
            startup.Year < 1753 || startup.Offset != TimeSpan.Zero || startup.ToString(UtcFormat, CultureInfo.InvariantCulture) != utcText) throw Invalid();
        string digest = Required("WITNESS_BINDING_SHA256", 64);
        if (digest.Length != 64 || digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
        return new(Id("FIXTURE_ID"), Id("DEPLOYMENT_ID"), phase, Id("JOB_ID"), Number("AGENT_SESSION_ID"),
            local, Number("AGENT_PROCESS_ID"), startup, digest);

        string Required(string name, int maximum)
        {
            if (!environment.TryGetValue(Prefix + name, out string? value) || string.IsNullOrEmpty(value) || value.Length > maximum) throw Invalid();
            return value;
        }
        Guid Id(string name)
        {
            string value = Required(name, 36);
            if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || id.ToString("D") != value) throw Invalid();
            return id;
        }
        int Number(string name)
        {
            string value = Required(name, 10);
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0 ||
                number.ToString(CultureInfo.InvariantCulture) != value) throw Invalid();
            return number;
        }
    }

    internal void RequireBindings(SqlAgentActivityRepositoryNativeQualificationInput repository, DomainEnrollment original)
    {
        if (FixtureId != repository.FixtureId || DeploymentId != original.EnrollmentId) throw Invalid();
    }

    internal SqlAgentActivityCandidateState RequireCapture(CollectorExecutionResult result, DomainEnrollment original, CollectorRunId runId)
    {
        var value = result.Payload.SqlAgentModuleCapture;
        if (DeploymentId != original.EnrollmentId || result.TargetId != original.TargetId || result.TargetRevision != original.TargetRevision ||
            result.CollectorId.Value != "sql-agent.activity-observations" || result.CollectorManifestVersion != 1 || result.OutputSchemaVersion != 1 ||
            result.Outcome != CollectorRunOutcome.Succeeded || result.Reason != CollectorRunReason.Completed || value is null ||
            value.EnrollmentRevision != original.Revision || value.EnrollmentId != original.EnrollmentId || value.EvidenceSha256 != original.EvidenceSha256 ||
            value.NativeQualification != SqlAgentActivityNativeQualification.Absent || value.ProductionAdmitted || value.ServiceSessionAssociationQualified)
            throw InvalidCapture();
        SqlAgentActivityObservationsOutputValidation.Validate(result.Payload, original.TargetId, original.TargetRevision,
            runId, result.Outcome, result.Accounting, result.Loss);
        var capture = value.Capture;
        if (capture.Truncated || capture.Jobs.Count is < 1 or > 512 || capture.SourceRowsRead != capture.Jobs.Count ||
            capture.NativeQualification != SqlAgentActivityNativeQualification.Absent || capture.ProductionAdmitted || capture.ServiceSessionAssociationQualified ||
            !original.IsActiveAt(capture.Before.SourceObservedAtUtc) || !original.IsActiveAt(capture.After.SourceObservedAtUtc)) throw InvalidCapture();
        foreach (var header in new[] { capture.Before, capture.After })
            if (header.Service.MatchingServiceCount != 1 || header.Service.Status != 4 || header.Service.ProcessId != AgentProcessId ||
                header.Service.StartupAtUtc != AgentStartupUtc || header.Session.SessionId != AgentSessionId ||
                header.Session.AgentStartLocal != AgentStartLocal) throw InvalidCapture();
        var evaluation = SqlAgentActivityEvaluator.Evaluate(capture);
        if (evaluation.Gap != SqlAgentActivityGap.None || evaluation.NativeQualification != SqlAgentActivityNativeQualification.Absent ||
            evaluation.ProductionAdmitted || evaluation.ServiceSessionAssociationQualified || evaluation.Jobs.Any(job =>
                job.StartedAtUtc is not null || job.ElapsedSeconds is not null || job.LongRunning is not null)) throw InvalidCapture();
        var owned = evaluation.Jobs.Where(job => job.Source.JobId == JobId).ToArray();
        var expected = Phase == "running" ? SqlAgentActivityCandidateState.Running : SqlAgentActivityCandidateState.Stopped;
        if (owned.Length != 1 || owned[0].Source.SessionId != AgentSessionId || owned[0].CandidateState != expected) throw InvalidCapture();
        return expected;
    }

    private static InvalidOperationException Invalid() => new("Native running-job qualification input or binding is invalid; private values withheld.");
    private static InvalidDataException InvalidCapture() => new("Native running-job capture contract is invalid; private values withheld.");
}

internal static class SqlAgentActivityRunningJobNativeReceipt
{
    internal const string Marker = "SQL_AGENT_ACTIVITY_RUNNING_JOB_NATIVE_RECEIPT_V1:";
    internal const int MaximumReceiptBytes = 534000;
    internal static string Serialize(SqlAgentActivityRunningJobNativeInput input,
        SqlAgentActivityRepositoryNativeQualificationInput repository, DomainEnrollment original,
        CollectorExecutionResult result, CollectorRunId runId, int resolutions)
    {
        input.RequireBindings(repository, original);
        SqlAgentActivityCandidateState candidate = input.RequireCapture(result, original, runId);
        var value = JsonNode.Parse(SqlAgentActivityRepositoryNativeReceipt.Capture(repository, original, result, runId, resolutions))!.AsObject();
        value["schemaVersion"] = 1;
        value["runningFixture"] = new JsonObject
        {
            ["fixtureId"] = input.FixtureId.ToString("D"), ["deploymentId"] = input.DeploymentId.ToString("D"),
            ["phase"] = input.Phase, ["jobId"] = input.JobId.ToString("D"), ["agentSessionId"] = input.AgentSessionId,
            ["agentStartLocal"] = input.AgentStartLocal.ToString(SqlAgentActivityRunningJobNativeInput.LocalFormat, CultureInfo.InvariantCulture),
            ["agentProcessId"] = input.AgentProcessId,
            ["agentStartupUtc"] = input.AgentStartupUtc.ToString(SqlAgentActivityRunningJobNativeInput.UtcFormat, CultureInfo.InvariantCulture),
            ["witnessBindingSha256"] = input.WitnessBindingSha256,
            ["candidateState"] = candidate == SqlAgentActivityCandidateState.Running ? "Running" : "Stopped",
        };
        string receipt = value.ToJsonString();
        if (Encoding.UTF8.GetByteCount(receipt) > MaximumReceiptBytes)
            throw new InvalidDataException("Native running-job receipt bound exceeded; private values withheld.");
        return receipt;
    }
}
