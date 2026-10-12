using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpAvailabilityGroupEvidenceTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid()), Other = new(Guid.NewGuid());
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly string Group = new('a', 64), Local = new('1', 64), Remote = new('2', 64), Database = new('b', 64);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [Theory]
    [InlineData("get_availability_replicas_v2")]
    [InlineData("get_availability_databases_v2")]
    public async Task SignedPagesFreezeDefaultCutoffAndPreserveFullIdentityAndNulls(string tool)
    {
        using var fixture = new Fixture();
        var args = Arguments();
        CallToolResult first = await fixture.Call(tool, args);
        JsonElement data = Success(first, tool);
        JsonElement item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal(Local, item.GetProperty("replicaFingerprint").GetString());
        Assert.Equal("complete", data.GetProperty("state").GetString());
        Assert.Equal("retained", data.GetProperty("evidenceStatus").GetString());
        if (tool == "get_availability_databases_v2")
        {
            Assert.Equal(0, item.GetProperty("logSendQueueKb").GetInt64());
            Assert.Equal("2026-09-30T08:15:00.000001", item.GetProperty("sourceLastCommitLocal").GetString());
        }
        string token = data.GetProperty("nextCursor").GetString()!;
        var cursor = fixture.Signer.Decode<AvailabilityGroupEvidenceCursor>(token, tool, args, Options);
        Assert.Equal(Target, cursor.TargetId); Assert.Equal(Run, cursor.RunId); Assert.Equal(Group, cursor.GroupFingerprint);
        Assert.Equal(tool == "get_availability_databases_v2" ? Database : null, cursor.DatabaseFingerprint);
        args["cursor"] = JsonSerializer.SerializeToElement(token);
        JsonElement last = Success(await fixture.Call(tool, args), tool);
        JsonElement remote = Assert.Single(last.GetProperty("items").EnumerateArray());
        Assert.Equal(Remote, remote.GetProperty("replicaFingerprint").GetString());
        Assert.Equal(cursor.AtUtc, fixture.Repository.Requests[1].AtUtc);
        Assert.Equal(fixture.Repository.Requests[0].AtUtc, fixture.Repository.Requests[1].AtUtc);
        Assert.False(last.GetProperty("hasMore").GetBoolean());
        Assert.False(last.TryGetProperty("nextCursor", out _));
        if (tool == "get_availability_databases_v2")
        {
            Assert.Equal(JsonValueKind.Null, remote.GetProperty("logSendQueueKb").ValueKind);
            Assert.Equal(JsonValueKind.Null, remote.GetProperty("sourceLastCommitLocal").ValueKind);
            Assert.Equal("SUSPECT", remote.GetProperty("databaseState").GetString());
        }
        else Assert.Equal(JsonValueKind.Null, remote.GetProperty("operationalState").ValueKind);
        Assert.Equal(2, fixture.Audit.Records.Count);
        Assert.All(fixture.Audit.Records, record => Assert.Equal(McpInvocationOutcome.Succeeded, record.Outcome));
    }

    [Theory]
    [InlineData("get_availability_replicas_v2")]
    [InlineData("get_availability_databases_v2")]
    public async Task NoSignerDisclosesMoreEvidenceWithoutThrowingOrOfferingAnUnsignedCursor(string tool)
    {
        using var fixture = new Fixture(signer: false);
        JsonElement data = Success(await fixture.Call(tool, Arguments()), tool);
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.False(data.TryGetProperty("nextCursor", out _));
        Assert.Single(fixture.Audit.Records);
    }

    [Theory]
    [InlineData(AvailabilityGroupEvidenceStatus.NotCollected, "notCollected")]
    [InlineData(AvailabilityGroupEvidenceStatus.NoData, "noData")]
    [InlineData(AvailabilityGroupEvidenceStatus.WithheldByByteLimit, "withheldByByteLimit")]
    [InlineData(AvailabilityGroupEvidenceStatus.CollectionFailed, "collectionFailed")]
    [InlineData(AvailabilityGroupEvidenceStatus.EvidenceNotRetained, "evidenceNotRetained")]
    public async Task RequiredNullableHeaderFieldsAndCoverageValidateAgainstTheAdvertisedSchema(
        AvailabilityGroupEvidenceStatus status, string expected)
    {
        using var fixture = new Fixture();
        fixture.Repository.Status = status;
        foreach (string tool in new[] { "get_availability_replicas_v2", "get_availability_databases_v2" })
        {
            JsonElement data = Success(await fixture.Call(tool, Arguments()), tool);
            Assert.Equal(expected, data.GetProperty("evidenceStatus").GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("sourceObservedAtUtc").ValueKind);
            Assert.Empty(data.GetProperty("items").EnumerateArray());
        }
        Assert.Equal(2, fixture.Audit.Records.Count);
    }

    [Fact]
    public async Task MixedTargetGrantsCannotReachEitherRepositoryStream()
    {
        using var fixture = new Fixture();
        var args = Arguments(); args["instanceId"] = JsonSerializer.SerializeToElement(Other.Value);
        foreach (string tool in new[] { "get_availability_replicas_v2", "get_availability_databases_v2" })
            Error(await fixture.Call(tool, args), "forbidden");
        Assert.Empty(fixture.Repository.Requests);
        Assert.Equal(2, fixture.Audit.Records.Count);
        Assert.All(fixture.Audit.Records, record => Assert.Equal(McpInvocationOutcome.Denied, record.Outcome));
    }

    [Fact]
    public async Task ToolAndCutoffChangesRejectContinuationAndStaleRunsHaveAnActionableError()
    {
        using var fixture = new Fixture();
        const string tool = "get_availability_replicas_v2";
        var args = Arguments();
        JsonElement data = Success(await fixture.Call(tool, args), tool);
        args["cursor"] = data.GetProperty("nextCursor").Clone();
        Error(await fixture.Call("get_availability_databases_v2", args), "invalid_request");
        var changed = new Dictionary<string, JsonElement>(args)
        { ["atUtc"] = JsonSerializer.SerializeToElement("2026-09-30T00:00:00Z") };
        Error(await fixture.Call(tool, changed), "invalid_request");
        Assert.Single(fixture.Repository.Requests);
        fixture.Repository.Changed = true;
        CallToolResult stale = await fixture.Call(tool, args);
        Error(stale, "cursor_stale");
        Assert.Contains("Restart", Assert.Single(stale.Content.OfType<TextContentBlock>()).Text, StringComparison.Ordinal);
        Assert.Equal(4, fixture.Audit.Records.Count);
    }

    [Fact]
    public async Task InvalidSubmicrosecondCutoffNamesTheFieldBeforeReading()
    {
        using var fixture = new Fixture();
        var args = Arguments(); args["atUtc"] = JsonSerializer.SerializeToElement("2026-09-30T00:00:00.0000001Z");
        CallToolResult result = await fixture.Call("get_availability_databases_v2", args);
        Error(result, "invalid_request");
        Assert.Contains("atUtc", Assert.Single(result.Content.OfType<TextContentBlock>()).Text, StringComparison.Ordinal);
        Assert.Empty(fixture.Repository.Requests); Assert.Single(fixture.Audit.Records);
    }

    private static Dictionary<string, JsonElement> Arguments() => new(StringComparer.Ordinal)
    { ["instanceId"] = JsonSerializer.SerializeToElement(Target.Value), ["limit"] = JsonSerializer.SerializeToElement(1) };
    private static JsonElement Success(CallToolResult result, string tool)
    {
        Assert.False(result.IsError, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        JsonSchemaAssertions.AssertValid(result.StructuredContent!.Value, McpCatalog.OutputSchema(tool), tool);
        return result.StructuredContent.Value.GetProperty("data");
    }
    private static void Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError); Assert.Null(result.StructuredContent);
        using JsonDocument json = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly ClaimsPrincipal user = new(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-8301"), new Claim(ClaimTypes.GroupSid, "S-1-5-21-8302"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-8303")], "Windows"));
        internal Repository Repository { get; } = new();
        internal AuditPort Audit { get; } = new();
        internal McpCursorSigner Signer { get; } = new(new byte[32]);
        internal Fixture(bool signer = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new WindowsGroupRoleResolver([
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-8302"), [ApplicationRole.Viewer], false, [Target]),
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-8303"), [ApplicationRole.Auditor], false, [Other])]));
            services.AddSingleton<IAvailabilityGroupEvidenceRepositoryPort>(Repository);
            services.AddSingleton<IAvailabilityGroupEvidenceQueryService, AvailabilityGroupEvidenceQueryService>();
            services.AddSingleton<IMcpInvocationAuditPort>(Audit);
            if (signer) services.AddSingleton(Signer);
            provider = services.BuildServiceProvider();
        }
        internal ValueTask<CallToolResult> Call(string tool, IDictionary<string, JsonElement> args) =>
            new McpCallHandler(provider).ExecuteAsync(tool, args, user, CancellationToken.None);
        public void Dispose() => provider.Dispose();
    }

    private sealed class Repository : IAvailabilityGroupEvidenceRepositoryPort
    {
        internal List<AvailabilityGroupEvidenceRequest> Requests { get; } = [];
        internal AvailabilityGroupEvidenceStatus Status { get; set; } = AvailabilityGroupEvidenceStatus.Retained;
        internal bool Changed { get; set; }
        public ValueTask<AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>> ReadReplicasAsync(
            AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken)
        {
            Begin(request); string key = request.Cursor is null ? Local : Remote;
            return ValueTask.FromResult(new AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>(Header(request),
                Status == AvailabilityGroupEvidenceStatus.Retained ? [new(Identity(key), true, key == Local ? "PRIMARY" : "SECONDARY",
                    key == Local ? "FAILED_NO_QUORUM" : null, "DISCONNECTED")] : [], Next(request, key)));
        }
        public ValueTask<AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>> ReadDatabasesAsync(
            AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken)
        {
            Begin(request); string key = request.Cursor is null ? Local : Remote;
            return ValueTask.FromResult(new AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>(Header(request),
                Status == AvailabilityGroupEvidenceStatus.Retained ? [new(Identity(key), Database, true, "SYNCHRONIZED", "NOT_HEALTHY",
                    "SUSPECT", false, key == Local ? 0 : null, null, long.MaxValue, 0,
                    key == Local ? new DateTime(2026, 9, 30, 8, 15, 0, DateTimeKind.Unspecified).AddTicks(10) : null)] : [], Next(request, key)));
        }
        private void Begin(AvailabilityGroupEvidenceRequest request)
        { Requests.Add(request); if (Changed && request.Cursor is not null) throw new AvailabilityGroupEvidenceCursorStaleException(); }
        private static AvailabilityReplicaEvidenceIdentity Identity(string key) =>
            new(Group, key, Local, key == Local, AvailabilityVisibilityScope.PrimaryAllKnown);
        private AvailabilityGroupEvidenceCursor? Next(AvailabilityGroupEvidenceRequest request, string key) =>
            Status == AvailabilityGroupEvidenceStatus.Retained && request.Cursor is null ?
                new(Target, request.AtUtc, 1, Run, request.Stream, Group, key,
                    request.Stream == AvailabilityGroupEvidenceKind.Databases ? Database : null) : null;
        private AvailabilityGroupEvidenceHeader Header(AvailabilityGroupEvidenceRequest request)
        {
            bool retained = Status == AvailabilityGroupEvidenceStatus.Retained, expired = Status == AvailabilityGroupEvidenceStatus.EvidenceNotRetained;
            bool absent = Status == AvailabilityGroupEvidenceStatus.NotCollected, withheld = Status == AvailabilityGroupEvidenceStatus.WithheldByByteLimit;
            return new(Target, request.AtUtc, 1, absent ? null : Run, retained ? request.AtUtc.AddSeconds(-2) : null,
                absent ? null : request.AtUtc.AddSeconds(-1), Status switch
                {
                    AvailabilityGroupEvidenceStatus.Retained => OperationalObservationState.Complete,
                    AvailabilityGroupEvidenceStatus.WithheldByByteLimit => OperationalObservationState.Partial,
                    AvailabilityGroupEvidenceStatus.CollectionFailed => OperationalObservationState.PermissionDenied,
                    AvailabilityGroupEvidenceStatus.EvidenceNotRetained => OperationalObservationState.Degraded,
                    _ => OperationalObservationState.NoData,
                }, Status, retained || expired || withheld ? 4 : 0, retained || expired ? 4 : 0, retained ? 4 : 0,
                withheld ? CollectorLossKind.ResponseByteLimit : CollectorLossKind.None, withheld, true);
        }
    }

    private sealed class AuditPort : IMcpInvocationAuditPort
    {
        internal List<McpInvocationAuditRecord> Records { get; } = [];
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        { Records.Add(request.Record); return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch)); }
    }
}
