using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpCursorRegressionTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly Guid Run = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Snapshot = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static readonly McpCursorSigner Signer = new(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Windows"));
    private static readonly string[] WindowTools = ["search_diagnostic_events", "search_deadlocks", "get_top_queries", "get_query_history", "get_blocking_history", "get_metric_series", "get_job_failures"];
    private static readonly string[] BindingChanges = ["tamper", "limit", "instanceId", "fromUtc", "oversize"];

    public static IEnumerable<object[]> WindowCases() => WindowTools.SelectMany(tool => Enumerable.Range(0, 4).Select(mode => new object[] { tool, mode }));
    public static IEnumerable<object[]> BindingCases() => WindowTools.SelectMany(tool => BindingChanges.Select(change => new object[] { tool, change }));

    [Theory]
    [MemberData(nameof(WindowCases))]
    public async Task ContinuationKeepsEffectiveWindowAndOriginalRequestIdentity(string tool, int mode)
    {
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy);
        var handler = new McpCallHandler(provider);
        Dictionary<string, JsonElement> arguments = Arguments(tool);
        DateTimeOffset now = DateTimeOffset.UtcNow.AddHours(-1);
        DateTimeOffset to = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        if ((mode & 1) != 0) arguments["fromUtc"] = JsonSerializer.SerializeToElement(to.AddHours(-1).UtcDateTime.ToString("O"));
        if ((mode & 2) != 0) arguments["toUtc"] = JsonSerializer.SerializeToElement(to.UtcDateTime.ToString("O"));
        string original = JsonSerializer.Serialize(arguments);
        CallToolResult first = await handler.ExecuteAsync(tool, arguments, User, CancellationToken.None);
        AssertSuccess(first);
        string token = first.StructuredContent!.Value.GetProperty("data").GetProperty("nextCursor").GetString()!;
        Assert.Equal(original, JsonSerializer.Serialize(arguments));
        var continuation = new Dictionary<string, JsonElement>(arguments) { ["cursor"] = JsonSerializer.SerializeToElement(token) };
        CallToolResult second = await handler.ExecuteAsync(tool, continuation, User, CancellationToken.None);
        AssertSuccess(second);
        Assert.Equal(2, proxy.Requests.Count);
        Assert.Equal(Window(proxy.Requests[0]), Window(proxy.Requests[1]));
        Assert.NotNull(proxy.Requests[1].GetType().GetProperty("Cursor")!.GetValue(proxy.Requests[1]));
        Assert.Equal(original, JsonSerializer.Serialize(arguments));
        Assert.False(second.StructuredContent!.Value.GetProperty("data").GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("list_instances")]
    [InlineData("get_active_alerts")]
    [InlineData("get_database_health")]
    [InlineData("get_file_io")]
    [InlineData("get_job_failures")]
    [InlineData("get_metric_series")]
    public async Task MissingSignerReturnsFirstPageWithoutUnsignedContinuation(string tool)
    {
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy, false);
        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync(tool, Arguments(tool), User, CancellationToken.None);
        AssertSuccess(result);
        JsonElement data = result.StructuredContent!.Value.GetProperty("data");
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.False(data.TryGetProperty("nextCursor", out _));
        Assert.Single(proxy.Requests);
        CallToolResult terminal = await new McpCallHandler(provider).ExecuteAsync(tool, Arguments(tool), User, CancellationToken.None);
        AssertSuccess(terminal);
        Assert.False(terminal.StructuredContent!.Value.GetProperty("data").GetProperty("hasMore").GetBoolean());
        Assert.False(terminal.StructuredContent.Value.GetProperty("data").TryGetProperty("nextCursor", out _));
    }

    [Theory]
    [InlineData("list_instances")]
    [InlineData("get_metric_series")]
    [InlineData("get_job_failures")]
    public async Task MissingSignerRejectsCursorBeforeQuery(string tool)
    {
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy, false);
        Dictionary<string, JsonElement> arguments = Arguments(tool);
        arguments["cursor"] = JsonSerializer.SerializeToElement("authenticated-looking-cursor");
        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync(tool, arguments, User, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Empty(proxy.Requests);
    }

    [Theory]
    [InlineData("get_top_queries")]
    [InlineData("get_query_history")]
    public async Task FullQueryPerformanceTupleFitsOneSharedCursorBound(string tool)
    {
        var cursor = new QueryPerformanceCursorEnvelope(Target, 32767, Snapshot.AddDays(-1), Snapshot,
            QueryPerformanceMetric.DurationMilliseconds, Snapshot, Snapshot.AddTicks(-1234567), new string('a', 64),
            long.MaxValue, Run, new string('b', 64), new string('c', 32));
        object page = tool == "get_top_queries" ? new TopQueryPage([], true, Snapshot) { NextCursor = cursor }
            : new QueryHistoryPage([], true, Snapshot) { NextCursor = cursor };
        Dictionary<string, JsonElement> arguments = Arguments(tool);
        if (tool == "get_query_history") arguments["databaseId"] = JsonSerializer.SerializeToElement(32767);
        else arguments["metric"] = JsonSerializer.SerializeToElement("durationMilliseconds");
        CallToolResult result = McpResults.Json(page, Options, tool, arguments, Signer);
        string token = result.StructuredContent!.Value.GetProperty("data").GetProperty("nextCursor").GetString()!;
        Assert.InRange(token.Length, 1025, 2048);
        Assert.Equal(2048, McpCursorSigner.MaximumTokenLength);
        QueryPerformanceCursorEnvelope decoded = Signer.Decode<QueryPerformanceCursorEnvelope>(token, tool, arguments, Options);
        Assert.Equal(JsonSerializer.Serialize(cursor, Options), JsonSerializer.Serialize(decoded, Options));
        using JsonDocument schema = JsonDocument.Parse(McpCatalog.Definitions.Single(x => x.Name == tool).InputSchemaJson);
        JsonElement input = schema.RootElement.GetProperty("properties").GetProperty("cursor");
        Assert.Equal(McpCursorSigner.MaximumTokenLength, input.GetProperty("maxLength").GetInt32());
        Assert.Matches(input.GetProperty("pattern").GetString()!, token);
        Assert.Equal(McpCursorSigner.MaximumTokenLength, McpCatalog.OutputSchema(tool).GetProperty("properties").GetProperty("data").GetProperty("properties").GetProperty("nextCursor").GetProperty("maxLength").GetInt32());
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy);
        arguments["cursor"] = JsonSerializer.SerializeToElement(token);
        CallToolResult continued = await new McpCallHandler(provider).ExecuteAsync(tool, arguments, User, CancellationToken.None);
        AssertSuccess(continued);
        object query = Assert.Single(proxy.Requests);
        object receivedCursor = query.GetType().GetProperty("Cursor")!.GetValue(query)!;
        Assert.Equal(JsonSerializer.Serialize(cursor, Options), JsonSerializer.Serialize(receivedCursor, Options));
    }

    [Theory]
    [InlineData("get_metric_series")]
    [InlineData("get_job_failures")]
    public async Task LegacyCursorWithoutWindowCannotSilentlyChooseNewDefaults(string tool)
    {
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy);
        Dictionary<string, JsonElement> arguments = Arguments(tool);
        object cursor = tool == "get_metric_series"
            ? new MetricSeriesCursor(Target, "host.cpu.percent", Snapshot, Run, "{}", Snapshot, new ObservationTargetRevision(3))
            : new McpRawCursor("legacy-raw-cursor");
        string token = Signer.Encode(JsonSerializer.SerializeToElement(cursor, Options), tool, arguments);
        arguments["cursor"] = JsonSerializer.SerializeToElement(token);
        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync(tool, arguments, User, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Empty(proxy.Requests);
    }

    [Theory]
    [MemberData(nameof(BindingCases))]
    public async Task ChangedRequestOrTokenIsRejectedBeforeSecondQuery(string tool, string change)
    {
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy);
        var handler = new McpCallHandler(provider);
        Dictionary<string, JsonElement> arguments = Arguments(tool);
        CallToolResult first = await handler.ExecuteAsync(tool, arguments, User, CancellationToken.None);
        AssertSuccess(first);
        string token = first.StructuredContent!.Value.GetProperty("data").GetProperty("nextCursor").GetString()!;
        if (change == "tamper") token = token[..10] + (token[10] == 'A' ? 'B' : 'A') + token[11..];
        if (change == "oversize") token = new string('A', 2049);
        arguments["cursor"] = JsonSerializer.SerializeToElement(token);
        if (change == "limit") arguments["limit"] = JsonSerializer.SerializeToElement(2);
        if (change == "instanceId") arguments["instanceId"] = JsonSerializer.SerializeToElement(Run);
        if (change == "fromUtc") arguments["fromUtc"] = JsonSerializer.SerializeToElement(Window(proxy.Requests[0]).From.UtcDateTime.ToString("O"));
        CallToolResult second = await handler.ExecuteAsync(tool, arguments, User, CancellationToken.None);
        Assert.True(second.IsError);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task LegacyMetricCursorWithBothExplicitBoundsRemainsUsable()
    {
        const string tool = "get_metric_series";
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy);
        Dictionary<string, JsonElement> arguments = Arguments(tool);
        arguments["fromUtc"] = JsonSerializer.SerializeToElement(Snapshot.AddHours(-1).UtcDateTime.ToString("O"));
        arguments["toUtc"] = JsonSerializer.SerializeToElement(Snapshot.UtcDateTime.ToString("O"));
        var cursor = new MetricSeriesCursor(Target, "host.cpu.percent", Snapshot.AddMinutes(-1), Run, "{}", Snapshot, new ObservationTargetRevision(3));
        arguments["cursor"] = JsonSerializer.SerializeToElement(Signer.Encode(JsonSerializer.SerializeToElement(cursor, Options), tool, arguments));
        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync(tool, arguments, User, CancellationToken.None);
        AssertSuccess(result);
        Assert.Equal((Snapshot.AddHours(-1), Snapshot), Window(Assert.Single(proxy.Requests)));
    }

    [Theory]
    [InlineData("missing-bound")]
    [InlineData("unknown-context-field")]
    [InlineData("non-utc")]
    [InlineData("reverse")]
    [InlineData("explicit-mismatch")]
    [InlineData("unknown-cursor-field")]
    public async Task AuthenticatedMalformedPrivateContextIsRejectedBeforeQuery(string problem)
    {
        const string tool = "get_metric_series";
        using ServiceProvider provider = Provider(tool, out RecordingProxy proxy);
        Dictionary<string, JsonElement> arguments = Arguments(tool);
        var cursor = new MetricSeriesCursor(Target, "host.cpu.percent", Snapshot.AddMinutes(-1), Run, "{}", Snapshot, new ObservationTargetRevision(3));
        JsonObject payload = JsonSerializer.SerializeToNode(cursor, Options)!.AsObject();
        JsonObject context = new() { ["fromUtc"] = Snapshot.AddHours(-1).ToString("O"), ["toUtc"] = Snapshot.ToString("O") };
        if (problem == "missing-bound") context.Remove("toUtc");
        if (problem == "unknown-context-field") context["unexpected"] = true;
        if (problem == "non-utc") context["toUtc"] = Snapshot.ToOffset(TimeSpan.FromHours(1)).ToString("O");
        if (problem == "reverse") context["fromUtc"] = Snapshot.AddHours(1).ToString("O");
        if (problem == "explicit-mismatch") arguments["fromUtc"] = JsonSerializer.SerializeToElement(Snapshot.AddHours(-2).UtcDateTime.ToString("O"));
        if (problem == "unknown-cursor-field") payload["unexpected"] = true;
        payload["__mcpWindow"] = context;
        arguments["cursor"] = JsonSerializer.SerializeToElement(Signer.Encode(JsonSerializer.SerializeToElement(payload), tool, arguments));
        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync(tool, arguments, User, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Empty(proxy.Requests);
    }

    internal static Dictionary<string, JsonElement> Arguments(string tool)
    {
        var args = new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(1) };
        if (tool != "list_instances") args["instanceId"] = JsonSerializer.SerializeToElement(Target.Value);
        if (tool == "get_metric_series") args["metricKey"] = JsonSerializer.SerializeToElement("host.cpu.percent");
        if (tool == "get_top_queries") args["metric"] = JsonSerializer.SerializeToElement("cpuMilliseconds");
        if (tool == "get_query_history") { args["databaseId"] = JsonSerializer.SerializeToElement(1); args["queryFingerprint"] = JsonSerializer.SerializeToElement(new string('a', 64)); }
        return args;
    }

    private static ServiceProvider Provider(string tool, out RecordingProxy proxy, bool signer = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1"), [ApplicationRole.Viewer], true)]));
        services.AddSingleton<IMcpInvocationAuditPort, AuditPort>();
        if (signer) services.AddSingleton(Signer);
        Type type = tool switch
        {
            "list_instances" => typeof(IObservationTargetQueryService), "get_active_alerts" => typeof(IAlertQueryService),
            "get_database_health" or "get_file_io" => typeof(IHealthProjectionQueryService),
            "get_metric_series" => typeof(IMetricSeriesQueryService), "get_job_failures" => typeof(IOperationalHealthQueryService),
            "get_blocking_history" => typeof(IActivityProjectionQueryService), "search_deadlocks" => typeof(IDeadlockProjectionQueryService),
            "search_diagnostic_events" => typeof(IDiagnosticEventQueryService), _ => typeof(IQueryPerformanceApiQueryService)
        };
        object service = DispatchProxy.Create(type, typeof(RecordingProxy));
        proxy = (RecordingProxy)service;
        proxy.Tool = tool;
        services.AddSingleton(type, service);
        return services.BuildServiceProvider();
    }

    private static (DateTimeOffset From, DateTimeOffset To) Window(object query) =>
        ((DateTimeOffset)query.GetType().GetProperty("FromUtc")!.GetValue(query)!, (DateTimeOffset)query.GetType().GetProperty("ToUtc")!.GetValue(query)!);

    private static object Page(string tool, object query, bool more)
    {
        ObservationTargetRevision revision = new(3);
        CollectorRunId run = new(Run);
        CollectorHealthProjection collector = new(Target, new CollectorId("core"), 1, 1, CollectorHealthState.Current, CollectorHealthReason.None, CollectorCircuitSnapshot.Closed(Snapshot), null, 1, 0, 0, 1, Snapshot, Snapshot, Snapshot, Snapshot.AddHours(1), Snapshot);
        if (tool == "list_instances")
        {
            ObservationTarget target = new(Target, new ObservationTargetKey("first"), new ObservationTargetDisplayName("first"), new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))), ObservationTargetLifecycle.Active, revision, Snapshot, Snapshot, Snapshot);
            return new ObservationTargetPage([target], more ? new ObservationTargetListCursor(target.Key, Target) : null);
        }
        if (tool == "get_active_alerts") return new AlertActivePage([], Snapshot, more ? new AlertActiveCursor(Target, Snapshot, Run, Snapshot) : null);
        if (tool == "get_database_health") return new DatabaseHealthPage(Target, run, revision, collector, [], more ? new DatabaseHealthCursor(Target, run, revision, 1) : null, Snapshot);
        if (tool == "get_file_io") return new DatabaseFileHealthPage(Target, run, revision, collector, [], more ? new DatabaseFileHealthCursor(Target, run, revision, 1, 1) : null, Snapshot);
        (DateTimeOffset from, DateTimeOffset to) = Window(query);
        DateTimeOffset tie = from.AddMicroseconds(1);
        return tool switch
        {
            "get_metric_series" => new MetricSeriesPage(Target, "host.cpu.percent", from, to, [], "complete", revision, Snapshot, more ? new MetricSeriesCursor(Target, "host.cpu.percent", tie, Run, "{}", Snapshot, revision) : null) { HasMore = more },
            "get_job_failures" => new SqlAgentFailureSnapshot(Target, revision, run, Snapshot, OperationalObservationState.Complete, [], 0, false, from, to) { NextCursor = more ? "repository-raw-cursor" : null },
            "get_blocking_history" => new BlockingHistoryPage(Target, from, to, [], more ? new BlockingHistoryCursor(Target, from, to, tie, run, 1, BlockingBlockerKind.Session, 2, new SqlServerWaitType("LCK")) : null, Snapshot),
            "search_deadlocks" => new DeadlockPage(Target, Snapshot, [], more ? new DeadlockPageCursor(Target, tie, Run, Snapshot, from, to) : null),
            "search_diagnostic_events" => new DiagnosticEventSearchPage(Target, from, to, [], more, more ? new DiagnosticEventCursor(Target, from, to, tie, Run, Snapshot, revision) : null, revision, Snapshot),
            "get_top_queries" => new TopQueryPage([], more, Snapshot) { NextCursor = more ? QueryCursor(from, to) : null },
            "get_query_history" => new QueryHistoryPage([], more, Snapshot) { NextCursor = more ? QueryCursor(from, to) : null },
            _ => throw new InvalidOperationException(tool)
        };
    }

    private static QueryPerformanceCursorEnvelope QueryCursor(DateTimeOffset from, DateTimeOffset to) => new(Target, 1, from, to, QueryPerformanceMetric.CpuMilliseconds, Snapshot, from.AddTicks(1), new string('a', 64), 1, Run, null, new string('c', 32));
    internal static void AssertSuccess(CallToolResult result) => Assert.False(result.IsError, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(x => x.Text)));

#pragma warning disable CA1852
    public class RecordingProxy : DispatchProxy
    {
        public string Tool { get; set; } = string.Empty;
        public List<object> Requests { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object query = args!.First(x => x is not AuthorizationContext && x is not CancellationToken)!;
            Requests.Add(query);
            object page = Page(Tool, query, Requests.Count == 1);
            return typeof(RecordingProxy).GetMethod(nameof(Result), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(targetMethod!.ReturnType.GetGenericArguments()[0]).Invoke(null, [page]);
        }
        private static ValueTask<T> Result<T>(object value) => ValueTask.FromResult((T)value);
    }
#pragma warning restore CA1852

    private sealed class AuditPort : IMcpInvocationAuditPort
    {
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, Snapshot));
    }
}
