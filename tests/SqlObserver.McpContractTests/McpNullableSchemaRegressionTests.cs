using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

public sealed class McpNullableSchemaRegressionTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly MonitoredInstanceId TargetId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ObservationTargetRevision Revision = new(3);
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BackupCoverage.NotSeenWithin35Days, false, false)]
    [InlineData(BackupCoverage.Unknown, false, false)]
    [InlineData(BackupCoverage.Complete, true, false)]
    [InlineData(BackupCoverage.Complete, false, true)]
    public void BackupWireKeepsRequiredNullableTimestampAndSatisfiesRealSchema(
        BackupCoverage coverage, bool unknownTime, bool knownTime)
    {
        CallToolResult result = MapBackup(coverage, unknownTime, knownTime);
        JsonElement structured = result.StructuredContent!.Value;

        JsonSchemaAssertions.AssertValid(structured, McpCatalog.OutputSchema("get_backup_status"), "get_backup_status");
        JsonElement timestamp = structured.GetProperty("data").GetProperty("items")[0].GetProperty("backupAtUtc");
        Assert.Equal(knownTime ? JsonValueKind.String : JsonValueKind.Null, timestamp.ValueKind);
        Assert.False(result.IsError);
        Assert.Equal(structured.GetRawText(), Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    [Fact]
    public void NoBackupRowsIsSchemaValidWithoutInventingAnObservation()
    {
        var snapshot = new BackupStatusSnapshot(TargetId, Revision, null, Now,
            OperationalObservationState.NoData, [], 0, false);
        CallToolResult result = McpResults.Json(snapshot, Options, "get_backup_status", new Dictionary<string, JsonElement>());
        JsonSchemaAssertions.AssertValid(result.StructuredContent!.Value, McpCatalog.OutputSchema("get_backup_status"), "empty backup snapshot");
        Assert.Equal(0, result.StructuredContent.Value.GetProperty("data").GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("wrong-type")]
    [InlineData("unadvertised-field")]
    public void RealSchemaRejectsMissingRequiredWrongTypesAndUnadvertisedFields(string mutation)
    {
        JsonElement schema = McpCatalog.OutputSchema("get_backup_status");
        JsonElement valid = MapBackup(BackupCoverage.Complete, false, true).StructuredContent!.Value;
        JsonSchemaAssertions.AssertValid(valid, schema, "known backup control");
        JsonObject payload = JsonNode.Parse(valid.GetRawText())!.AsObject();
        JsonObject item = payload["data"]!["items"]![0]!.AsObject();
        switch (mutation)
        {
            case "missing-required": item.Remove("backupAtUtc"); break;
            case "wrong-type": item["backupAtUtc"] = 17; break;
            case "unadvertised-field": item["unadvertised"] = "extra"; break;
        }
        Assert.False(JsonSchemaAssertions.Evaluate(JsonSerializer.SerializeToElement(payload), schema).IsValid);
    }

    private static CallToolResult MapBackup(BackupCoverage coverage, bool unknownTime, bool knownTime)
    {
        var observation = new BackupStatusObservation(TargetId, Revision, new string('a', 64), BackupKind.Full,
            knownTime ? Now : null, unknownTime ? DateTime.SpecifyKind(Now.DateTime, DateTimeKind.Unspecified) : null,
            unknownTime, null, null, null, null, coverage);
        var snapshot = new BackupStatusSnapshot(TargetId, Revision, null, Now,
            OperationalObservationState.Complete, [observation], 1, false);
        return McpResults.Json(snapshot, Options, "get_backup_status", new Dictionary<string, JsonElement>());
    }
}
