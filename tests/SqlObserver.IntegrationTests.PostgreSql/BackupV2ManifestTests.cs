using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

// Schema-only checks use this project's existing real JSON Schema validator; no database is needed.
public sealed class BackupV2ManifestTests
{
    private static readonly JsonSchema Schema = LoadSchema();

    private static JsonSchema LoadSchema()
    {
        using JsonDocument schema = JsonDocument.Parse(SqlServerBackupV2AssetCatalog.LoadEmbedded().Schema);
        return JsonSchema.Build(schema.RootElement.Clone());
    }

    [Fact]
    public void PinnedManifestHasExactBoundedTwoStreamSourceAndConditionalAgPermission()
    {
        var assets = SqlServerBackupV2AssetCatalog.LoadEmbedded();
        using JsonDocument manifest = JsonDocument.Parse(assets.Manifest);
        Assert.True(Schema.Evaluate(manifest.RootElement).IsValid);
        JsonElement root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("collectorVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("outputSchemaVersion").GetInt32());
        var bounds = root.GetProperty("sourceBounds");
        Assert.Equal(512, bounds.GetProperty("maximumDatabases").GetInt32());
        Assert.Equal(5, bounds.GetProperty("maximumSamplesPerDatabase").GetInt32());
        Assert.Equal(35, bounds.GetProperty("historyLookbackDays").GetInt32());
        Assert.Equal(3072, root.GetProperty("executionBounds").GetProperty("maximumRows").GetInt32());
        Assert.Equal("server.view-any-definition", Assert.Single(root.GetProperty("conditionalPermissions")
            .GetProperty("requiredPermissionsByMajor").GetProperty("15").EnumerateArray()).GetString());
        Assert.Empty(root.GetProperty("conditionalPermissions").GetProperty("requiredPermissionsByMajor").GetProperty("16").EnumerateArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => assets.GetQuery(14));
        Assert.Throws<ArgumentOutOfRangeException>(() => assets.GetQuery(18));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("nested-extra")]
    [InlineData("missing")]
    [InlineData("unbounded")]
    [InlineData("wrong-version")]
    [InlineData("missing-ag-permission")]
    public void RealSchemaRejectsShapeAndPermissionDrift(string change)
    {
        var assets = SqlServerBackupV2AssetCatalog.LoadEmbedded();
        var manifest = JsonNode.Parse(assets.Manifest)!.AsObject();
        switch (change)
        {
            case "extra": manifest["extra"] = true; break;
            case "nested-extra": manifest["sourceBounds"]!["extra"] = true; break;
            case "missing": manifest.Remove("sourceBounds"); break;
            case "unbounded": manifest["executionBounds"]!["maximumRows"] = int.MaxValue; break;
            case "wrong-version": manifest["outputSchemaVersion"] = 1; break;
            case "missing-ag-permission": manifest["conditionalPermissions"]!["requiredPermissionsByMajor"]!["15"] = new JsonArray(); break;
        }
        using JsonDocument modified = JsonDocument.Parse(manifest.ToJsonString());
        Assert.False(Schema.Evaluate(modified.RootElement).IsValid);
    }
}
