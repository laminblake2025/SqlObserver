using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpIncidentListTests
{
    private const string Tool = "list_incidents";
    private const string Actor = "S-1-5-21-2101";
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly MonitoredInstanceId Other = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly Guid First = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Second = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SignedPagesFreezeDefaultWindowAndSnapshotAndExposeEvidenceThreadIds()
    {
        using var fixture = new Fixture();
        Dictionary<string, JsonElement> args = Arguments();
        CallToolResult first = await fixture.Call(args);
        AssertSuccess(first);
        JsonElement data = first.StructuredContent!.Value.GetProperty("data");
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        JsonElement item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal(First, item.GetProperty("threadId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("latestGenerationObservedAtUtc").ValueKind);
        Assert.Equal(0, item.GetProperty("generationCount").GetInt64());
        string token = data.GetProperty("nextCursor").GetString()!;
        Assert.InRange(token.Length, 1, McpCursorSigner.MaximumTokenLength);
        IncidentListCursor cursor = fixture.Signer.Decode<IncidentListCursor>(token, Tool, args, Options);
        Assert.Equal(7, cursor.PublicationRevision);
        args["cursor"] = JsonSerializer.SerializeToElement(token);
        CallToolResult second = await fixture.Call(args);
        AssertSuccess(second);
        JsonElement terminal = second.StructuredContent!.Value.GetProperty("data");
        Assert.False(terminal.GetProperty("hasMore").GetBoolean());
        Assert.False(terminal.TryGetProperty("nextCursor", out _));
        Assert.Equal(Second, Assert.Single(terminal.GetProperty("items").EnumerateArray()).GetProperty("threadId").GetGuid());
        Assert.Equal(fixture.Repository.Queries[0].FromUtc, fixture.Repository.Queries[1].FromUtc);
        Assert.Equal(fixture.Repository.Queries[0].ToUtc, fixture.Repository.Queries[1].ToUtc);
        Assert.Equal(cursor.SnapshotUtc, fixture.Repository.Queries[1].SnapshotUtc);
        Assert.Equal(cursor.TargetRevision, fixture.Repository.Queries[1].TargetRevision);
        Assert.Equal(2, fixture.Audit.Records.Count);
        Assert.All(fixture.Audit.Records, audit => { Assert.Equal(Target, audit.TargetId); Assert.Null(audit.IncidentId); Assert.Equal(McpInvocationOutcome.Succeeded, audit.Outcome); });
    }

    [Fact]
    public async Task WithoutSignerFirstPageKeepsHasMoreAndOmitsUnavailableCursor()
    {
        using var fixture = new Fixture(signer: false);
        CallToolResult result = await fixture.Call(Arguments());
        AssertSuccess(result);
        JsonElement data = result.StructuredContent!.Value.GetProperty("data");
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.False(data.TryGetProperty("nextCursor", out _));
        Assert.Single(fixture.Audit.Records);
    }

    [Theory]
    [InlineData(ApplicationRole.Viewer)]
    [InlineData(ApplicationRole.Operator)]
    [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task MixedGrantsCannotReadTheAuditorOnlyTarget(ApplicationRole role)
    {
        using var fixture = new Fixture(role: role);
        Dictionary<string, JsonElement> args = Arguments();
        args["instanceId"] = JsonSerializer.SerializeToElement(Other.Value);
        AssertError(await fixture.Call(args), "forbidden");
        Assert.Empty(fixture.Repository.Queries);
        Assert.Equal(McpInvocationOutcome.Denied, Assert.Single(fixture.Audit.Records).Outcome);
        fixture.Audit.Records.Clear();
        AssertSuccess(await fixture.Call(Arguments()));
        Assert.Single(fixture.Repository.Queries);
    }

    [Fact]
    public async Task ChangedIncidentSetReturnsAnActionableStaleCursorErrorAndOneAudit()
    {
        using var fixture = new Fixture();
        Dictionary<string, JsonElement> args = Arguments();
        CallToolResult first = await fixture.Call(args);
        AssertSuccess(first);
        args["cursor"] = first.StructuredContent!.Value.GetProperty("data").GetProperty("nextCursor").Clone();
        fixture.Repository.Changed = true;
        fixture.Audit.Records.Clear();
        CallToolResult result = await fixture.Call(args);
        AssertError(result, "cursor_stale");
        Assert.Contains("Restart list_incidents without a cursor", Text(result), StringComparison.Ordinal);
        Assert.Equal(McpInvocationOutcome.Invalid, Assert.Single(fixture.Audit.Records).Outcome);
    }

    [Fact]
    public async Task ChangedArgumentsCannotReuseSignedCursor()
    {
        using var fixture = new Fixture();
        Dictionary<string, JsonElement> args = Arguments();
        CallToolResult first = await fixture.Call(args);
        AssertSuccess(first);
        args["cursor"] = first.StructuredContent!.Value.GetProperty("data").GetProperty("nextCursor").Clone();
        args["limit"] = JsonSerializer.SerializeToElement(2);
        AssertError(await fixture.Call(args), "invalid_request");
        Assert.Single(fixture.Repository.Queries);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("window")]
    [InlineData("threadId")]
    public async Task InvalidBoundsOrUnsupportedFieldsDoNotReachRepository(string invalid)
    {
        using var fixture = new Fixture();
        Dictionary<string, JsonElement> args = Arguments();
        if (invalid == "limit") args["limit"] = JsonSerializer.SerializeToElement(101);
        if (invalid == "threadId") args["threadId"] = JsonSerializer.SerializeToElement(First);
        if (invalid == "window")
        {
            args["fromUtc"] = JsonSerializer.SerializeToElement("2026-01-01T00:00:00Z");
            args["toUtc"] = JsonSerializer.SerializeToElement("2026-02-02T00:00:00Z");
        }
        AssertError(await fixture.Call(args), "invalid_request");
        Assert.Empty(fixture.Repository.Queries);
        Assert.Equal(McpInvocationOutcome.Invalid, Assert.Single(fixture.Audit.Records).Outcome);
    }

    [Fact]
    public async Task MissingAuditWithholdsIncidentIdentities()
    {
        using var fixture = new Fixture(audit: false);
        CallToolResult result = await fixture.Call(Arguments());
        AssertError(result, "audit_unavailable");
        Assert.DoesNotContain(First.ToString("D"), Text(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other-target")]
    [InlineData("duplicate")]
    [InlineData("future-generation")]
    [InlineData("count-mismatch")]
    [InlineData("wrong-cursor")]
    public async Task InvalidRepositoryMetadataIsWithheld(string defect)
    {
        using var fixture = new Fixture();
        fixture.Repository.Defect = defect;
        Dictionary<string, JsonElement> args = Arguments();
        if (defect == "duplicate") args["limit"] = JsonSerializer.SerializeToElement(2);
        AssertError(await fixture.Call(args), "request_failed");
        Assert.Equal(McpInvocationOutcome.RepositoryFailure, Assert.Single(fixture.Audit.Records).Outcome);
    }

    [Theory]
    [InlineData(1_000_001L, true)]
    [InlineData(-1L, false)]
    public async Task GenerationCountUsesTheNonnegativeLongContract(long count, bool valid)
    {
        using var fixture = new Fixture();
        fixture.Repository.GenerationCount = count;
        CallToolResult result = await fixture.Call(Arguments());
        if (valid)
        {
            AssertSuccess(result);
            Assert.Equal(count, Assert.Single(result.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("generationCount").GetInt64());
        }
        else AssertError(result, "request_failed");
    }

    private static Dictionary<string, JsonElement> Arguments() => new(StringComparer.Ordinal)
    {
        ["instanceId"] = JsonSerializer.SerializeToElement(Target.Value),
        ["limit"] = JsonSerializer.SerializeToElement(1)
    };
    private static void AssertSuccess(CallToolResult result)
    {
        Assert.False(result.IsError, Text(result));
        JsonSchemaAssertions.AssertValid(result.StructuredContent!.Value, McpCatalog.OutputSchema(Tool), Tool);
    }
    private static void AssertError(CallToolResult result, string code)
    {
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        using JsonDocument json = JsonDocument.Parse(Text(result));
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
    private static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly ClaimsPrincipal user = new(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, Actor), new Claim(ClaimTypes.GroupSid, "S-1-5-21-2102"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-2103")], "Windows"));
        internal Repository Repository { get; } = new();
        internal AuditPort Audit { get; } = new();
        internal McpCursorSigner Signer { get; } = new(new byte[32]);
        internal Fixture(bool signer = true, bool audit = true, ApplicationRole role = ApplicationRole.Viewer)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new WindowsGroupRoleResolver([
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-2102"), [role], false, [Target]),
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-2103"), [ApplicationRole.Auditor], false, [Other])]));
            services.AddSingleton<IIncidentListProjectionRepositoryPort>(Repository);
            services.AddSingleton<IIncidentListQueryService, IncidentListQueryService>();
            if (signer) services.AddSingleton(Signer);
            if (audit) services.AddSingleton<IMcpInvocationAuditPort>(Audit);
            provider = services.BuildServiceProvider();
        }
        internal ValueTask<CallToolResult> Call(IDictionary<string, JsonElement> args) => new McpCallHandler(provider).ExecuteAsync(Tool, args, user, CancellationToken.None);
        public void Dispose() => provider.Dispose();
    }

    private sealed class Repository : IIncidentListProjectionRepositoryPort
    {
        internal List<IncidentListQuery> Queries { get; } = [];
        internal bool Changed { get; set; }
        internal string? Defect { get; set; }
        internal long? GenerationCount { get; set; }
        public ValueTask<IncidentListPage> ListIncidentsAsync(IncidentListQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            if (Changed) throw new IncidentListChangedException();
            DateTimeOffset snapshot = query.SnapshotUtc ?? query.ToUtc;
            var revision = query.TargetRevision ?? new ObservationTargetRevision(3);
            bool more = query.Cursor is null;
            DateTimeOffset opened = query.FromUtc.AddMinutes(1);
            var item = new IncidentListItem(more ? First : Second, opened, more ? null : opened, more ? 0 : 1);
            if (GenerationCount is { } count) item = item with { GenerationCount = count, LatestGenerationObservedAtUtc = count == 0 ? null : opened };
            var cursor = more ? new IncidentListCursor(Target, revision, query.FromUtc, query.ToUtc, snapshot, 7, opened, First) : null;
            var page = new IncidentListPage(Target, query.FromUtc, query.ToUtc, [item], revision, snapshot, 7, more, cursor);
            page = Defect switch
            {
                "other-target" => page with { TargetId = Other },
                "duplicate" => page with { Items = [item, item] },
                "future-generation" => page with { Items = [item with { LatestGenerationObservedAtUtc = snapshot.AddSeconds(1), GenerationCount = 1 }] },
                "count-mismatch" => page with { Items = [item with { GenerationCount = 1 }] },
                "wrong-cursor" => page with { NextCursor = new IncidentListCursor(Target, revision, query.FromUtc, query.ToUtc, snapshot, 7, opened, Second) },
                _ => page
            };
            return ValueTask.FromResult(page);
        }
    }

    private sealed class AuditPort : IMcpInvocationAuditPort
    {
        internal List<McpInvocationAuditRecord> Records { get; } = [];
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        {
            Records.Add(request.Record);
            return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch));
        }
    }
}
