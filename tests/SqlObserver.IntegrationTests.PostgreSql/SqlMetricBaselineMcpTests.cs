using System.Globalization;
using System.Text.Json;
using Json.Schema;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Domain.Analytics;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class SqlMetricAnalyticsSourceTests
{
    [Fact]
    public async Task OfficialMcpClientReadsPublishedSqlBaselineAndAuditsMixedScopeDenialWithoutCursorSigner()
    {
        await using RepositoryTestDatabase database = await Database(); Guid target = await Target(database), other = await Target(database);
        await SeedBaselineHours(database, target); await SqlBaselineJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var writer = RollupRepository(collector);
        var lease = await RollupLease(collector); var job = Assert.Single(await writer.ClaimAsync(lease, 1, default));
        await writer.StoreSqlBaselinesAsync(RollupRequest(job, lease),
            SqlMetricBaselineV2.Compute(await writer.ReadSqlBaselineInputsAsync(job, default), BatchMetric, job.ToUtc, job.SourceCutoffUtc), default);
        string serverConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        await using var factory = new McpProductionPipelineFactory(serverConnection, false, target, other);
        using HttpClient http = factory.CreateClient();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(5),
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport); var tools = await client.ListToolsAsync();
        const string baselineTool = "get_sql_metric_baselines_v2", catalogTool = "list_sql_metric_catalog_v2";
        var args = new Dictionary<string, JsonElement>
        {
            ["instanceId"] = JsonSerializer.SerializeToElement(target), ["targetRevision"] = JsonSerializer.SerializeToElement(1),
            ["metricKey"] = JsonSerializer.SerializeToElement(BatchMetric),
            ["fromUtc"] = JsonSerializer.SerializeToElement(Utc(job.FromUtc)), ["toUtc"] = JsonSerializer.SerializeToElement(Utc(job.ToUtc)),
        };
        JsonElement catalog = await Read(catalogTool, []);
        Assert.Equal(MetricCatalogV2.Checksum, catalog.GetProperty("catalogChecksum").GetString());
        Assert.Equal(7, catalog.GetProperty("items").GetArrayLength());
        JsonElement baseline = await Read(baselineTool, args);
        Assert.Equal("complete", baseline.GetProperty("state").GetString());
        Assert.Equal(168, baseline.GetProperty("items").GetArrayLength());
        Assert.Equal(Enumerable.Range(0, 168), baseline.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("hourOfWeek").GetInt32()));
        Assert.All(baseline.GetProperty("items").EnumerateArray(), r => Assert.Equal(32.5, r.GetProperty("median").GetDouble()));
        Assert.False(baseline.TryGetProperty("nextCursor", out _));
        args["snapshotUtc"] = JsonSerializer.SerializeToElement(Utc(job.ToUtc));
        Assert.Equal("no_data", (await Read(baselineTool, args)).GetProperty("state").GetString());
        args.Remove("snapshotUtc"); args["generation"] = JsonSerializer.SerializeToElement(2);
        Assert.Equal("no_data", (await Read(baselineTool, args)).GetProperty("state").GetString());
        args.Remove("generation"); args["instanceId"] = JsonSerializer.SerializeToElement(other);
        CallToolResult denied = await client.CallToolAsync(new CallToolRequestParams { Name = baselineTool, Arguments = args });
        Assert.True(denied.IsError); Assert.Null(denied.StructuredContent);
        using (var error = JsonDocument.Parse(Assert.Single(denied.Content.OfType<TextContentBlock>()).Text))
            Assert.Equal("forbidden", error.RootElement.GetProperty("code").GetString());
        await using var audit = database.DataSource.CreateCommand("""
            SELECT count(*), count(*) FILTER(WHERE tool_name='get_sql_metric_baselines_v2' AND outcome='denied' AND target_id=@other),
                count(*) FILTER(WHERE tool_name='list_sql_metric_catalog_v2' AND target_id IS NULL)
            FROM audit.mcp_invocation;
            """);
        audit.Parameters.AddWithValue("other", other); await using var rows = await audit.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync()); Assert.Equal(5L, rows.GetInt64(0)); Assert.Equal(1L, rows.GetInt64(1)); Assert.Equal(1L, rows.GetInt64(2));

        async Task<JsonElement> Read(string tool, Dictionary<string, JsonElement> arguments)
        {
            CallToolResult result = await client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = arguments });
            Assert.False(result.IsError, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
            JsonElement content = Assert.IsType<JsonElement>(result.StructuredContent);
            Assert.True(JsonSchema.Build(Assert.IsType<JsonElement>(Assert.Single(tools, t => t.Name == tool).ProtocolTool.OutputSchema)).Evaluate(content).IsValid);
            return content.GetProperty("data");
        }
        static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }
}
