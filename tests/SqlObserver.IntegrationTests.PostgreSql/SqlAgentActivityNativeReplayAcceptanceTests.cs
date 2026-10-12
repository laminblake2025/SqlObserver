extern alias ServerAssembly;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using Xunit.Abstractions;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Finite replay of an original qualified native source receipt. No second source invocation.
/// Repository clocks are fresh. This does not qualify continuous deployment or native repository resolution.</summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentActivityNativeReplayAcceptanceTests(PostgreSql18Fixture fixture, ITestOutputHelper output)
{
    private const string Prefix = "SQLOBSERVER_AGENT_NATIVE_REPLAY_";
    private const string AcceptanceScheme = "Agent.NativeReplay.Acceptance";
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "RequiresAgentNativeReplayAcceptance")]
    public async Task OriginalNativeSourceSurvivesRestrictedCanonicalAdmissionRootHttpAndBuiltOperations()
    {
        Assert.Equal("1", Required("ACCEPTANCE"));
        var input = SqlAgentActivityNativeReplayInput.Load();
        await RunDownstreamAsync(input.Enrollment, input.RunId, input.Result, input.Proof, input.RawPrivateReceiptSha256, synthetic: false);
    }

    [Fact]
    [Trait("Category", "RequiresAgentNativeReplayRehearsal")]
    public async Task ExplicitSyntheticRehearsalExercisesNativeReplayDownstreamWithoutNativeProof()
    {
        Assert.Equal("1", Required("REHEARSAL"));
        var now = DateTimeOffset.UtcNow;
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var revision = new ObservationTargetRevision(2);
        var run = new CollectorRunId(Guid.NewGuid());
        var binding = new SqlAgentModuleBinding("sql01", 1433, "sql01", "SQL01", "SQL01", "", 42, 43,
            SqlAgentModuleBinding.ApprovedDefinitionSha256, new string('a', 64), new string('b', 40));
        var enrollment = new SqlAgentModuleEnrollment(target, revision, 1, Guid.NewGuid(), "S-1-5-21-1-2-3-1002", binding,
            now.AddMinutes(-1), now.AddMinutes(10), new string('c', 64));
        var sourceClock = new DateTimeOffset(now.AddSeconds(-2).Ticks / 10 * 10 + 7, TimeSpan.Zero);
        var local = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(1234567);
        var header = new SqlAgentActivityHeader(sourceClock, new(1, 1, 0, null), new(9, local.AddHours(-1)));
        var jobs = Enumerable.Range(1, 12).Select(index => new SqlAgentActivityJob(
            Guid.Parse(FormattableString.Invariant($"22222222-2222-4222-8222-{index:x12}")), 9,
            local, null, local, local.AddHours(-1), 2, local)).ToArray();
        var source = new SqlAgentModuleCaptureResult(new(target, revision, run, header, jobs,
            new(sourceClock.AddSeconds(1), header.Service, header.Session), jobs.Length, false), enrollment);
        var payload = new CollectorPayload(sqlAgentModuleCapture: source);
        var result = new CollectorExecutionResult(target, revision, new("sql-agent.activity-observations"), 1, 1,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, payload,
            new(jobs.Length, 1, 4096 + jobs.Length * 768, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        var proof = new SyntheticRehearsalProof(Synthetic: true, NativeQualified: false, OriginalAuthorizationPreserved: false,
            Scope: "Explicit synthetic enrollment and source; downstream rehearsal only, no native artifact proof");
        await RunDownstreamAsync(enrollment, run, result, proof, rawPrivateReceiptSha256: null, synthetic: true);
    }

    private sealed record SyntheticRehearsalProof(bool Synthetic, bool NativeQualified, bool OriginalAuthorizationPreserved, string Scope);

    private async Task RunDownstreamAsync(SqlAgentModuleEnrollment enrollment, CollectorRunId run,
        CollectorExecutionResult native, object proof, string? rawPrivateReceiptSha256, bool synthetic)
    {
        var source = Assert.IsType<SqlAgentModuleCaptureResult>(native.Payload.SqlAgentModuleCapture);
        var capture = source.Capture;
        Assert.Equal(1, enrollment.Revision); // Original finite trial authorization, never renumbered for replay.
        Assert.Equal(run, capture.RunId);
        Assert.Equal(enrollment.TargetId, capture.TargetId);
        Assert.Equal(enrollment.TargetRevision, capture.TargetRevision);
        string original = JsonSerializer.Serialize(capture, Web);
        string webRoot = Path.GetFullPath(Required("ROOT_WEB"));
        Assert.True(File.Exists(Path.Combine(webRoot, "index.html")));
        Assert.True(File.Exists(Path.GetFullPath(Required("BROWSER_SCRIPT"))));

        await using var database = await SqlAgentActivityHistoryFixture.CreateAsync(fixture, seal: true);
        await using (var version = database.DataSource.CreateCommand("SHOW server_version_num"))
            Assert.Equal("180004", await version.ExecuteScalarAsync());
        await SeedOriginalTargetAsync(database, enrollment);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        await using (var role = server.CreateCommand("SELECT current_user")) Assert.Equal("sqlobserver_server", await role.ExecuteScalarAsync());
        await using (var role = collector.CreateCommand("SELECT current_user")) Assert.Equal("sqlobserver_collector", await role.ExecuteScalarAsync());
        var administration = new PostgreSqlSqlAgentModuleEnrollmentPort(server);
        var mutation = new SetSqlAgentModuleEnrollmentRequest(new(enrollment.TargetId, enrollment.TargetRevision, 0, Guid.NewGuid(), enrollment),
            new(new("S-1-5-21-1-2-3-1001"), new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, enrollment.TargetId), Timeout);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.Applied, (await administration.SetAsync(mutation, CancellationToken.None)).Status);
        var resolved = Assert.IsType<SqlAgentModuleEnrollmentSnapshot>(await new PostgreSqlSqlAgentModuleEnrollmentPort(collector)
            .ResolveAsync(enrollment.TargetId, enrollment.TargetRevision, CancellationToken.None));
        Assert.True(enrollment.SameAuthorization(resolved.Enrollment));
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        await using var host = new NativeFactory(connection, enrollment.TargetId.Value, webRoot);
        host.UseKestrel(0);
        using var admin = host.Client("admin"); using var viewer = host.Client("viewer"); using var foreign = host.Client("foreign");
        string statePath = $"/api/v1/observation-targets/{enrollment.TargetId.Value:D}/sql-agent/activity-observations/collection";
        using var initial = JsonDocument.Parse(await ReadAsync(viewer, statePath + $"?targetRevision={enrollment.TargetRevision.Value}"));
        Assert.False(initial.RootElement.GetProperty("collectorEnabled").GetBoolean());
        var enable = new { expectedTargetRevision = enrollment.TargetRevision.Value,
            expectedScheduleRevision = initial.RootElement.GetProperty("scheduleRevision").GetInt64(), expectedEnabled = false, enabled = true };
        using (var denied = await viewer.PutAsJsonAsync(statePath, enable)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var enabled = await admin.PutAsJsonAsync(statePath, enable)) Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var work = Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items,
            item => item.TargetId == enrollment.TargetId && item.CollectorId.Value == "sql-agent.activity-observations");
        Assert.Equal(enrollment.TargetRevision, work.TargetRevision);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        var acquired = await leases.AcquireAsync(new(new($"collector/run/sql-agent.activity-observations/{enrollment.TargetId.Value:N}"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        DateTimeOffset completed;
        try
        {
            // Check immediately before begin. PostgreSQL independently enforces its unchanged clock fence.
            var now = await SqlAgentActivityPersistenceTests.NowAsync(database);
            Assert.True(capture.Before.SourceObservedAtUtc >= now.AddMinutes(-5), "Source receipt expired before fresh repository admission; never backdate bookkeeping.");
            Assert.True(capture.After.SourceObservedAtUtc <= now.AddMinutes(5));
            Assert.True(enrollment.IsActiveAt(now), "Original authorization expired; never reconstruct its window.");
            Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, lease, Timeout), CancellationToken.None)).Status);
            // Zero is replay bookkeeping, not a claim about native execution duration.
            var summary = new CollectorRunSummary(run, native.TargetId, native.TargetRevision, native.CollectorId,
                native.CollectorManifestVersion, native.OutputSchemaVersion, native.Outcome, native.Reason, TimeSpan.Zero, 1, native.Accounting, native.Loss);
            var commit = new CommitCollectorRunRequest(work, summary, native.Payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
            var committed = await runtime.CommitRunAsync(commit, CancellationToken.None);
            Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
            completed = Assert.IsType<DateTimeOffset>(committed.CommittedAtUtc);
            Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
            Assert.True(completed >= work.RepositoryTimeUtc);
        }
        finally { Assert.Equal(LeaseReleaseStatus.Released, await leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None)); }

        var revoke = new SetSqlAgentModuleEnrollmentRequest(new(enrollment.TargetId, enrollment.TargetRevision, enrollment.Revision, Guid.NewGuid(), null), mutation.Audit, Timeout);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.Applied, (await administration.SetAsync(revoke, CancellationToken.None)).Status);
        using var disabled = JsonDocument.Parse(await ReadAsync(viewer, statePath + $"?targetRevision={enrollment.TargetRevision.Value}"));
        Assert.False(disabled.RootElement.GetProperty("collectorEnabled").GetBoolean());
        Assert.DoesNotContain((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items,
            item => item.TargetId == enrollment.TargetId && item.CollectorId.Value == "sql-agent.activity-observations");
        var from = completed.AddMinutes(-1); var to = completed.AddMinutes(1);
        string path = $"/api/v1/observation-targets/{enrollment.TargetId.Value:D}/sql-agent/activity/observations";
        string query = $"targetRevision={enrollment.TargetRevision.Value}&fromUtc={Utc(from)}&toUtc={Utc(to)}";
        string listText = await ReadAsync(viewer, path + "?" + query + "&limit=100");
        string exactPath = path + $"/{run.Value:D}?" + query + $"&sampleTargetRevision={enrollment.TargetRevision.Value}";
        string exactText = await ReadAsync(viewer, exactPath);
        using var list = JsonDocument.Parse(listText); using var exact = JsonDocument.Parse(exactText);
        Assert.Equal(1, list.RootElement.GetProperty("coverage").GetProperty("runCount").GetInt32());
        Assert.Equal(run.Value, Assert.Single(list.RootElement.GetProperty("items").EnumerateArray()).GetProperty("runId").GetGuid());
        Assert.DoesNotContain("jobId", listText, StringComparison.Ordinal); Assert.DoesNotContain("enrollment", listText, StringComparison.OrdinalIgnoreCase);
        var publicCapture = exact.RootElement.GetProperty("capture"); AssertFlags(exact.RootElement); AssertFlags(publicCapture);
        Assert.Equal(enrollment.Revision, publicCapture.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal(capture.Truncated ? "SourceTruncated" : "ServiceNotRunning", publicCapture.GetProperty("gap").GetString());
        AssertHeader(capture.Before, publicCapture.GetProperty("before")); AssertHeader(capture.After, publicCapture.GetProperty("after"));
        Assert.Equal(capture.SourceRowsRead, publicCapture.GetProperty("sourceRowsRead").GetInt32());
        Assert.Equal(capture.Truncated, publicCapture.GetProperty("truncated").GetBoolean());
        Assert.Equal(capture.Jobs.Count, publicCapture.GetProperty("jobs").GetArrayLength());
        foreach (var job in capture.Jobs)
        {
            var retained = Assert.Single(publicCapture.GetProperty("jobs").EnumerateArray(), value => value.GetProperty("jobId").GetGuid() == job.JobId);
            Assert.Equal(job.SessionId, retained.GetProperty("sessionId").GetInt32());
            Assert.Equal("Unknown", retained.GetProperty("candidateState").GetString());
            Assert.Equal(job.LastExecutedStepId, retained.GetProperty("lastExecutedStepId").ValueKind == JsonValueKind.Null ? (int?)null : retained.GetProperty("lastExecutedStepId").GetInt32());
            foreach (var pair in new[] { ("requestedLocal", job.RequestedLocal), ("queuedLocal", job.QueuedLocal), ("startExecutionLocal", job.StartExecutionLocal), ("stopExecutionLocal", job.StopExecutionLocal), ("lastExecutedStepLocal", job.LastExecutedStepLocal) })
                Assert.Equal(Local(pair.Item2), retained.GetProperty(pair.Item1).GetString());
        }
        foreach (string text in new[] { listText, exactText })
            foreach (string forbidden in new[] { "enrollmentId", "evidenceSha256", "expectedWindowsSid", "definitionSha256", "publicCertificateSha256", "elapsedSeconds", "currentStep" })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        using (var denied = await foreign.GetAsync(path + "?" + query)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await foreign.GetAsync(exactPath)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(original, JsonSerializer.Serialize(capture, Web));
        await RunBrowserAsync(viewer.BaseAddress!, enrollment, run, from, to, publicCapture, synthetic);
        output.WriteLine((synthetic ? "AGENT_NATIVE_REPLAY_SYNTHETIC_REHEARSAL_V1:" : "AGENT_NATIVE_REPLAY_ACCEPTANCE_V1:") + JsonSerializer.Serialize(new {
            proof, rawPrivateReceiptSha256, synthetic, targetId = enrollment.TargetId.Value, targetRevision = enrollment.TargetRevision.Value,
            runId = run.Value, sourceBeforeUtc = capture.Before.SourceObservedAtUtc, sourceAfterUtc = capture.After.SourceObservedAtUtc,
            repositoryCompletedAtUtc = completed, originalSourcePreserved = true, originalAuthorizationPreserved = !synthetic, zeroReplayDuration = true,
            productionCollectorInvokedAgain = false, syntheticRepositoryPrerequisites = true, repositoryResolverNativeQualified = false,
            continuousDeploymentQualified = false, productionAdmitted = false, serviceSessionAssociationQualified = false, nativeQualification = "Absent",
            scope = synthetic ? "Explicit synthetic rehearsal -> restricted sealed canonical repository -> production root HTTP -> compiled Operations"
                : "Finite recorded native source -> restricted sealed canonical repository -> production root HTTP -> compiled Operations" }, Web));
    }

    private static async Task SeedOriginalTargetAsync(RepositoryTestDatabase database, SqlAgentModuleEnrollment enrollment)
    {
        await SqlAgentActivityHistoryFixture.ExecuteAsync(database, """
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,certificate_host_name,connect_timeout,
             authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Recorded native Agent source',@host,@port,@certificate,interval '5 seconds',
             'windows_integrated_service_identity','mandatory_validated','active',@revision,statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule SET enabled=(collector_id IN('capability.connection','engine.core')),
             next_due_at=clock_timestamp()+interval '1 day',
             last_outcome=CASE WHEN collector_id IN('capability.connection','engine.core') THEN 'succeeded' ELSE last_outcome END,
             last_succeeded_at=CASE WHEN collector_id IN('capability.connection','engine.core') THEN clock_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            """, ("target", enrollment.TargetId.Value), ("key", $"agent.native.replay.{enrollment.TargetId.Value:N}"),
            ("host", enrollment.Binding.EndpointHost), ("port", enrollment.Binding.TcpPort), ("certificate", enrollment.Binding.CertificateHost), ("revision", enrollment.TargetRevision.Value));
    }
    private static void AssertHeader(SqlAgentActivityHeader original, JsonElement actual)
    {
        Assert.Equal(SourceUtc(original.SourceObservedAtUtc), actual.GetProperty("sourceObservedAtUtc").GetString());
        var service = actual.GetProperty("service");
        Assert.Equal(original.Service.MatchingServiceCount, service.GetProperty("matchingServiceCount").GetInt32());
        Assert.Equal(original.Service.Status, service.GetProperty("status").ValueKind == JsonValueKind.Null ? (int?)null : service.GetProperty("status").GetInt32());
        Assert.Equal(original.Service.ProcessId, service.GetProperty("processId").ValueKind == JsonValueKind.Null ? (int?)null : service.GetProperty("processId").GetInt32());
        Assert.Equal(original.Service.StartupAtUtc is { } clock ? SourceUtc(clock) : null, service.GetProperty("startupAtUtc").GetString());
        var session = actual.GetProperty("session");
        Assert.Equal(original.Session.SessionId, session.GetProperty("sessionId").ValueKind == JsonValueKind.Null ? (int?)null : session.GetProperty("sessionId").GetInt32());
        Assert.Equal(Local(original.Session.AgentStartLocal), session.GetProperty("agentStartLocal").GetString());
    }
    private static void AssertFlags(JsonElement value)
    { Assert.False(value.GetProperty("productionAdmitted").GetBoolean()); Assert.False(value.GetProperty("serviceSessionAssociationQualified").GetBoolean()); Assert.Equal("Absent", value.GetProperty("nativeQualification").GetString()); }
    private static string? Local(DateTime? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    private static string SourceUtc(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    private static string Required(string name) => Environment.GetEnvironmentVariable(Prefix + name) ?? throw new InvalidOperationException("Explicit native replay input is required: " + name);
    private static async Task<string> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore); return await response.Content.ReadAsStringAsync(); }
    private async Task RunBrowserAsync(Uri origin, SqlAgentModuleEnrollment enrollment, CollectorRunId run, DateTimeOffset from, DateTimeOffset to, JsonElement capture, bool synthetic)
    {
        string expectedPath = Path.Combine(Path.GetTempPath(), "agent-native-replay-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(expectedPath, capture.GetRawText());
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable(Prefix + "NODE") ?? "node")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.GetFullPath(Required("BROWSER_SCRIPT")));
            foreach (var pair in new Dictionary<string, string> { ["MODE"] = synthetic ? "synthetic-rehearsal" : "native", ["API_URL"] = origin.ToString(), ["TARGET_ID"] = enrollment.TargetId.Value.ToString("D"),
                ["TARGET_REVISION"] = enrollment.TargetRevision.Value.ToString(CultureInfo.InvariantCulture), ["RUN_ID"] = run.Value.ToString("D"),
                ["FROM_UTC"] = from.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
                ["TO_UTC"] = to.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), ["EXPECTED_CAPTURE_PATH"] = expectedPath })
                start.Environment[Prefix + pair.Key] = pair.Value;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Native replay browser could not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await process.WaitForExitAsync(termination.Token);
                }
                throw;
            }
            output.WriteLine(await stdout); output.WriteLine(await stderr); Assert.Equal(0, process.ExitCode);
        }
        finally { File.Delete(expectedPath); }
    }
    private sealed class NativeFactory(string connection, Guid target, string webRoot) : WebApplicationFactory<ServerAssembly::Program>
    {
        internal HttpClient Client(string identity) { var client = CreateClient(); client.DefaultRequestHeaders.Add("X-Agent-Native-Replay-Identity", identity); return client; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); builder.UseSetting("SqlObserver:DevelopmentAuthentication:Enabled", "false");
            builder.UseSetting("ConnectionStrings:SqlObserverRepository", connection);
            builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("SqlObserver:WebRootPath", webRoot); builder.ConfigureLogging(static logging => logging.ClearProviders());
            foreach (var (index, role) in new[] { (0, "Viewer"), (1, "TargetAdministrator"), (2, "Viewer") })
            {
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:GroupSid", $"S-1-5-21-{2200 + index}");
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:AllTargets", "false");
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:TargetIds:0", (index == 2 ? Guid.NewGuid() : target).ToString("D"));
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:Roles:0", role);
            }
            builder.ConfigureTestServices(services => services.AddAuthentication(options =>
            { options.DefaultAuthenticateScheme = AcceptanceScheme; options.DefaultChallengeScheme = AcceptanceScheme; })
                .AddScheme<AuthenticationSchemeOptions, NativeAuthentication>(AcceptanceScheme, static _ => { }));
        }
    }
    private sealed class NativeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            int group = Request.Headers["X-Agent-Native-Replay-Identity"].ToString() switch { "viewer" => 2200, "admin" => 2201, "foreign" => 2202, _ => 0 };
            if (group == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.PrimarySid, "S-1-5-21-1800"), new(ClaimTypes.GroupSid, $"S-1-5-21-{group}")], AcceptanceScheme));
            return Task.FromResult(AuthenticateResult.Success(new(principal, AcceptanceScheme)));
        }
    }
}
