using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SqlObserver.McpContractTests;

public sealed class McpBoundaryScaffoldTests
{
    private static readonly string[] SensitiveNames = ["providerError", "connectionPolicy", "endpoint", "physicalPath", "queryText", "planXml", "jobCommand", "credentials"];
    [Fact]
    public void McpMarkerIsIsolatedInItsAdapterAssembly()
    {
        string? assemblyName = typeof(Mcp.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("SqlObserver.Mcp", assemblyName);
    }

    [Fact]
    public void CatalogContainsExactlyTheAllowlistedTools()
    {
        string[] expected =
        [
            "list_instances", "get_instance_capabilities", "get_instance_health", "get_active_alerts", "get_metric_series",
            "compare_metric_windows", "get_wait_summary", "get_active_sessions", "get_active_requests", "get_blocking_chain",
            "get_blocking_history", "get_deadlock", "search_deadlocks", "get_top_queries", "get_query_history",
            "get_query_plan_metadata", "get_database_health", "get_tempdb_health", "get_file_io", "get_storage_forecast",
            "get_backup_status", "get_job_failures", "get_availability_health", "get_incident_evidence", "search_diagnostic_events"
        ];

        Assert.Equal(expected.OrderBy(static n => n), SqlObserver.Mcp.McpCatalog.Definitions.Select(static d => d.Name).OrderBy(static n => n));
        Assert.Equal(25, SqlObserver.Mcp.McpCatalog.CreateTools().Count);
    }

    [Fact]
    public void CatalogSchemasAreStrictAndToolsAreReadOnly()
    {
        foreach (ModelContextProtocol.Server.McpServerTool tool in SqlObserver.Mcp.McpCatalog.CreateTools())
        {
            Assert.Equal(JsonValueKind.Object, tool.ProtocolTool.InputSchema.ValueKind);
            Assert.True(tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
            Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint);
            Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint);
        }
    }

    [Fact]
    public void EveryAdvertisedOutputObjectIsClosed()
    {
        foreach (ModelContextProtocol.Server.McpServerTool tool in SqlObserver.Mcp.McpCatalog.CreateTools())
            AssertClosedObjects(tool.ProtocolTool.OutputSchema!.Value, tool.ProtocolTool.Name);

        static void AssertClosedObjects(JsonElement schema, string name)
        {
            if (schema.ValueKind != JsonValueKind.Object) return;
            if (schema.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
                Assert.True(schema.TryGetProperty("additionalProperties", out JsonElement closed) && closed.ValueKind == JsonValueKind.False, name);
            foreach (JsonProperty property in schema.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object) AssertClosedObjects(property.Value, name);
                else if (property.Value.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement item in property.Value.EnumerateArray()) AssertClosedObjects(item, name);
            }
        }
    }

    [Fact]
    public void AllCatalogToolsProduceStructuredAllowlistedRepresentativePayloads()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var signer = new SqlObserver.Mcp.McpCursorSigner(Enumerable.Repeat((byte)7, 32).ToArray());
        foreach (ModelContextProtocol.Server.McpServerTool tool in SqlObserver.Mcp.McpCatalog.CreateTools())
        {
            JsonElement schema = tool.ProtocolTool.OutputSchema!.Value;
            // McpResults accepts the typed data payload; the envelope is the
            // adapter's responsibility. This also prevents a test fixture
            // from accidentally exercising a null/default projection.
            JsonNode representative = Build(schema.GetProperty("properties").GetProperty("data"));
            if (representative is JsonObject data)
            {
                data["unexpected"] = "drop";
                data["providerError"] = "drop";
            }
            var result = SqlObserver.Mcp.McpResults.JsonWire(representative.AsObject(), options, tool.ProtocolTool.Name, new Dictionary<string, JsonElement>(), signer);
            Assert.False(result.IsError, tool.ProtocolTool.Name);
            Assert.True(result.StructuredContent.HasValue, tool.ProtocolTool.Name);
            JsonElement outputData = result.StructuredContent.Value.GetProperty("data");
            Assert.Equal(JsonValueKind.Object, outputData.ValueKind);
            Assert.NotEmpty(outputData.EnumerateObject());
            AssertSchemaCompatible(outputData, schema.GetProperty("properties").GetProperty("data"), tool.ProtocolTool.Name);
            AssertNoSensitive(result.StructuredContent.Value, tool.ProtocolTool.Name);
        }

        static JsonNode Build(JsonElement schema)
        {
            if (schema.TryGetProperty("oneOf", out JsonElement alternatives)) return Build(alternatives.EnumerateArray().First());
            if (schema.TryGetProperty("type", out JsonElement type))
            {
                string selected = type.ValueKind == JsonValueKind.Array
                    ? type.EnumerateArray().Select(x => x.GetString()).First(x => x != "null")!
                    : type.GetString()!;
                return selected switch
                {
                    "object" => BuildObject(schema),
                    "array" => new JsonArray(Build(schema.GetProperty("items"))),
                    "integer" => JsonValue.Create(1)!,
                    "number" => JsonValue.Create(1.0)!,
                    "boolean" => JsonValue.Create(true)!,
                    "null" => JsonValue.Create((string?)null)!,
                    _ => JsonValue.Create("sample")!
                };
            }
            return new JsonObject();
        }

        static JsonObject BuildObject(JsonElement schema)
        {
            var result = new JsonObject();
            if (schema.TryGetProperty("properties", out JsonElement properties))
                foreach (JsonProperty property in properties.EnumerateObject()) result[property.Name] = Build(property.Value);
            return result;
        }

        static void AssertNoSensitive(JsonElement value, string tool)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    Assert.DoesNotContain(property.Name, SensitiveNames);
                    AssertNoSensitive(property.Value, tool);
                }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in value.EnumerateArray()) AssertNoSensitive(item, tool);
        }

        static void AssertSchemaCompatible(JsonElement value, JsonElement schema, string tool) =>
            JsonSchemaAssertions.AssertValid(value, schema, tool);
    }

    [Fact]
    public void ProtocolInventoryIncludesCurrentAndDownlevelRevisions()
    {
        Assert.Equal("2026-07-28", SqlObserver.Mcp.McpCatalog.CurrentProtocolVersion);
        Assert.Equal("2025-11-25", SqlObserver.Mcp.McpCatalog.DownlevelProtocolVersion);
    }

    [Theory]
    [InlineData("https://observer.example/mcp", true)]
    [InlineData("https://observer.example/other", false)]
    [InlineData("https://user:password@observer.example/mcp", false)]
    [InlineData("https://observer.example/mcp?x=1", false)]
    [InlineData("https://observer.example/mcp#fragment", false)]
    [InlineData("http://observer.example/mcp", false)]
    public void StdioEndpointIsPinnedAndCredentialFree(string value, bool expected)
    {
        Assert.Equal(expected, SqlObserver.Mcp.McpStdioBridge.TryValidateEndpoint(value, out _));
    }
}
