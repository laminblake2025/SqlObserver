using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpBackupAssessmentTests
{
    private const string Tool = "get_backup_assessments_v2";
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid()), Other = new(Guid.NewGuid());
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SignedPagesKeepCutoffRevisionRunAndAdministrativeContextWithNoGaps()
    {
        using var f = new Fixture(); var args = Args();
        JsonElement first = Success(await f.Call(args));
        JsonElement item = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal("withinThreshold", item.GetProperty("full").GetProperty("status").GetString());
        Assert.Equal("notRequired", item.GetProperty("log").GetProperty("status").GetString());
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), item.GetProperty("samples")[0].GetProperty("sizeBytes").GetString());
        string token = first.GetProperty("nextCursor").GetString()!;
        BackupEvidenceCursor cursor = f.Signer.Decode<BackupEvidenceCursor>(token, Tool, args, Options);
        Assert.Equal(Target, cursor.TargetId); Assert.Equal(Run, cursor.RunId); Assert.Equal(2, cursor.PolicyHistoryCount); Assert.Equal(1, cursor.QualificationCommandRevision);
        args["cursor"] = JsonSerializer.SerializeToElement(token);
        JsonElement last = Success(await f.Call(args));
        Assert.Equal(2, Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("databaseOrdinal").GetInt32());
        Assert.Equal(f.Repository.Requests[0].AtUtc, f.Repository.Requests[1].AtUtc);
        Assert.False(last.GetProperty("hasMore").GetBoolean()); Assert.False(last.TryGetProperty("nextCursor", out _));
        Assert.Equal(2, f.Audit.Records.Count);
    }

    [Theory]
    [InlineData(BackupEvidenceStatus.NotCollected)] [InlineData(BackupEvidenceStatus.NoData)]
    [InlineData(BackupEvidenceStatus.WithheldByLimit)] [InlineData(BackupEvidenceStatus.CollectionFailed)]
    [InlineData(BackupEvidenceStatus.EvidenceNotRetained)]
    public async Task UnavailableEvidenceKeepsRequiredNullsAndNeverInventsDatabaseVerdicts(BackupEvidenceStatus status)
    {
        using var f = new Fixture(); f.Repository.Status = status;
        JsonElement data = Success(await f.Call(Args()));
        Assert.Empty(data.GetProperty("items").EnumerateArray()); Assert.False(data.GetProperty("hasMore").GetBoolean());
        Assert.False(data.TryGetProperty("policyHistoryCount", out _)); Assert.False(data.TryGetProperty("qualificationCommandRevision", out _));
        Assert.Single(f.Audit.Records);
    }

    [Fact]
    public async Task RevokedQualificationKeepsRecordedUtcButWithholdsAgeAndPreservesObservedDamage()
    {
        using var f = new Fixture(); f.Repository.Trusted = false; f.Repository.Damaged = true;
        JsonElement item = Success(await f.Call(Args())).GetProperty("items")[0];
        Assert.Equal("timestampQualificationUnavailable", item.GetProperty("full").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("full").GetProperty("observedAgeSeconds").ValueKind);
        Assert.Equal(JsonValueKind.String, item.GetProperty("samples")[0].GetProperty("recordedFinishUtc").ValueKind);
        Assert.False(item.GetProperty("samples")[0].GetProperty("qualificationTrusted").GetBoolean());
        Assert.Equal("backupDamaged", Assert.Single(item.GetProperty("observedIssues").EnumerateArray()).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task NoSignerReturnsFirstPageAndMoreFlagWithoutUnsignedContinuation()
    {
        using var f = new Fixture(false); JsonElement data = Success(await f.Call(Args()));
        Assert.True(data.GetProperty("hasMore").GetBoolean()); Assert.False(data.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public async Task ProjectionDropsUnreviewedFieldsAtEveryBackupObjectBoundary()
    {
        using var f = new Fixture();
        JsonObject wire = JsonNode.Parse(Success(await f.Call(Args())).GetRawText())!.AsObject();
        var item = wire["items"]![0]!.AsObject();
        JsonObject[] objects = [wire, item, item["policy"]!.AsObject(), item["full"]!.AsObject(),
            item["full"]!["selectedBackup"]!.AsObject(), item["samples"]![0]!.AsObject()];
        foreach (JsonObject obj in objects) obj["providerError"] = "private source error";
        wire["policyHistoryCount"] = 17; wire["qualificationCommandRevision"] = 19;
        item["physicalPath"] = "private path";
        wire.Remove("nextCursor");
        JsonElement data = Success(McpResults.JsonWire(wire, Options, Tool, Args(), f.Signer));
        string json = data.GetRawText();
        Assert.DoesNotContain("providerError", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.False(data.TryGetProperty("policyHistoryCount", out _));
        Assert.False(data.TryGetProperty("qualificationCommandRevision", out _));
    }

    [Fact]
    public async Task MixedGrantsDenyTheAuditorTargetBeforeRepositoryReads()
    {
        using var f = new Fixture(); var args = Args(); args["instanceId"] = JsonSerializer.SerializeToElement(Other.Value);
        Error(await f.Call(args), "forbidden"); Assert.Empty(f.Repository.Requests); Assert.Single(f.Audit.Records);
    }

    [Theory]
    [InlineData("atUtc", "2026-09-30T00:00:00.0000001Z")]
    [InlineData("atUtc", "2026-09-30T00:00:00+00:00")]
    [InlineData("atUtc", "9999-01-01T00:00:00Z")]
    [InlineData("fromUtc", "2026-09-30T00:00:00Z")]
    public async Task InvalidCutoffsAndUnsupportedWindowArgumentsFailBeforeReading(string field, string value)
    {
        using var f = new Fixture(); var args = Args(); args[field] = JsonSerializer.SerializeToElement(value);
        Error(await f.Call(args), "invalid_request"); Assert.Empty(f.Repository.Requests); Assert.Single(f.Audit.Records);
    }

    [Fact]
    public async Task ChangedArgumentsAndSupersededContextRequireRestart()
    {
        using var f = new Fixture(); var args = Args(); JsonElement data = Success(await f.Call(args)); args["cursor"] = data.GetProperty("nextCursor").Clone();
        var changed = new Dictionary<string, JsonElement>(args) { ["limit"] = JsonSerializer.SerializeToElement(2) };
        Error(await f.Call(changed), "invalid_request"); Assert.Single(f.Repository.Requests);
        f.Repository.Changed = true; Error(await f.Call(args), "cursor_stale"); Assert.Equal(3, f.Audit.Records.Count);
    }

    [Fact]
    public async Task EveryDatabaseCanBePagedWithTheAdvertisedDefaultAndNoDuplicateOrdinals()
    {
        using var f = new Fixture(); f.Repository.Count = 512; var args = Args(); args.Remove("limit"); var seen = new HashSet<int>();
        do
        {
            JsonElement data = Success(await f.Call(args));
            Assert.InRange(data.GetProperty("items").GetArrayLength(), 1, 20);
            foreach (JsonElement item in data.GetProperty("items").EnumerateArray()) Assert.True(seen.Add(item.GetProperty("databaseOrdinal").GetInt32()));
            if (!data.GetProperty("hasMore").GetBoolean()) break;
            args["cursor"] = data.GetProperty("nextCursor").Clone();
        } while (true);
        Assert.Equal(Enumerable.Range(1, 512), seen.Order()); Assert.Equal(26, f.Audit.Records.Count);
    }

    [Fact]
    public async Task MaximumPageWithAllSampleSlotsAndFaultsFitsTheCompleteWireBudget()
    {
        using var f = new Fixture(); f.Repository.Count = 21; f.Repository.AllSamples = true; f.Repository.Damaged = true;
        var args = Args(); args.Remove("limit");
        CallToolResult result = await f.Call(args); JsonElement data = Success(result);
        Assert.Equal(20, data.GetProperty("items").GetArrayLength());
        foreach (JsonElement item in data.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(5, item.GetProperty("samples").GetArrayLength());
            Assert.Equal(10, item.GetProperty("observedIssues").GetArrayLength());
        }
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.InRange(McpResults.DisclosedResponseBytes(result) + 1024, 1, McpQueryBounds.MaximumResponseBytes);
        Assert.Single(f.Audit.Records);
    }

    [Fact]
    public async Task OfficialSdkPagesBackupAssessmentsThroughHttpAndAuditsEachCallExactlyOnce()
    {
        var repository = new Repository(); var audit = new AuditPort();
        await using var app = McpProtocolHttpContractTests.BuildHost(services =>
        {
            services.AddSingleton<IBackupEvidencePort>(repository); services.AddSingleton<BackupAssessmentQueryService>();
            services.AddSingleton<IMcpInvocationAuditPort>(audit);
        });
        using HttpClient http = app.GetTestClient(); http.DefaultRequestHeaders.Add("X-Test-Auth", "allow");
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(5)
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport);
        var args = Args(); JsonElement first = Success(await client.CallToolAsync(new CallToolRequestParams { Name = Tool, Arguments = args }));
        args["cursor"] = first.GetProperty("nextCursor").Clone();
        JsonElement last = Success(await client.CallToolAsync(new CallToolRequestParams { Name = Tool, Arguments = args }));
        Assert.Equal(2, last.GetProperty("items")[0].GetProperty("databaseOrdinal").GetInt32());
        Assert.False(last.GetProperty("hasMore").GetBoolean());
        Assert.Equal(repository.Requests[0].AtUtc, repository.Requests[1].AtUtc);
        Assert.Equal(2, audit.Records.Count); Assert.All(audit.Records, r => Assert.Equal(McpInvocationOutcome.Succeeded, r.Outcome));
    }

    private static Dictionary<string, JsonElement> Args() => new() { ["instanceId"] = JsonSerializer.SerializeToElement(Target.Value), ["limit"] = JsonSerializer.SerializeToElement(1) };
    private static JsonElement Success(CallToolResult result)
    { Assert.False(result.IsError, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text))); JsonSchemaAssertions.AssertValid(result.StructuredContent!.Value, McpCatalog.OutputSchema(Tool), Tool); return result.StructuredContent.Value.GetProperty("data"); }
    private static void Error(CallToolResult result, string code)
    { Assert.True(result.IsError); Assert.Null(result.StructuredContent); using var json = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text); Assert.Equal(code, json.RootElement.GetProperty("code").GetString()); }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-8301"), new Claim(ClaimTypes.GroupSid, "S-1-5-21-8302"), new Claim(ClaimTypes.GroupSid, "S-1-5-21-8303")], "Windows"));
        internal Repository Repository { get; } = new(); internal AuditPort Audit { get; } = new(); internal McpCursorSigner Signer { get; } = new(new byte[32]);
        internal Fixture(bool signer = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-8302"), [ApplicationRole.Viewer], false, [Target]), new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-8303"), [ApplicationRole.Auditor], false, [Other])]));
            services.AddSingleton<IBackupEvidencePort>(Repository); services.AddSingleton<BackupAssessmentQueryService>(); services.AddSingleton<IMcpInvocationAuditPort>(Audit);
            if (signer) services.AddSingleton(Signer); provider = services.BuildServiceProvider();
        }
        internal ValueTask<CallToolResult> Call(IDictionary<string, JsonElement> args) => new McpCallHandler(provider).ExecuteAsync(Tool, args, user, CancellationToken.None);
        public void Dispose() => provider.Dispose();
    }
    private sealed class Repository : IBackupEvidencePort
    {
        internal List<BackupEvidenceRequest> Requests { get; } = []; internal int Count { get; set; } = 2;
        internal BackupEvidenceStatus Status { get; set; } = BackupEvidenceStatus.Retained;
        internal bool Changed { get; set; } internal bool Trusted { get; set; } = true; internal bool Damaged { get; set; }
        internal bool AllSamples { get; set; }
        public ValueTask<BackupEvidencePage> ReadAsync(BackupEvidenceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request); if (Changed && request.Cursor is not null) throw new BackupEvidenceCursorStaleException();
            DateTimeOffset at = request.AtUtc; bool retained = Status == BackupEvidenceStatus.Retained, absent = Status == BackupEvidenceStatus.NotCollected, expired = Status == BackupEvidenceStatus.EvidenceNotRetained;
            string zone = AllSamples ? new string('\\', 128) : "UTC";
            int count = retained || expired ? Count : 0, output = retained || expired ? Count * (AllSamples ? 6 : 2) : 0;
            BackupFreshnessCoverage coverage = Status switch { BackupEvidenceStatus.NotCollected => BackupFreshnessCoverage.NotCollected, BackupEvidenceStatus.NoData => BackupFreshnessCoverage.Partial, BackupEvidenceStatus.WithheldByLimit => BackupFreshnessCoverage.Truncated, BackupEvidenceStatus.CollectionFailed => BackupFreshnessCoverage.PermissionDenied, BackupEvidenceStatus.EvidenceNotRetained => BackupFreshnessCoverage.Failed, _ => BackupFreshnessCoverage.Complete };
            var header = new BackupEvidenceHeader(Target, at, 1, absent ? null : Run, retained || expired ? at : null, absent ? null : at, retained || expired ? at : null, zone, at.DateTime.AddDays(-35), coverage, Status, Status == BackupEvidenceStatus.WithheldByLimit ? 3073 : output, output, retained ? output : 0, count, true, Count, 1);
            int after = request.Cursor?.DatabaseOrdinal ?? 0, take = retained ? Math.Min(request.Limit, Count - after) : 0;
            BackupDatabaseEvidence Database(int ordinal)
            {
                string fingerprint = ordinal.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), fork = new('f', 64); DateTimeOffset finish = at.AddMinutes(-1);
                var context = new BackupV2DatabaseContext(fingerprint, fork, fork, AllSamples ? fork : null, AllSamples ? fork : null, AllSamples ? fork : null,
                    true, AllSamples ? BackupRecoveryModel.Full : BackupRecoveryModel.Simple, "ONLINE",
                    AllSamples ? BackupFreshnessScope.AvailabilityGroupLocalOnly : BackupFreshnessScope.Standalone,
                    AllSamples ? "SECONDARY" : null, AllSamples ? true : null, at, zone, at.DateTime.AddDays(-35), false);
                BackupV2SampleRead Sample(BackupKind kind, bool copyOnly) => new(new(kind, copyOnly, finish, finish.DateTime, !AllSamples, Damaged, true),
                    finish.DateTime.AddMinutes(-1), 0, long.MaxValue, fork, fork, new(BackupFinishTimeState.Resolved, finish, 1));
                BackupV2SampleRead[] samples = AllSamples ? Enum.GetValues<BackupKind>().SelectMany(kind => kind == BackupKind.Differential
                    ? new[] { Sample(kind, false) } : new[] { Sample(kind, false), Sample(kind, true) }).ToArray() : [Sample(BackupKind.Full, false)];
                var read = new BackupV2DatabaseRead(context, samples, new(Target, new(1), new(Run)), BackupFreshnessCoverage.Complete);
                return new(ordinal, read, new(Target, new(1), fingerprint, 1, true, TimeSpan.FromDays(1), AllSamples ? TimeSpan.FromHours(1) : null,
                    TimeSpan.FromMinutes(5), FullBackupRequirement.FullImage, true), samples.Select(_ => Trusted).ToArray());
            }
            return ValueTask.FromResult(new BackupEvidencePage(header, Enumerable.Range(after + 1, take).Select(Database).ToArray(), retained && after + take < Count ? new(Target, at, 1, Run, after + take, Count, 1) : null));
        }
    }
    private sealed class AuditPort : IMcpInvocationAuditPort
    {
        internal List<McpInvocationAuditRecord> Records { get; } = [];
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        { Records.Add(request.Record); return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch)); }
    }
}
