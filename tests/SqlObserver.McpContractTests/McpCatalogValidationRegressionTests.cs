using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

// Error messages are deliberate public contract expectations, not exception text.
public sealed class McpCatalogValidationRegressionTests
{
    private static readonly JsonSerializerOptions AuditSnapshotOptions = new() { MaxDepth = 64 };
    private static readonly Guid Target = Guid.Parse("a9aff392-aec9-4919-8436-d8203c79fd59");
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity([
        new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1701")], "Windows"));
    private const string Metric = "host.cpu.percent";
    private const string Fingerprint = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static readonly (string Name, string Title, string Meaning, string Properties)[] Catalog =
    [
        ("list_instances", "List monitored instances", "Call this first", "limit cursor"),
        ("get_instance_capabilities", "Get capability discovery status", "discovery status", "instanceId"),
        ("get_instance_health", "Get instance collection health", "collection freshness", "instanceId"),
        ("get_active_alerts", "List active alerts", "cannot acknowledge", "instanceId limit cursor"),
        ("get_metric_series", "Get metric samples", "available dimensions", "instanceId fromUtc toUtc limit cursor metricKey"),
        ("compare_metric_windows", "Compare metric windows", "sample-count-weighted rollup means", "instanceId metricKey leftFromUtc leftToUtc rightFromUtc rightToUtc limit"),
        ("get_wait_summary", "Get recent wait changes", "null deltas", "instanceId limit cursor"),
        ("get_active_sessions", "List sampled sessions", "latest collected activity snapshot", "instanceId limit cursor"),
        ("get_active_requests", "List sampled requests", "request IDs", "instanceId limit cursor"),
        ("get_blocking_chain", "Get current blocking chains", "root blocker", "instanceId limit cursor"),
        ("get_blocking_history", "Get blocking history", "sampled observations", "instanceId fromUtc toUtc limit cursor"),
        ("get_deadlock", "Get deadlock details", "no query text or raw deadlock XML", "instanceId eventId"),
        ("search_deadlocks", "Search deadlock events", "newest first", "instanceId fromUtc toUtc limit cursor"),
        ("get_top_queries", "Get top query observations", "opaque fingerprints", "instanceId fromUtc toUtc limit cursor metric"),
        ("get_query_history", "Get query performance history", "metric semantics, reset, coverage", "instanceId fromUtc toUtc limit cursor databaseId queryFingerprint"),
        ("get_query_plan_metadata", "Get query plan metadata", "plan metadata only", "instanceId databaseId queryFingerprint planFingerprint"),
        ("get_database_health", "List database health", "does not evaluate backup RPO", "instanceId limit cursor"),
        ("get_tempdb_health", "Get TempDB file usage", "does not provide session-level", "instanceId limit cursor"),
        ("get_file_io", "Get database file I/O counters", "cumulative total", "instanceId limit cursor"),
        ("get_storage_forecast", "Get stored metric forecasts", "does not compute a forecast", "instanceId limit cursor metricKey horizonDays"),
        ("get_backup_status", "Get backup observations", "opaque database fingerprint", "instanceId limit cursor"),
        ("get_job_failures", "Get observed Agent failures", "not the job's execution time", "instanceId fromUtc toUtc limit cursor"),
        ("get_availability_health", "Get availability group health", "Replica and database rows have different fields", "instanceId"),
        ("get_incident_evidence", "Get incident evidence", "thread ID obtained outside", "instanceId limit cursor threadId"),
        ("search_diagnostic_events", "Search diagnostic events", "not a SQL Server error-log", "instanceId fromUtc toUtc limit cursor")
    ];

    private static readonly string[] MetricKeys =
    [
        "host.cpu.percent", "host.memory.available_bytes", "host.memory.committed_bytes",
        "host.volume.free_bytes", "host.volume.queue_length", "host.volume.read_latency_ms",
        "host.volume.total_bytes", "host.volume.write_latency_ms", "replication.latency_seconds",
        "replication.pending_commands"
    ];

    public static TheoryData<string, int, string> WindowCaps => new()
    {
        { "get_metric_series", 744, "31 days" },
        { "search_deadlocks", 744, "31 days" },
        { "get_blocking_history", 24, "24 hours" },
        { "get_top_queries", 168, "7 days" },
        { "get_query_history", 168, "7 days" },
        { "get_job_failures", 168, "7 days" },
        { "search_diagnostic_events", 168, "7 days" }
    };

    [Theory]
    [InlineData(McpCatalog.CurrentProtocolVersion)]
    [InlineData(McpCatalog.DownlevelProtocolVersion)]
    public async Task OfficialSdkAdvertisesSpecificCatalogAndTheCompleteInputContract(string protocol)
    {
        var state = new State();
        await using WebApplication app = await BuildHostAsync(state);
        using HttpClient http = app.GetTestClient();
        await using var transport = Transport(http);
        await using McpClient client = await McpClient.CreateAsync(transport,
            new McpClientOptions { ProtocolVersion = protocol, InitializationTimeout = TimeSpan.FromSeconds(5) });
        IList<McpClientTool> tools = await client.ListToolsAsync();

        Assert.Equal(protocol, client.NegotiatedProtocolVersion);
        Assert.Equal(Catalog.Select(x => x.Name).Order(StringComparer.Ordinal), tools.Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(25, tools.Select(x => x.Description).Distinct(StringComparer.Ordinal).Count());
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string title, string meaning, string properties) in Catalog)
        {
            Tool actual = Assert.Single(tools, x => x.Name == name).ProtocolTool;
            // SDK 2.2 supports both the top-level title and annotation title.
            string? actualTitle = actual.Title ?? actual.Annotations?.Title;
            Assert.Equal(title, actualTitle);
            Assert.True(titles.Add(actualTitle!));
            Assert.Contains(meaning, actual.Description!, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("Read-only SQL Observer diagnostic projection.", actual.Description);
            Assert.True(actual.Annotations?.ReadOnlyHint);
            Assert.False(actual.Annotations?.DestructiveHint);
            JsonElement schema = actual.InputSchema;
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            JsonElement advertised = schema.GetProperty("properties");
            Assert.Equal(properties.Split(' ').Order(StringComparer.Ordinal), advertised.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
            foreach (JsonProperty property in advertised.EnumerateObject())
            {
                string? description = property.Value.GetProperty("description").GetString();
                Assert.False(string.IsNullOrWhiteSpace(description), name + "." + property.Name);
                Assert.NotEqual(property.Name, description);
            }
            if (advertised.TryGetProperty("limit", out JsonElement limit))
            {
                int expected = name switch
                {
                    "get_metric_series" or "compare_metric_windows" => 1000,
                    "search_deadlocks" or "get_incident_evidence" => 256,
                    "get_top_queries" or "get_query_history" or "get_storage_forecast" => 200,
                    _ => 100
                };
                Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
                Assert.Equal(expected, limit.GetProperty("maximum").GetInt32());
                Assert.Equal(expected, limit.GetProperty("default").GetInt32());
            }
            if (advertised.TryGetProperty("metricKey", out JsonElement metric))
                Assert.Equal(MetricKeys, metric.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray());
            if (advertised.TryGetProperty("fromUtc", out JsonElement from))
            {
                Assert.Contains("ending in Z", from.GetProperty("description").GetString()!, StringComparison.Ordinal);
                Assert.False(from.TryGetProperty("default", out _));
                Assert.False(advertised.GetProperty("toUtc").TryGetProperty("default", out _));
            }
        }
        Assert.Equal(MetricKeys, MetricCatalogV1.All.Where(x => x.Enabled).Select(x => x.Key).Order(StringComparer.Ordinal));
        JsonElement forecast = Assert.Single(tools, x => x.Name == "get_storage_forecast").ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal(30, forecast.GetProperty("horizonDays").GetProperty("default").GetInt32());
        Assert.Contains("empty dimension set", forecast.GetProperty("metricKey").GetProperty("description").GetString()!, StringComparison.Ordinal);
        foreach (string instruction in new[] { "Call list_instances first", "ending in Z", "24 hours", "7 days", "31 days", "originally omitted", "first-observed time" })
            Assert.Contains(instruction, client.ServerInstructions!, StringComparison.Ordinal);
        Assert.Empty(state.Calls);
        Assert.Empty(state.Audits);
    }

    [Theory]
    [MemberData(nameof(WindowCaps))]
    public async Task ExactToolWindowCapDispatchesUnchangedAndAuditsSuccess(string tool, int hours, string _)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments(tool);
        Window(args, Start, Start.AddHours(hours));
        CallToolResult result = await fixture.CallAsync(tool, args);
        AssertSuccess(result, fixture.State);
        object request = Assert.Single(fixture.State.Calls).Arguments.First(x => x is not AuthorizationContext && x is not CancellationToken)!;
        Assert.Equal(Start, request.GetType().GetProperty("FromUtc")!.GetValue(request));
        Assert.Equal(Start.AddHours(hours), request.GetType().GetProperty("ToUtc")!.GetValue(request));
    }

    [Theory]
    [MemberData(nameof(WindowCaps))]
    public async Task ToolWindowCapPlusOneMicrosecondIsRejectedBeforeApplicationDispatch(string tool, int hours, string label)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments(tool);
        Window(args, Start, Start.AddHours(hours).AddMicroseconds(1));
        AssertInvalid(await fixture.CallAsync(tool, args), fixture.State, $"fromUtc and toUtc must span no more than {label}.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task EmptyOrReversedWindowNamesBothEndpoints(int minutes)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("get_metric_series");
        Window(args, Start, Start.AddMinutes(minutes));
        AssertInvalid(await fixture.CallAsync("get_metric_series", args), fixture.State, "toUtc must be later than fromUtc.");
    }

    [Theory]
    [InlineData("2026-08-01T00:00:00+00:00")]
    [InlineData("2026-08-01T00:00:00")]
    [InlineData("2026-08-01T01:00:00+01:00")]
    [InlineData("08/01/2026 00:00:00Z")]
    [InlineData("2026-08-01T00:00:00Z trailing-secret")]
    [InlineData("2026-08-01T00:00:00.Z")]
    [InlineData("not-a-date-private-detail")]
    public async Task PublicTimestampRequiresInvariantIsoUtcEndingInZ(string timestamp)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("get_metric_series");
        Put(args, "fromUtc", timestamp);
        AssertInvalid(await fixture.CallAsync("get_metric_series", args), fixture.State, "fromUtc must be an ISO 8601 UTC timestamp ending in Z.");
    }

    [Theory]
    [InlineData("2026-08-01T00:00:00Z")]
    [InlineData("2026-08-01T00:00:00.123Z")]
    [InlineData("2026-08-01T00:00:00.123456Z")]
    public async Task PublicUtcTimestampAcceptsNormalFractionalPrecision(string timestamp)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("get_metric_series");
        Put(args, "fromUtc", timestamp);
        AssertSuccess(await fixture.CallAsync("get_metric_series", args), fixture.State);
    }

