using System.Text.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpMetricCatalogTests
{
    private const string ToolName = "list_metric_catalog";
    private const string ActorSid = "S-1-5-21-1901";
    private const string GroupSid = "S-1-5-21-1902";
    private static readonly MonitoredInstanceId ScopedTarget = new(Guid.Parse("c5f422ec-55dc-4f44-b3e0-502fce104af7"));
    private static readonly string[] ExpectedRootFields = ["checksum", "items", "version"];
    private static readonly string[] ExpectedItemFields = ["aggregation", "dimensionKeys", "displayName", "metricKey", "source", "unit"];
    private static readonly string[] ExpectedVolumeDimensions = ["volume"];
    private static readonly (string Key, string Unit, string Source, bool Volume)[] ExpectedMetrics =
    [
        ("host.cpu.percent", "percent", "host", false),
        ("host.memory.available_bytes", "bytes", "host", false),
        ("host.memory.committed_bytes", "bytes", "host", false),
        ("host.volume.free_bytes", "bytes", "host", true),
        ("host.volume.queue_length", "count", "host", true),
        ("host.volume.read_latency_ms", "milliseconds", "host", true),
        ("host.volume.total_bytes", "bytes", "host", true),
        ("host.volume.write_latency_ms", "milliseconds", "host", true),
        ("replication.latency_seconds", "seconds", "replication", false),
        ("replication.pending_commands", "count", "replication", false)
    ];

    [Fact]
    public void MetricCatalogAdvertisesClosedNoArgumentReadOnlyContract()
    {
        Tool tool = Assert.Single(McpCatalog.CreateTools(), x => x.ProtocolTool.Name == ToolName).ProtocolTool;
        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.Empty(tool.InputSchema.GetProperty("properties").EnumerateObject());
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        if (tool.InputSchema.TryGetProperty("required", out JsonElement required)) Assert.Empty(required.EnumerateArray());
        Assert.True(tool.Annotations?.ReadOnlyHint);
        Assert.True(tool.Annotations?.IdempotentHint);
        Assert.False(tool.Annotations?.DestructiveHint);
        Assert.False(tool.Annotations?.OpenWorldHint);
    }

    [Theory]
    [InlineData(ApplicationRole.Viewer)]
    [InlineData(ApplicationRole.Operator)]
    [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task MetricCatalogReadRolesWithRestrictedGroupScopeSucceedAndAuditWithoutTarget(ApplicationRole role)
    {
        using var fixture = new Fixture(role);
        CallToolResult result = await fixture.CallAsync();

        Assert.False(result.IsError, Text(result));
        Assert.Equal(10, result.StructuredContent!.Value.GetProperty("data").GetProperty("items").GetArrayLength());
        Assert.DoesNotContain(ScopedTarget.Value.ToString("D"), Text(result), StringComparison.OrdinalIgnoreCase);
        McpInvocationAuditRecord audit = AssertAudit(fixture, McpInvocationOutcome.Succeeded, McpAuthorizationResult.Allowed);
        Assert.Equal(ActorSid, audit.Actor.Value);
        Assert.True(audit.ResponseBytes > 0);
        Assert.Equal(McpParameterCanonicalizer.Digest(JsonSerializer.SerializeToElement(new { })).ToString(), audit.ParameterDigest.ToString());
    }

    [Fact]
    public async Task MetricCatalogReturnsExactEnabledMetadataAndMatchesMetricInputEnumsAndOutputSchema()
    {
        using var fixture = new Fixture();
        CallToolResult result = await fixture.CallAsync();
        Assert.False(result.IsError, Text(result));
        JsonElement structured = result.StructuredContent!.Value;
        JsonElement data = structured.GetProperty("data");
        Assert.Equal(ExpectedRootFields, data.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(MetricCatalogV1.Version, data.GetProperty("version").GetInt32());
        Assert.Equal(MetricCatalogV1.Checksum, data.GetProperty("checksum").GetString());
        JsonElement items = data.GetProperty("items");
        string[] keys = items.EnumerateArray().Select(x => x.GetProperty("metricKey").GetString()!).ToArray();
        Assert.Equal(ExpectedMetrics.Select(x => x.Key), keys);
        Assert.Equal(MetricCatalogV1.All.Where(x => x.Enabled).Select(x => x.Key).Order(StringComparer.Ordinal), keys);
        foreach ((string key, string unit, string source, bool volume) in ExpectedMetrics)
        {
            JsonElement item = Assert.Single(items.EnumerateArray(), x => x.GetProperty("metricKey").GetString() == key);
            Assert.Equal(ExpectedItemFields, item.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
            Assert.Equal(MetricCatalogV1.Get(key).DisplayName, item.GetProperty("displayName").GetString());
            Assert.Equal(unit, item.GetProperty("unit").GetString());
            Assert.Equal(source, item.GetProperty("source").GetString());
            Assert.Equal("gauge", item.GetProperty("aggregation").GetString());
            Assert.Equal(volume ? ExpectedVolumeDimensions : [], item.GetProperty("dimensionKeys").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }
        foreach (string name in new[] { "get_metric_series", "compare_metric_windows", "get_storage_forecast" })
        {
            using JsonDocument schema = JsonDocument.Parse(Assert.Single(McpCatalog.Definitions, x => x.Name == name).InputSchemaJson);
            Assert.Equal(keys, schema.RootElement.GetProperty("properties").GetProperty("metricKey").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }
        Assert.Equal(structured.GetRawText(), Text(result));
        JsonSchemaAssertions.AssertValid(structured, McpCatalog.OutputSchema(ToolName), ToolName);
        AssertAudit(fixture, McpInvocationOutcome.Succeeded, McpAuthorizationResult.Allowed);
    }

    [Theory]
    [InlineData(ApplicationRole.SecurityAdministrator)]
    [InlineData(ApplicationRole.Auditor)]
    [InlineData(ApplicationRole.CollectorService)]
    [InlineData(ApplicationRole.QueryTextReader)]
    public async Task MetricCatalogNonReadRolesAreDeniedAndAuditedOnce(ApplicationRole role)
    {
        using var fixture = new Fixture(role);
        AssertError(await fixture.CallAsync(), "forbidden");
        AssertAudit(fixture, McpInvocationOutcome.Denied, McpAuthorizationResult.Denied);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task MetricCatalogDisabledUnauthenticatedOrUnmappedCallersAreDeniedAndAuditedOnce(bool active, bool authenticated, bool groupClaim)
    {
        using var fixture = new Fixture(active: active, authenticated: authenticated, groupClaim: groupClaim);
        AssertError(await fixture.CallAsync(), "forbidden");
        AssertAudit(fixture, McpInvocationOutcome.Denied, McpAuthorizationResult.Denied);
    }

    [Theory]
    [InlineData("instanceId")]
    [InlineData("limit")]
    [InlineData("cursor")]
    [InlineData("role")]
    public async Task MetricCatalogUnsupportedArgumentsAreInvalidAndAuditedOnceWithoutTarget(string argument)
    {
        using var fixture = new Fixture();
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [argument] = argument == "limit" ? JsonSerializer.SerializeToElement(1) : JsonSerializer.SerializeToElement(ScopedTarget.Value.ToString("D"))
        };
        AssertError(await fixture.CallAsync(args), "invalid_request");
        McpInvocationAuditRecord audit = AssertAudit(fixture, McpInvocationOutcome.Invalid, McpAuthorizationResult.NotApplicable);
        Assert.Equal(McpParameterCanonicalizer.Digest(JsonSerializer.SerializeToElement(args)).ToString(), audit.ParameterDigest.ToString());
    }

    [Fact]
    public async Task MetricCatalogMissingAuditWithholdsAllCatalogMetadata()
    {
        using var fixture = new Fixture(withAudit: false);
        AssertError(await fixture.CallAsync(), "audit_unavailable");
        Assert.Empty(fixture.Audit.Records);
    }

    private static McpInvocationAuditRecord AssertAudit(Fixture fixture, McpInvocationOutcome outcome, McpAuthorizationResult authorization)
    {
        McpInvocationAuditRecord audit = Assert.Single(fixture.Audit.Records);
        Assert.Equal(ToolName, audit.Tool.Value);
        Assert.Equal("mcp.tool." + ToolName, audit.Action.Value);
        Assert.Equal(outcome, audit.Outcome);
        Assert.Equal(authorization, audit.Authorization);
        Assert.Null(audit.TargetId);
        Assert.Null(audit.IncidentId);
        if (outcome != McpInvocationOutcome.Succeeded) Assert.Equal(0, audit.ResponseBytes);
        return audit;
    }

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.True(result.IsError);
        using JsonDocument error = JsonDocument.Parse(Text(result));
        Assert.Equal(code, error.RootElement.GetProperty("code").GetString());
        Assert.Null(result.StructuredContent);
        Assert.DoesNotContain("host.cpu.percent", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain(MetricCatalogV1.Checksum, Text(result), StringComparison.Ordinal);
    }

    private static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly ClaimsPrincipal user;
        internal RecordingAuditPort Audit { get; } = new();

        internal Fixture(ApplicationRole role = ApplicationRole.Viewer, bool active = true, bool authenticated = true, bool groupClaim = true, bool withAudit = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new WindowsGroupRoleResolver(
                [new WindowsGroupRoleBinding(new ActorSecurityIdentifier(GroupSid), [role], allTargets: false, [ScopedTarget])],
                active ? [] : [new ActorSecurityIdentifier(ActorSid)]));
            if (withAudit) services.AddSingleton<IMcpInvocationAuditPort>(Audit);
            provider = services.BuildServiceProvider();
            var claims = new List<Claim> { new(ClaimTypes.PrimarySid, ActorSid) };
            if (groupClaim) claims.Add(new Claim(ClaimTypes.GroupSid, GroupSid));
            user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "Windows" : null));
        }

        internal ValueTask<CallToolResult> CallAsync(IDictionary<string, JsonElement>? arguments = null) =>
            new McpCallHandler(provider).ExecuteAsync(ToolName, arguments, user, CancellationToken.None);

        public void Dispose() => provider.Dispose();
    }

    private sealed class RecordingAuditPort : IMcpInvocationAuditPort
    {
        internal List<McpInvocationAuditRecord> Records { get; } = [];
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        {
            Records.Add(request.Record);
            return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch));
        }
    }
}
