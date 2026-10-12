extern alias ServerAssembly;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;
using Xunit.Abstractions;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Opt-in replay of unmodified, hash-bound successful native qualification evidence.
/// The observation identity and clocks are original; repository scheduling and completion are fresh.</summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
[Trait("Category", "RequiresCpuNativeReceipt")]
public sealed class CpuComponentsNativeReceiptHttpTests(PostgreSql18Fixture fixture, ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private const string Marker = "CPU_COMPONENT_NATIVE_RECEIPT_V1:";

    [Fact]
    public async Task OriginalNativeCollectorReceiptSurvivesCanonicalCommitAndScopedProductionHttp()
    {
        var evidence = LoadNativeEvidence();
        var observation = evidence.GetProperty("observation").Deserialize<CpuComponentsObservation>(Web)!;
        var accounting = evidence.GetProperty("accounting").Deserialize<CollectorRunAccounting>(Web)!;
        var loss = evidence.GetProperty("loss").Deserialize<CollectorLossEvidence>(Web)!;
        var payload = new CollectorPayload(cpuComponents: observation);
        var native = new CollectorExecutionResult(observation.TargetId, observation.TargetRevision, new("cpu.components"),
            evidence.GetProperty("collectorManifestVersion").GetInt32(), evidence.GetProperty("outputSchemaVersion").GetInt32(),
            (CollectorRunOutcome)evidence.GetProperty("outcome").GetInt32(), (CollectorRunReason)evidence.GetProperty("reason").GetInt32(),
            payload, accounting, loss);
        Assert.Equal(CollectorRunOutcome.Succeeded, native.Outcome);
        Assert.Equal(CollectorRunReason.Completed, native.Reason);
        Assert.Equal(CpuComponentAssociationState.Verified, observation.AssociationState);
        Assert.True(observation.SqlProcess.Available); Assert.True(observation.OperatingSystem.Available);
        Assert.Equal(6, accounting.SourceRowsRead);
        Assert.Equal(CpuComponentsOutputValidation.AccountedResponseBytes(observation), accounting.ResponseBytes);
        string originalObservation = JsonSerializer.Serialize(observation, Web);

        await using var database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        await SqlVolumeWindowHistoryFixture.SealAsync(database);
        await using (var version = database.DataSource.CreateCommand("SHOW server_version_num"))
            Assert.Equal("180004", await version.ExecuteScalarAsync());
        Guid target = observation.TargetId.Value;
        await SeedReplayTargetAsync(database, observation);
        string serverConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        await using var host = new NativeReceiptHttpFactory(serverConnection, target);
        host.UseKestrel(0);
        using var admin = host.Client("admin"); using var reader = host.Client("viewer"); using var foreign = host.Client("foreign");
        string route = $"/api/v1/observation-targets/{target:D}/resources/cpu";
        using (var state = await reader.GetAsync(route + $"/collection?targetRevision={observation.TargetRevision.Value}"))
        {
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            using var json = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("collectorEnabled").GetBoolean());
        }
        using (var enable = await admin.PutAsJsonAsync(route + "/collection", new
        { expectedTargetRevision = observation.TargetRevision.Value, expectedEnabled = false, enabled = true }))
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        await using var collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items,
            row => row.TargetId == observation.TargetId && row.CollectorId.Value == "cpu.components");
        Assert.Equal(observation.TargetRevision, work.TargetRevision);
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new($"collector/run/cpu.components/{target:N}"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new(work, observation.RunId, lease, Timeout), CancellationToken.None)).Status);
        // The source did not export scheduler duration. Zero records replay bookkeeping, never a fabricated native duration.
        var summary = new CollectorRunSummary(observation.RunId, observation.TargetId, observation.TargetRevision,
            work.CollectorId, native.CollectorManifestVersion, native.OutputSchemaVersion, native.Outcome, native.Reason,
            TimeSpan.Zero, 1, native.Accounting, native.Loss);
        var commit = new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
        var completed = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, completed.Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        Assert.Equal(originalObservation, JsonSerializer.Serialize(observation, Web));
        Assert.True(completed.CommittedAtUtc >= work.RepositoryTimeUtc);
        Assert.True(completed.CommittedAtUtc > observation.ObservedAtUtc);
        await using (var stored = database.DataSource.CreateCommand("SELECT payload->'observation' FROM telemetry.cpu_component_receipt WHERE run_id=@run"))
        {
            stored.Parameters.AddWithValue("run", observation.RunId.Value);
            using var json = JsonDocument.Parse(Assert.IsType<string>(await stored.ExecuteScalarAsync()));
            Assert.Equal(target, json.RootElement.GetProperty("targetId").GetGuid());
            Assert.Equal(observation.TargetRevision.Value, json.RootElement.GetProperty("targetRevision").GetInt64());
            Assert.Equal(observation.RunId.Value, json.RootElement.GetProperty("runId").GetGuid());
            Assert.Equal(observation.ObservedAtUtc, json.RootElement.GetProperty("observedAtUtc").GetDateTimeOffset());
            foreach (var (name, component) in new[] { ("sqlProcess", observation.SqlProcess), ("operatingSystem", observation.OperatingSystem) })
            {
                Assert.Equal(component.RawBefore, json.RootElement.GetProperty(name).GetProperty("rawBefore").Deserialize<PdhCpuRawCounter>(Web));
                Assert.Equal(component.RawAfter, json.RootElement.GetProperty(name).GetProperty("rawAfter").Deserialize<PdhCpuRawCounter>(Web));
            }
        }

        var from = completed.CommittedAtUtc!.Value.AddMinutes(-1); var to = from.AddMinutes(2);
        string query = $"targetRevision={observation.TargetRevision.Value}&fromUtc={Utc(from)}&toUtc={Utc(to)}";
        string list = route + "/observations?" + query;
        string exact = route + $"/observations/{observation.RunId.Value:D}?" + query + $"&sampleTargetRevision={observation.TargetRevision.Value}";
        using var listResponse = await reader.GetAsync(list); using var exactResponse = await reader.GetAsync(exact);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode); Assert.Equal(HttpStatusCode.OK, exactResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl!.NoStore); Assert.True(exactResponse.Headers.CacheControl!.NoStore);
        string listText = await listResponse.Content.ReadAsStringAsync(), exactText = await exactResponse.Content.ReadAsStringAsync();
        using var listed = JsonDocument.Parse(listText); using var selected = JsonDocument.Parse(exactText);
        var receipt = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(observation.RunId.Value, receipt.GetProperty("runId").GetGuid());
        Assert.Equal(observation.TargetRevision.Value, receipt.GetProperty("sampleTargetRevision").GetInt64());
        Assert.Equal(receipt.GetRawText(), selected.RootElement.GetProperty("receipt").GetRawText());
        var publicObservation = receipt.GetProperty("observation");
        Assert.Equal(observation.ExactPairEligible, publicObservation.GetProperty("exactPairEligible").GetBoolean());
        Assert.True(publicObservation.GetProperty("sourceAssociationVerified").GetBoolean());
        Assert.Equal(observation.AssociationReason.ToString(), publicObservation.GetProperty("associationReason").GetString());
        AssertComponent(observation.SqlProcess, publicObservation.GetProperty("sqlProcess"));
        AssertComponent(observation.OperatingSystem, publicObservation.GetProperty("operatingSystem"));
        foreach (string forbidden in new[] { "rawBefore", "rawAfter", "hostFingerprint", "sessionId", "bytes", "sourceFrames" })
        { Assert.DoesNotContain('"' + forbidden + '"', listText, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain('"' + forbidden + '"', exactText, StringComparison.OrdinalIgnoreCase); }
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync(list)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync(exact)).StatusCode);
        await using (var count = database.DataSource.CreateCommand("SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target"))
        { count.Parameters.AddWithValue("target", target); Assert.Equal(0L, await count.ExecuteScalarAsync()); }
        await RunBrowserAsync(reader.BaseAddress!, target, observation.TargetRevision.Value, observation.RunId.Value, from, to, publicObservation);
        output.WriteLine(JsonSerializer.Serialize(new { sourceObservedAtUtc = observation.ObservedAtUtc,
            replayRepositoryTimeUtc = work.RepositoryTimeUtc, replayCompletedAtUtc = completed.CommittedAtUtc,
            targetId = target, targetRevision = observation.TargetRevision.Value, runId = observation.RunId.Value,
            originalIdentityAndCountersPreserved = true, canonicalReplayStatus = "replayed", listStatus = 200, exactStatus = 200,
            publicObservation }, Web));
    }

    private static JsonElement LoadNativeEvidence()
    {
        string path = Environment.GetEnvironmentVariable("SQLOBSERVER_CPU_NATIVE_RECEIPT_TRX")
            ?? throw new InvalidOperationException("Explicit native receipt TRX is required; this acceptance test never substitutes synthetic evidence.");
        string hash = Environment.GetEnvironmentVariable("SQLOBSERVER_CPU_NATIVE_RECEIPT_SHA256")
            ?? throw new InvalidOperationException("Explicit native receipt SHA256 is required.");
        byte[] bytes = File.ReadAllBytes(path); Assert.InRange(bytes.Length, 1, 4 * 1024 * 1024);
        Assert.Equal(hash.ToLowerInvariant(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var stream = new MemoryStream(bytes, writable: false);
        var trx = XDocument.Load(stream); XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var counters = Assert.Single(trx.Descendants(ns + "Counters"));
        Assert.Equal("0", counters.Attribute("failed")?.Value);
        Assert.Equal(counters.Attribute("total")?.Value, counters.Attribute("passed")?.Value);
        Assert.Equal("0", counters.Attribute("notExecuted")?.Value);
        Assert.Equal(3, trx.Descendants(ns + "UnitTestResult").Count(row => row.Attribute("testName")?.Value.StartsWith("SqlObserver.IntegrationTests.SqlServer.CpuComponentsNativeQualificationTests.", StringComparison.Ordinal) == true && row.Attribute("outcome")?.Value == "Passed"));
        var result = Assert.Single(trx.Descendants(ns + "UnitTestResult"), row => row.Attribute("testName")?.Value ==
            "SqlObserver.IntegrationTests.SqlServer.CpuComponentsNativeQualificationTests.ActualAttestedCollectorPublishesIndependentNativeComponents");
        Assert.Equal("Passed", result.Attribute("outcome")?.Value);
        string line = Assert.Single(result.Descendants(ns + "StdOut").SelectMany(row => row.Value.Split('\n')),
            text => text.TrimStart().StartsWith(Marker, StringComparison.Ordinal));
        Assert.Equal(1, trx.Descendants(ns + "StdOut").SelectMany(row => row.Value.Split('\n')).Count(text => text.TrimStart().StartsWith(Marker, StringComparison.Ordinal)));
        using var json = JsonDocument.Parse(line.Trim()[Marker.Length..]);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        return json.RootElement.Clone();
    }

    private async Task RunBrowserAsync(Uri address, Guid target, long revision, Guid run, DateTimeOffset from, DateTimeOffset to, JsonElement expected)
    {
        string script = Environment.GetEnvironmentVariable("SQLOBSERVER_CPU_NATIVE_BROWSER_SCRIPT")
            ?? throw new InvalidOperationException("Connected native acceptance requires the browser verification script.");
        Assert.True(File.Exists(script));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("SQLOBSERVER_CPU_NATIVE_NODE") ?? "node")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath(script));
        start.Environment["SQLOBSERVER_CPU_NATIVE_API_URL"] = address.ToString();
        start.Environment["SQLOBSERVER_CPU_NATIVE_TARGET_ID"] = target.ToString("D");
        start.Environment["SQLOBSERVER_CPU_NATIVE_TARGET_REVISION"] = revision.ToString(CultureInfo.InvariantCulture);
        start.Environment["SQLOBSERVER_CPU_NATIVE_RUN_ID"] = run.ToString("D");
        start.Environment["SQLOBSERVER_CPU_NATIVE_FROM_UTC"] = from.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        start.Environment["SQLOBSERVER_CPU_NATIVE_TO_UTC"] = to.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        start.Environment["SQLOBSERVER_CPU_NATIVE_EXPECTED_JSON"] = expected.GetRawText();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start connected browser verification.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        output.WriteLine(await stdout); output.WriteLine(await stderr);
        Assert.Equal(0, process.ExitCode);
    }

    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    private static void AssertComponent(CpuComponentValue original, JsonElement value)
    {
        Assert.Equal(original.Percent, value.GetProperty("percent").GetDecimal());
        Assert.Equal(original.RawFormulaPercent, value.GetProperty("rawPercent").GetDecimal());
        Assert.Equal(original.ProviderRawPercent, value.GetProperty("providerRawPercent").GetDecimal());
        Assert.Equal(original.FromNative100ns!.Value.ToString(CultureInfo.InvariantCulture), value.GetProperty("fromNative100ns").GetString());
        Assert.Equal(original.ToNative100ns!.Value.ToString(CultureInfo.InvariantCulture), value.GetProperty("toNative100ns").GetString());
        Assert.Equal(original.ObservedFromUtc, value.GetProperty("observedFromUtc").GetDateTimeOffset());
        Assert.Equal(original.ObservedToUtc, value.GetProperty("observedToUtc").GetDateTimeOffset());
        Assert.Equal(original.Quality == CpuComponentQuality.ProviderAdjusted ? "provider_adjusted" : "available", value.GetProperty("quality").GetString());
        Assert.Equal(original.Reason.ToString(), value.GetProperty("reason").GetString());
        Assert.Equal(original.RawFormulaEligible, value.GetProperty("rawFormulaEligible").GetBoolean());
        Assert.Equal(original.RawFormulaReason.ToString(), value.GetProperty("rawFormulaReason").GetString());
        Assert.Equal(original.ProviderApiStatus, value.GetProperty("providerApiStatus").GetUInt32());
        Assert.Equal(original.ProviderCStatus, value.GetProperty("providerCStatus").GetUInt32());
        Assert.Equal(original.LogicalProcessorDivisor, value.GetProperty("normalizationLogicalProcessors").GetUInt32());
        Assert.Equal(original.Method, value.GetProperty("method").GetString());
    }

    private static async Task SeedReplayTargetAsync(RepositoryTestDatabase database, CpuComponentsObservation observation)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
             authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Isolated native receipt replay','replay.invalid',1433,interval '5 seconds',
             'windows_integrated_service_identity','mandatory_validated','active',@revision,statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target;
            """);
        command.Parameters.AddWithValue("target", observation.TargetId.Value);
        command.Parameters.AddWithValue("revision", observation.TargetRevision.Value);
        command.Parameters.AddWithValue("key", "native.receipt." + observation.TargetId.Value.ToString("N"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class NativeReceiptHttpFactory(string connection, Guid target) : WebApplicationFactory<ServerAssembly::Program>
    {
        internal HttpClient Client(string identity) { var client = CreateClient(); client.DefaultRequestHeaders.Add("X-Native-Receipt-Identity", identity); return client; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(static logging => logging.ClearProviders());
            builder.UseSetting("ConnectionStrings:SqlObserverRepository", connection);
            builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            foreach (var (index, role) in new[] { (0, "Viewer"), (1, "TargetAdministrator"), (2, "Viewer") })
            {
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:GroupSid", $"S-1-5-21-{1100 + index}");
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:AllTargets", "false");
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:TargetIds:0", (index == 2 ? Guid.NewGuid() : target).ToString("D"));
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:Roles:0", role);
            }
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.AddSingleton<IStartupFilter, NativeWebAssets>();
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = ReceiptAuthentication.SchemeName; options.DefaultChallengeScheme = ReceiptAuthentication.SchemeName; })
                    .AddScheme<AuthenticationSchemeOptions, ReceiptAuthentication>(ReceiptAuthentication.SchemeName, static _ => { });
            });
        }
    }
    private sealed class NativeWebAssets : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            string root = Environment.GetEnvironmentVariable("SQLOBSERVER_CPU_NATIVE_WEB_ROOT")
                ?? throw new InvalidOperationException("Explicit compiled production web root is required.");
            Assert.True(File.Exists(Path.Combine(root, "index.html")));
            app.UseFileServer(new FileServerOptions { FileProvider = new PhysicalFileProvider(Path.GetFullPath(root)), EnableDirectoryBrowsing = false });
            next(app);
        };
    }

    private sealed class ReceiptAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "Native.Receipt.Http";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-Native-Receipt-Identity"];
            int group = identity switch { "viewer" => 1100, "admin" => 1101, "foreign" => 1102, _ => 0 };
            if (group == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "S-1-5-21-1700"), new(ClaimTypes.GroupSid, $"S-1-5-21-{group}")], SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new(principal, SchemeName)));
        }
    }
}