    public static TheoryData<string, string, string?, string> InvalidScalars => new()
    {
        { "get_instance_health", "instanceId", null, "instanceId is required." },
        { "get_instance_health", "instanceId", "null", "instanceId is required." },
        { "get_instance_health", "instanceId", "42", "instanceId must be a string." },
        { "get_instance_health", "instanceId", "\"00000000-0000-0000-0000-000000000000\"", "instanceId must be a non-empty UUID." },
        { "get_instance_health", "instanceId", "\"private-invalid-uuid\"", "instanceId must be a non-empty UUID." },
        { "get_deadlock", "eventId", "\"private-invalid-event\"", "eventId must be a non-empty UUID." },
        { "get_incident_evidence", "threadId", "false", "threadId must be a string." },
        { "list_instances", "limit", "1.5", "limit must be an integer from 1 to 100." },
        { "list_instances", "limit", "2147483648", "limit must be an integer from 1 to 100." },
        { "list_instances", "limit", "\"1\"", "limit must be an integer from 1 to 100." },
        { "list_instances", "limit", "null", "limit must be an integer from 1 to 100." },
        { "list_instances", "limit", "0", "limit must be an integer from 1 to 100." },
        { "list_instances", "limit", "101", "limit must be an integer from 1 to 100." },
        { "get_top_queries", "limit", "201", "limit must be an integer from 1 to 200." },
        { "get_metric_series", "metricKey", "\"engine.private-provider-detail\"", "metricKey must be one of the advertised metric keys." },
        { "get_storage_forecast", "metricKey", "\"HOST.CPU.PERCENT\"", "metricKey must be one of the advertised metric keys." },
        { "get_top_queries", "metric", "\"private-unlisted-metric\"", "metric must be one of: cpuMilliseconds, durationMilliseconds, executions, logicalReads, writes, rows." },
        { "get_query_history", "databaseId", "32768", "databaseId must be an integer from 1 to 32767." },
        { "get_query_history", "queryFingerprint", "\"private-invalid-fingerprint\"", "queryFingerprint must contain exactly 64 hexadecimal characters." },
        { "get_query_plan_metadata", "planFingerprint", "\"private-invalid-plan\"", "planFingerprint must contain exactly 64 hexadecimal characters." },
        { "get_storage_forecast", "horizonDays", "367", "horizonDays must be an integer from 1 to 366." },
        { "get_metric_series", "cursor", "\"private_invalid_cursor\"", "cursor is invalid or does not match this tool and its original arguments." }
    };

