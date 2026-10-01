using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class QueryPerformanceV3ManifestTests
{
    internal static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    internal const string ManifestDigest = "071ba07098afdf427846cd828439bcd823aa4863330829429f77801549a5ee7c";
    internal const string BundleDigest = "8a707984375b0d29ca144ded142b836c0ece121d419508601e64179169a631e8";

    [Fact]
    public void StagedManifestPinsBothSourcesAndPreservesProtectedSidecarsWithoutActivation()
    {
        byte[] manifest = File.ReadAllBytes(Path.Combine(Root, "collectors/manifests/queries.performance.v3.json"));
        byte[] index = File.ReadAllBytes(Path.Combine(Root, "collectors/manifests/m7-query-performance-v3.assets.sha256"));
        Assert.Equal(ManifestDigest, Convert.ToHexStringLower(SHA256.HashData(manifest)));
        Assert.Equal(BundleDigest, Convert.ToHexStringLower(SHA256.HashData(index)));
        string[] lines = Encoding.UTF8.GetString(index).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        foreach (string line in lines)
        {
            string[] fields = line.Split("  ", StringSplitOptions.None);
            Assert.Equal(2, fields.Length);
            string file = Path.GetFullPath(fields[1], Root);
            Assert.StartsWith(Root, file, StringComparison.OrdinalIgnoreCase);
            byte[] content = File.ReadAllBytes(file);
            Assert.DoesNotContain((byte)'\r', content);
            Assert.Equal(fields[0], Convert.ToHexStringLower(SHA256.HashData(content)));
        }
        _ = SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded();
        _ = PlanCacheRuntimeSourceBundle.LoadEmbedded();
        _ = PlanCacheDatabaseIdentitySource.LoadEmbedded();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "collectors/manifests/queries.performance.v3.schema.json")));
        JsonSchema validator = JsonSchema.Build(schema.RootElement.Clone());
        using JsonDocument document = JsonDocument.Parse(manifest);
        Assert.True(validator.Evaluate(document.RootElement).IsValid);
        Assert.False(document.RootElement.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("collectorVersion").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("outputSchemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("queryResources").GetProperty("cacheRuntime").GetProperty("queryStorePreferred").GetBoolean());
        foreach (string field in new[] { "defaultEnabled", "collectorVersion", "outputSchemaVersion", "unexpected" })
        {
            var changed = Assert.IsType<JsonObject>(JsonNode.Parse(manifest));
            changed[field] = field == "defaultEnabled" ? JsonValue.Create(true) : JsonValue.Create(1);
            Assert.False(validator.Evaluate(JsonSerializer.SerializeToElement(changed)).IsValid);
        }
    }
}
