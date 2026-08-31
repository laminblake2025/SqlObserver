using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Application.Services;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

public sealed class McpCursorSecurityTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public void SignedCursorRoundTripsAndBindsToolAndRequest()
    {
        var signer = new McpCursorSigner(Key);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["instanceId"] = JsonSerializer.SerializeToElement(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            ["limit"] = JsonSerializer.SerializeToElement(10),
        };
        using JsonDocument payload = JsonDocument.Parse("{\"position\":\"last\"}");
        string token = signer.Encode(payload.RootElement, "list_instances", arguments);
        Dictionary<string, string> decoded = signer.Decode<Dictionary<string, string>>(token, "list_instances", arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("last", decoded["position"]);
        Assert.InRange(token.Length, 1, McpCursorSigner.MaximumTokenLength);
        Assert.Throws<ArgumentException>(() => signer.Decode<Dictionary<string, string>>(token, "get_active_alerts", arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        arguments["limit"] = JsonSerializer.SerializeToElement(11);
        Assert.Throws<ArgumentException>(() => signer.Decode<Dictionary<string, string>>(token, "list_instances", arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void SignedCursorTamperingAndOversizeFailClosed()
    {
        var signer = new McpCursorSigner(Key);
        var arguments = new Dictionary<string, JsonElement>();
        using JsonDocument payload = JsonDocument.Parse("{\"position\":\"last\"}");
        string token = signer.Encode(payload.RootElement, "list_instances", arguments);
        char replacement = token[0] == 'A' ? 'B' : 'A';
        Assert.Throws<ArgumentException>(() => signer.Decode<Dictionary<string, string>>(replacement + token[1..], "list_instances", arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using JsonDocument huge = JsonDocument.Parse("{\"position\":\"" + new string('x', 2000) + "\"}");
        Assert.Throws<McpApplicationResponseOversizeException>(() => signer.Encode(huge.RootElement, "list_instances", arguments));
        Assert.Throws<ArgumentException>(() => new McpCursorSigner(new byte[31]));
    }

    [Fact]
    public void LegacyStringCursorIsAlsoSignedAndRequestBound()
    {
        var signer = new McpCursorSigner(Key);
        var arguments = new Dictionary<string, JsonElement> { ["instanceId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["limit"] = JsonSerializer.SerializeToElement(2) };
        string token = signer.Encode(JsonSerializer.SerializeToElement(new McpRawCursor("legacy-page-2")), "get_backup_status", arguments);
        McpRawCursor decoded = signer.Decode<McpRawCursor>(token, "get_backup_status", arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("legacy-page-2", decoded.Value);
        Assert.Throws<ArgumentException>(() => signer.Decode<McpRawCursor>(token, "get_job_failures", arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void ProjectionIsToolAndPathAllowlistedAndFlattensDomainValues()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var args = new Dictionary<string, JsonElement>();
        JsonNode payload = new JsonObject
        {
            ["targets"] = new JsonArray(new JsonObject
            {
                ["targetId"] = new JsonObject { ["value"] = "opaque" },
                ["displayName"] = new JsonObject { ["value"] = "safe" },
                ["alertId"] = "must-not-cross-path",
                ["connectionPolicy"] = new JsonObject { ["endpoint"] = "secret" }
            }),
            ["unexpected"] = "must-not-escape"
        };
        var result = McpResults.JsonWire(payload.AsObject(), options, "list_instances", args, new McpCursorSigner(Key));
        JsonElement data = result.StructuredContent!.Value.GetProperty("data");
        Assert.Equal("opaque", data.GetProperty("targets")[0].GetProperty("targetId").GetString());
        Assert.False(data.GetProperty("targets")[0].TryGetProperty("alertId", out _));
        Assert.False(data.TryGetProperty("unexpected", out _));
        Assert.False(data.GetProperty("targets")[0].TryGetProperty("connectionPolicy", out _));
    }
}
