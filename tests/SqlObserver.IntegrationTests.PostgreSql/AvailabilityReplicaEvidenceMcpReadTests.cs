using System.Text.Json;
using Json.Schema;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class AvailabilityReplicaEvidenceCommitTests
{
    private static async Task AssertV2McpPagesAsync(RepositoryTestDatabase database, Guid target,
        DateTimeOffset cutoff, Guid run)
    {
        string connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c role=sqlobserver_server",
        }.ConnectionString;
        int calls = 0;
        foreach (bool signed in new[] { true, false })
        {
            await using var host = new McpProductionPipelineFactory(connectionString, signed);
            using HttpClient http = host.CreateClient();
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            }, http, ownsHttpClient: false);
            await using McpClient client = await McpClient.CreateAsync(transport);
            IList<McpClientTool> tools = await client.ListToolsAsync();
            foreach (string name in new[] { "get_availability_replicas_v2", "get_availability_databases_v2" })
            {
                McpClientTool tool = Assert.Single(tools, item => item.Name == name);
                JsonElement schema = Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema);
                string? cursor = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool unknown = false, zero = false;
                for (int page = 0; page < (signed ? 2 : 1); page++)
                {
                    var args = new Dictionary<string, JsonElement>
                    {
                        ["instanceId"] = JsonSerializer.SerializeToElement(target), ["limit"] = JsonSerializer.SerializeToElement(1),
                        ["atUtc"] = JsonSerializer.SerializeToElement(cutoff.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                            System.Globalization.CultureInfo.InvariantCulture)),
                    };
                    if (cursor is not null) args["cursor"] = JsonSerializer.SerializeToElement(cursor);
                    CallToolResult result = await client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = args });
                    calls++;
                    Assert.False(result.IsError, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(item => item.Text)));
                    JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
                    EvaluationResults validation = JsonSchema.Build(schema).Evaluate(structured,
                        new EvaluationOptions { OutputFormat = OutputFormat.List });
                    Assert.True(validation.IsValid, JsonSerializer.Serialize(validation));
                    JsonElement data = structured.GetProperty("data");
                    Assert.Equal(run, data.GetProperty("runId").GetGuid());
                    Assert.Equal(cutoff, data.GetProperty("atUtc").GetDateTimeOffset());
                    JsonElement item = Assert.Single(data.GetProperty("items").EnumerateArray());
                    Assert.True(seen.Add(item.GetProperty("replicaFingerprint").GetString()!));
                    if (name == "get_availability_databases_v2")
                    {
                        JsonElement queue = item.GetProperty("logSendQueueKb");
                        unknown |= queue.ValueKind == JsonValueKind.Null;
                        zero |= queue.ValueKind == JsonValueKind.Number && queue.GetInt64() == 0;
                        if (item.GetProperty("sourceLastCommitLocal").ValueKind == JsonValueKind.String)
                            Assert.Equal("2026-09-29T08:15:00.000001", item.GetProperty("sourceLastCommitLocal").GetString());
                    }
                    Assert.Equal(page == 0, data.GetProperty("hasMore").GetBoolean());
                    cursor = data.TryGetProperty("nextCursor", out JsonElement next) ? next.GetString() : null;
                    Assert.Equal(signed && page == 0, cursor is not null);
                    await using var audit = database.DataSource.CreateCommand("""
                        SELECT count(*) FROM audit.mcp_invocation
                        WHERE tool_name IN ('get_availability_replicas_v2','get_availability_databases_v2');
                        """);
                    Assert.Equal((long)calls, await audit.ExecuteScalarAsync());
                }
                Assert.Equal(signed ? 2 : 1, seen.Count);
                if (signed && name == "get_availability_databases_v2") { Assert.True(unknown); Assert.True(zero); }
            }
        }
        Assert.Equal(6, calls);
    }
}
