using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpSqlBaselineTests
{
    private const string Catalog = "list_sql_metric_catalog_v2", Baselines = "get_sql_metric_baselines_v2";
    private const string Key = "engine.batch_requests_per_second", Group = "S-1-5-21-9231";
    private static readonly Guid Target = Guid.Parse("c5f422ec-55dc-4f44-b3e0-502fce104af7");
    private static readonly Guid Other = Guid.Parse("c5f422ec-55dc-4f44-b3e0-502fce104af8");
    private static readonly DateTimeOffset End = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SqlDiscoveryAdvertisesOnlyActivatedKeysAndAuditsWithoutTarget()
    {
        using var fixture = new Fixture();
        CallToolResult result = await fixture.Call(Catalog, []);
        Assert.False(result.IsError, Text(result));
        JsonElement data = result.StructuredContent!.Value.GetProperty("data");
        Assert.Equal(2, data.GetProperty("catalogVersion").GetInt32());
        Assert.Equal(MetricCatalogV2.Checksum, data.GetProperty("catalogChecksum").GetString());
        Assert.Equal(Baselines, data.GetProperty("baselineTool").GetString());
        string[] keys = data.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("metricKey").GetString()!).ToArray();
        Assert.Equal(MetricCatalogV2.All.Where(d => d.Source == AnalyticsMetricSource.SqlEngine).Select(d => d.Metric.Key).Order(StringComparer.Ordinal), keys);
        using JsonDocument schema = JsonDocument.Parse(McpCatalog.Definitions.Single(d => d.Name == Baselines).InputSchemaJson);
        Assert.Equal(keys, schema.RootElement.GetProperty("properties").GetProperty("metricKey").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        JsonSchemaAssertions.AssertValid(result.StructuredContent.Value, McpCatalog.OutputSchema(Catalog), Catalog);
        Assert.Null(Assert.Single(fixture.Audit.Records).TargetId);
        Assert.Equal(0, fixture.Repository.Calls);
    }

    [Theory]
    [InlineData("no_data")]
    [InlineData("unavailable")]
    [InlineData("partial")]
    [InlineData("complete")]
    public async Task SqlBaselinePreservesNullableStatisticsAndExactIdentityWithOneAudit(string state)
    {
        using var fixture = new Fixture(state: state);
        CallToolResult result = await fixture.Call(Baselines, Arguments());
        Assert.False(result.IsError, Text(result));
        JsonElement data = result.StructuredContent!.Value.GetProperty("data");
        Assert.Equal(state, data.GetProperty("state").GetString());
        Assert.Equal(Target, data.GetProperty("targetId").GetGuid());
        Assert.Equal("batches_per_second", data.GetProperty("unit").GetString());
        Assert.Equal("sql-baseline-v2", data.GetProperty("algorithmVersion").GetString());
        Assert.Equal("sql-rollup-v2", data.GetProperty("sourceAlgorithmVersion").GetString());
        JsonElement items = data.GetProperty("items");
        Assert.Equal(state == "no_data" ? 0 : 168, items.GetArrayLength());
        if (state != "no_data")
        {
            Assert.Equal(Enumerable.Range(0, 168), items.EnumerateArray().Select(r => r.GetProperty("hourOfWeek").GetInt32()));
            Assert.Equal(state == "unavailable" ? JsonValueKind.Null : JsonValueKind.Number, items[0].GetProperty("median").ValueKind);
        }
        Assert.False(data.TryGetProperty("nextCursor", out _));
        JsonSchemaAssertions.AssertValid(result.StructuredContent.Value, McpCatalog.OutputSchema(Baselines), Baselines);
        McpInvocationAuditRecord audit = Assert.Single(fixture.Audit.Records);
        Assert.Equal(McpInvocationOutcome.Succeeded, audit.Outcome); Assert.Equal(Target, audit.TargetId!.Value);
        Assert.Equal(1, fixture.Repository.Calls);
        Assert.Equal(168, fixture.Repository.Request!.Limit);
        Assert.Equal(End, fixture.Repository.Request.ToUtc);
    }

    [Theory]
    [InlineData("targetRevision", 0)]
    [InlineData("generation", 0)]
    [InlineData("metricKey", "host.cpu.percent")]
    [InlineData("fromUtc", "2026-08-05T00:00:00Z")]
    [InlineData("toUtc", "2026-09-01T01:00:00Z")]
    [InlineData("snapshotUtc", "2026-08-31T00:00:00Z")]
    [InlineData("limit", 1)]
    [InlineData("cursor", "bogus")]
    public async Task InvalidIdentityAndPaginationNeverReachRepository(string field, object value)
    {
        using var fixture = new Fixture(); var args = Arguments(); args[field] = JsonSerializer.SerializeToElement(value);
        CallToolResult result = await fixture.Call(Baselines, args);
        AssertError(result, "invalid_request"); Assert.Equal(0, fixture.Repository.Calls);
        Assert.Equal(McpInvocationOutcome.Invalid, Assert.Single(fixture.Audit.Records).Outcome);
    }

    [Fact]
    public async Task MixedViewerAuditorScopeCannotReadOtherTargetBaseline()
    {
        using var fixture = new Fixture(); var args = Arguments(); args["instanceId"] = JsonSerializer.SerializeToElement(Other);
        AssertError(await fixture.Call(Baselines, args), "forbidden");
        Assert.Equal(0, fixture.Repository.Calls);
        Assert.Equal(McpInvocationOutcome.Denied, Assert.Single(fixture.Audit.Records).Outcome);
    }

    [Theory]
    [InlineData(ApplicationRole.Auditor)]
    [InlineData(ApplicationRole.SecurityAdministrator)]
    public async Task DiscoveryRejectsNonReadRoles(ApplicationRole role)
    {
        using var fixture = new Fixture(role);
        AssertError(await fixture.Call(Catalog, []), "forbidden");
        Assert.Equal(McpInvocationOutcome.Denied, Assert.Single(fixture.Audit.Records).Outcome);
    }

    [Fact]
    public async Task CorruptRepositoryStatisticsAreWithheldAndAuditedAsFailure()
    {
        using var fixture = new Fixture(); fixture.Repository.Rows = fixture.Repository.Rows.Take(167).ToArray();
        AssertError(await fixture.Call(Baselines, Arguments()), "request_failed");
        Assert.Equal(McpInvocationOutcome.RepositoryFailure, Assert.Single(fixture.Audit.Records).Outcome);
    }

    [Fact]
    public async Task PositiveInt64RevisionAndGenerationReachTheServiceWithoutNarrowing()
    {
        using var fixture = new Fixture(); const long identity = 3_000_000_000;
        fixture.Repository.Rows = fixture.Repository.Rows.Select(r => r with { Statistics = r.Statistics with { Generation = identity } }).ToArray();
        var args = Arguments(); args["targetRevision"] = JsonSerializer.SerializeToElement(identity); args["generation"] = JsonSerializer.SerializeToElement(identity);
        CallToolResult result = await fixture.Call(Baselines, args); Assert.False(result.IsError, Text(result));
        Assert.Equal(identity, result.StructuredContent!.Value.GetProperty("data").GetProperty("targetRevision").GetInt64());
        Assert.Equal(identity, result.StructuredContent.Value.GetProperty("data").GetProperty("generation").GetInt64());
    }

    [Fact]
    public async Task MissingAuditWithholdsTheBaselinePayload()
    {
        using var fixture = new Fixture(withAudit: false);
        AssertError(await fixture.Call(Baselines, Arguments()), "audit_unavailable");
        Assert.Empty(fixture.Audit.Records);
    }

    [Fact]
    public async Task UnexpectedCatalogInstanceArgumentDoesNotBecomeAnAuditTarget()
    {
        using var fixture = new Fixture();
        AssertError(await fixture.Call(Catalog, new() { ["instanceId"] = JsonSerializer.SerializeToElement(Target) }), "invalid_request");
        Assert.Null(Assert.Single(fixture.Audit.Records).TargetId);
    }

    private static Dictionary<string, JsonElement> Arguments() => new()
    {
        ["instanceId"] = JsonSerializer.SerializeToElement(Target), ["targetRevision"] = JsonSerializer.SerializeToElement(1),
        ["metricKey"] = JsonSerializer.SerializeToElement(Key), ["fromUtc"] = JsonSerializer.SerializeToElement("2026-08-04T00:00:00Z"),
        ["toUtc"] = JsonSerializer.SerializeToElement("2026-09-01T00:00:00Z"),
    };
    private static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
    private static void AssertError(CallToolResult result, string code)
    { Assert.True(result.IsError); Assert.Null(result.StructuredContent); using var json = JsonDocument.Parse(Text(result)); Assert.Equal(code, json.RootElement.GetProperty("code").GetString()); }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, Group), new Claim(ClaimTypes.GroupSid, Group), new Claim(ClaimTypes.GroupSid, "S-1-5-21-9232")], "Windows"));
        internal AuditPort Audit { get; } = new();
        internal RepositoryProxy Repository { get; }
        internal Fixture(ApplicationRole role = ApplicationRole.Viewer, string state = "unavailable", bool withAudit = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new WindowsGroupRoleResolver([
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier(Group), [role], false, [new(Target)]),
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-9232"), [ApplicationRole.Auditor], false, [new(Other)])]));
            var repository = DispatchProxy.Create<IAnalyticsRepositoryPort, RepositoryProxy>(); Repository = (RepositoryProxy)repository;
            Repository.Rows = state == "no_data" ? [] : Enumerable.Range(0, 168).Select(hour => new SqlMetricBaselineResult(new BaselineResult
            {
                MetricKey = Key, HourOfWeek = hour, CompleteDays = state == "complete" ? 28 : state == "partial" ? 7 : 0,
                SampleCount = state == "complete" ? 4 : state == "partial" ? 1 : 0,
                Coverage = state == "complete" ? 1 : state == "partial" ? .25 : 0,
                Confidence = state == "complete" ? .5 : state == "partial" ? .03125 : 0,
                Median = state == "unavailable" ? null : 32.5, Mad = state == "unavailable" ? null : 2,
                P10 = state == "unavailable" ? null : 30, P90 = state == "unavailable" ? null : 35,
                WindowStartUtc = End.AddDays(-28), WindowEndUtc = End, VisibilityState = state,
            }, End.AddHours(1))).ToArray();
            services.AddSingleton(repository); services.AddSingleton<SqlMetricBaselineQueryService>();
            if (withAudit) services.AddSingleton<IMcpInvocationAuditPort>(Audit);
            provider = services.BuildServiceProvider();
        }
        internal ValueTask<CallToolResult> Call(string tool, Dictionary<string, JsonElement> args) => new McpCallHandler(provider).ExecuteAsync(tool, args, user, default);
        public void Dispose() => provider.Dispose();
    }
    private sealed class AuditPort : IMcpInvocationAuditPort
    {
        internal List<McpInvocationAuditRecord> Records { get; } = [];
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        { Records.Add(request.Record); return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch)); }
    }
    public class RepositoryProxy : DispatchProxy
    {
        internal IReadOnlyList<SqlMetricBaselineResult> Rows { get; set; } = [];
        internal int Calls { get; private set; }
        internal AnalyticsQueryRequest? Request { get; private set; }
        // DispatchProxy returns the boxed ValueTask to the real service, which awaits it once.
#pragma warning disable CA2012
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        { Assert.Equal("ReadSqlBaselinesAsync", targetMethod!.Name); Calls++; Request = (AnalyticsQueryRequest)args![0]!; return ValueTask.FromResult(Rows); }
#pragma warning restore CA2012
    }
}