    [Theory]
    [MemberData(nameof(InvalidScalars))]
    public async Task InvalidScalarNamesOnlyItsControlledFieldAndIsAuditedWithoutDispatch(string tool, string field, string? json, string message)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments(tool);
        if (json is null) args.Remove(field);
        else { using JsonDocument document = JsonDocument.Parse(json); args[field] = document.RootElement.Clone(); }
        AssertInvalid(await fixture.CallAsync(tool, args), fixture.State, message);
    }

    [Fact]
    public async Task UnsupportedArgumentNeverEchoesAnAttackerControlledNameOrValue()
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("get_instance_health");
        Put(args, "private-header\r\nforged: secret", "private-provider-value");
        AssertInvalid(await fixture.CallAsync("get_instance_health", args), fixture.State, "The request contains an unsupported argument.");
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(65537)]
    public async Task RequestByteLimitIsInclusiveAndInvalidInputRetainsItsAuditDigest(int requestBytes)
    {
        using var fixture = new HandlerFixture();
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["cursor"] = JsonSerializer.SerializeToElement(string.Empty) };
        int overhead = JsonSerializer.SerializeToUtf8Bytes(args).Length;
        Put(args, "cursor", new string('a', requestBytes - overhead));
        Assert.Equal(requestBytes, JsonSerializer.SerializeToUtf8Bytes(args).Length);
        JsonElement expected = JsonSerializer.SerializeToElement(args);
        AssertInvalid(await fixture.CallAsync("list_instances", args), fixture.State, requestBytes > 65536
            ? "The request exceeds the 65536-byte limit."
            : "cursor is invalid or does not match this tool and its original arguments.");
        Assert.Equal(McpParameterCanonicalizer.Digest(expected).ToString(), Assert.Single(fixture.State.Audits).ParameterDigest.ToString());
    }

    [Theory]
    [InlineData(31, "", "instanceId must be a string.")]
    [InlineData(32, "", "The request exceeds the maximum nesting depth of 32.")]
    [InlineData(33, "0", "The request exceeds the maximum nesting depth of 32.")]
    public async Task NestingLimitIncludesTheArgumentObjectAndKeepsTheExactAuditDigest(int arrays, string inner, string message)
    {
        using var fixture = new HandlerFixture();
        using JsonDocument nested = JsonDocument.Parse(new string('[', arrays) + inner + new string(']', arrays), new JsonDocumentOptions { MaxDepth = 64 });
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["instanceId"] = nested.RootElement.Clone() };
        // The expected digest represents exactly the bounded malformed input;
        // it must not silently become an empty dictionary or an unrelated hash.
        JsonElement expected = JsonSerializer.SerializeToElement(args, AuditSnapshotOptions);
        AssertInvalid(await fixture.CallAsync("get_instance_health", args), fixture.State, message);
        Assert.Equal(McpParameterCanonicalizer.Digest(expected).ToString(), Assert.Single(fixture.State.Audits).ParameterDigest.ToString());
    }

    public static TheoryData<string> EnabledMetrics => new(MetricKeys);

    [Theory]
    [MemberData(nameof(EnabledMetrics))]
    public async Task EachAdvertisedMetricDispatchesWithoutChangingItsKey(string metric)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("get_metric_series");
        Put(args, "metricKey", metric);
        AssertSuccess(await fixture.CallAsync("get_metric_series", args), fixture.State);
        object request = Assert.Single(fixture.State.Calls).Arguments.First(x => x is not AuthorizationContext && x is not CancellationToken)!;
        Assert.Equal(metric, request.GetType().GetProperty("MetricKey")!.GetValue(request));
    }

    [Fact]
    public async Task ComparisonRejectsThePreviouslyAdvertisedButUnusedGenericWindow()
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("compare_metric_windows");
        Window(args, Start, Start.AddHours(1));
        AssertInvalid(await fixture.CallAsync("compare_metric_windows", args), fixture.State, "The request contains an unsupported argument.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdjacentComparisonWindowsAtTheCapDispatchWithoutSyntheticWindowArguments(bool reversedOrder)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("compare_metric_windows");
        DateTimeOffset leftFrom = reversedOrder ? Start.AddDays(31) : Start;
        DateTimeOffset rightFrom = reversedOrder ? Start : Start.AddDays(31);
        Comparison(args, leftFrom, leftFrom.AddDays(31), rightFrom, rightFrom.AddDays(31));
        AssertSuccess(await fixture.CallAsync("compare_metric_windows", args), fixture.State);
        RecordedCall call = Assert.Single(fixture.State.Calls);
        Assert.Equal("CompareAsync", call.Method);
        Assert.Equal(new[] { leftFrom, leftFrom.AddDays(31), rightFrom, rightFrom.AddDays(31) }, call.Arguments.OfType<DateTimeOffset>());
    }

    [Theory]
    [InlineData("unequal")]
    [InlineData("overlap")]
    [InlineData("identical")]
    public async Task ComparisonRejectsUnequalOrOverlappingWindowsBeforeDispatch(string variant)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("compare_metric_windows");
        DateTimeOffset rightFrom = variant == "overlap" ? Start.AddMinutes(30) : variant == "identical" ? Start : Start.AddHours(2);
        Comparison(args, Start, Start.AddHours(1), rightFrom, rightFrom.AddHours(variant == "unequal" ? 2 : 1));
        AssertInvalid(await fixture.CallAsync("compare_metric_windows", args), fixture.State,
            "leftFromUtc/leftToUtc and rightFromUtc/rightToUtc must have equal durations and must not overlap.");
    }

    [Theory]
    [InlineData("left", "empty")]
    [InlineData("left", "reversed")]
    [InlineData("left", "overcap")]
    [InlineData("right", "empty")]
    [InlineData("right", "reversed")]
    [InlineData("right", "overcap")]
    public async Task ComparisonNamesTheInvalidWindow(string side, string variant)
    {
        using var fixture = new HandlerFixture();
        Dictionary<string, JsonElement> args = Arguments("compare_metric_windows");
        DateTimeOffset from = side == "left" ? Start : Start.AddHours(2);
        Put(args, side + "ToUtc", Utc(variant switch { "empty" => from, "reversed" => from.AddTicks(-10), _ => from.AddDays(31).AddMicroseconds(1) }));
        AssertInvalid(await fixture.CallAsync("compare_metric_windows", args), fixture.State,
            $"{side}FromUtc and {side}ToUtc must define a positive window of no more than 31 days.");
    }

    [Fact]
    public async Task DownstreamArgumentExceptionsRemainGenericAndDoNotExposeProviderDetails()
    {
        using var fixture = new HandlerFixture();
        fixture.State.Failure = new ArgumentException("private-password=secret; provider-path=C:\\private");
        CallToolResult result = await fixture.CallAsync("get_instance_health", Arguments("get_instance_health"));
        AssertInvalid(result, fixture.State, "The request is invalid.", dispatched: true);
    }

    [Fact]
    public async Task OfficialSdkInvalidCallHasOneTerminalAuditAndNoApplicationDispatch()
    {
        var state = new State();
        await using WebApplication app = await BuildHostAsync(state);
        using HttpClient http = app.GetTestClient();
        await using var transport = Transport(http);
        await using McpClient client = await McpClient.CreateAsync(transport);
        Dictionary<string, JsonElement> args = Arguments("search_diagnostic_events");
        Window(args, Start, Start.AddDays(7).AddMicroseconds(1));
        CallToolResult result = await client.CallToolAsync(new CallToolRequestParams { Name = "search_diagnostic_events", Arguments = args });
        AssertInvalid(result, state, "fromUtc and toUtc must span no more than 7 days.");
    }

    private static void AssertInvalid(CallToolResult result, State state, string message, bool dispatched = false)
    {
        Assert.True(result.IsError);
        using JsonDocument text = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal("invalid_request", text.RootElement.GetProperty("code").GetString());
        Assert.Equal(message, text.RootElement.GetProperty("message").GetString());
        McpInvocationAuditRecord audit = Assert.Single(state.Audits);
        Assert.Equal(McpInvocationOutcome.Invalid, audit.Outcome);
        Assert.Equal(McpInvocationAuditReason.InvalidRequest, audit.Reason);
        Assert.Equal(dispatched ? 1 : 0, state.Calls.Count);
    }

    private static void AssertSuccess(CallToolResult result, State state)
    {
        Assert.False(result.IsError, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(x => x.Text)));
        Assert.Single(state.Calls);
        McpInvocationAuditRecord audit = Assert.Single(state.Audits);
        Assert.Equal(McpInvocationOutcome.Succeeded, audit.Outcome);
        Assert.Equal(McpInvocationAuditReason.Completed, audit.Reason);
    }

    private static Dictionary<string, JsonElement> Arguments(string tool)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (tool != "list_instances") Put(args, "instanceId", Target);
        if (WindowCaps.Any(row => (string)row[0] == tool)) Window(args, Start, Start.AddHours(1));
        if (tool is "get_metric_series" or "get_storage_forecast" or "compare_metric_windows") Put(args, "metricKey", Metric);
        if (tool == "get_top_queries") Put(args, "metric", "cpuMilliseconds");
        if (tool is "get_query_history" or "get_query_plan_metadata") { Put(args, "databaseId", 1); Put(args, "queryFingerprint", Fingerprint); }
        if (tool == "get_query_plan_metadata") Put(args, "planFingerprint", Fingerprint.ToUpperInvariant());
        if (tool == "get_deadlock") Put(args, "eventId", Guid.Parse("392f0d48-935f-4474-af53-17fb2cd9a778"));
        if (tool == "get_incident_evidence") Put(args, "threadId", Guid.Parse("4f984ed7-c0b8-4d4a-a37e-f0730622aac9"));
        if (tool == "compare_metric_windows") Comparison(args, Start, Start.AddHours(1), Start.AddHours(2), Start.AddHours(3));
        return args;
    }

    private static void Put(Dictionary<string, JsonElement> args, string key, object? value) => args[key] = JsonSerializer.SerializeToElement(value);
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static void Window(Dictionary<string, JsonElement> args, DateTimeOffset from, DateTimeOffset to) { Put(args, "fromUtc", Utc(from)); Put(args, "toUtc", Utc(to)); }
    private static void Comparison(Dictionary<string, JsonElement> args, DateTimeOffset leftFrom, DateTimeOffset leftTo, DateTimeOffset rightFrom, DateTimeOffset rightTo)
    {
        Put(args, "leftFromUtc", Utc(leftFrom)); Put(args, "leftToUtc", Utc(leftTo));
        Put(args, "rightFromUtc", Utc(rightFrom)); Put(args, "rightToUtc", Utc(rightTo));
    }

    private static HttpClientTransport Transport(HttpClient http) => new(new HttpClientTransportOptions
    {
        Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
        EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(5)
    }, http, ownsHttpClient: false);

    private static async Task<WebApplication> BuildHostAsync(State state)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSqlObserverMcp(builder.Configuration);
        AddServices(builder.Services, state);
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<McpHttpAuditBoundaryMiddleware>();
        app.UseAuthorization();
        app.MapSqlObserverMcp().RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static void AddServices(IServiceCollection services, State state)
    {
        services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1701"), [ApplicationRole.Viewer], true)]));
        services.AddSingleton(new McpCursorSigner(Enumerable.Repeat((byte)17, 32).ToArray()));
        services.AddSingleton<IMcpInvocationAuditPort>(new AuditPort(state));
        Type[] interfaces = [typeof(IObservationTargetQueryService), typeof(IObservationTargetStatusQueryService),
            typeof(IHealthProjectionQueryService), typeof(IAlertQueryService), typeof(IActivityProjectionQueryService),
            typeof(IDeadlockProjectionQueryService), typeof(IMetricSeriesQueryService), typeof(IStorageForecastQueryService),
            typeof(IDiagnosticEventQueryService), typeof(IIncidentEvidenceQueryService), typeof(IAnalyticsQueryService),
            typeof(IQueryPerformanceApiQueryService), typeof(IOperationalHealthQueryService)];
        foreach (Type serviceType in interfaces)
        {
            object service = DispatchProxy.Create(serviceType, typeof(RecordingProxy));
            ((RecordingProxy)service).State = state;
            services.AddSingleton(serviceType, service);
        }
    }

    private sealed class HandlerFixture : IDisposable
    {
        private readonly ServiceProvider provider;
        internal State State { get; } = new();
        internal HandlerFixture() { var services = new ServiceCollection(); AddServices(services, State); provider = services.BuildServiceProvider(); }
        internal ValueTask<CallToolResult> CallAsync(string tool, Dictionary<string, JsonElement> args) => new McpCallHandler(provider).ExecuteAsync(tool, args, User, CancellationToken.None);
        public void Dispose() => provider.Dispose();
    }

    public sealed record RecordedCall(string Method, object?[] Arguments);
    public sealed class State
    {
        public ConcurrentQueue<RecordedCall> Calls { get; } = [];
        public ConcurrentQueue<McpInvocationAuditRecord> Audits { get; } = [];
        public Exception? Failure { get; set; }
    }

#pragma warning disable CA1852
    public class RecordingProxy : DispatchProxy
    {
        public State State { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            State.Calls.Enqueue(new RecordedCall(targetMethod!.Name, args ?? []));
            if (State.Failure is not null) throw State.Failure;
            Type resultType = targetMethod.ReturnType.GetGenericArguments()[0];
            return typeof(RecordingProxy).GetMethod(nameof(EmptyResult), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(resultType).Invoke(null, null);
        }
        private static ValueTask<T> EmptyResult<T>() => ValueTask.FromResult(default(T)!);
    }
#pragma warning restore CA1852

    private sealed class AuditPort(State state) : IMcpInvocationAuditPort
    {
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        {
            state.Audits.Enqueue(request.Record);
            return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, Start));
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(User, Scheme.Name)));
    }
}
