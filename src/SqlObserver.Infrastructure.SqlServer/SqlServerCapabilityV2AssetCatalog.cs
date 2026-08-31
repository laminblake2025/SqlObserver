using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Separate v2 capability resource set; v1 assets remain byte immutable.</summary>
public sealed class SqlServerCapabilityV2AssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.CapabilityV2Assets.";
    private static readonly string[] Names = ["capability.connection.v2.schema.json", "capability.connection.v2.json", "capability.connection.sqlserver15-windows.v2.sql", "capability.connection.sqlserver16-windows.v2.sql", "capability.connection.sqlserver17-windows.v2.sql"];
    public string BundleChecksum { get; }
    public IReadOnlyList<string> AssetNames => assets.Keys.ToArray();
    public int ManifestVersion => assets.Count == 0 ? 2 : 2;
    private readonly IReadOnlyDictionary<string,string> assets;
    private SqlServerCapabilityV2AssetCatalog(IReadOnlyDictionary<string,string> assets, string checksum) { this.assets=assets; BundleChecksum=checksum; }
    public string Get(string name) => assets.TryGetValue(name, out string? value) ? value : throw new KeyNotFoundException("Capability v2 asset is not registered.");
    public static SqlServerCapabilityV2AssetCatalog LoadEmbedded(Assembly? assembly=null)
    {
        assembly ??= typeof(SqlServerCapabilityV2AssetCatalog).Assembly;
        using Stream checksumStream=assembly.GetManifestResourceStream(Prefix+"capability.connection.assets-v2.sha256") ?? throw new InvalidDataException("Capability v2 checksum manifest is missing.");
        using var buffer=new MemoryStream(); checksumStream.CopyTo(buffer); byte[] checksumBytes=buffer.ToArray();
        if (checksumBytes.Length < 3 || checksumBytes[0] == 0xEF || checksumBytes[0] == 0xFF || checksumBytes[0] == 0xFE || Array.IndexOf(checksumBytes, (byte)'\r') >= 0)
            throw new InvalidDataException("Capability v2 checksum manifest must be UTF-8 without BOM and use LF line endings.");
        string[] lines=Encoding.UTF8.GetString(checksumBytes).Split('\n',StringSplitOptions.RemoveEmptyEntries);
        if(lines.Length!=Names.Length) throw new InvalidDataException("Capability v2 checksum manifest has an unexpected entry count.");
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        for(int i=0;i<Names.Length;i++) { string[] p=lines[i].Split("  ",StringSplitOptions.None); if(p.Length!=2||p[1]!=Names[i]||p[0].Length!=64||p[0].Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new InvalidDataException("Capability v2 asset order or digest shape is invalid."); using Stream s=assembly.GetManifestResourceStream(Prefix+Names[i]) ?? throw new InvalidDataException("Capability v2 asset is missing."); using var b=new MemoryStream(); s.CopyTo(b); byte[] bytes=b.ToArray(); if(bytes.Length>=3 && bytes[0]==0xEF && bytes[1]==0xBB && bytes[2]==0xBF || Array.IndexOf(bytes,(byte)'\r')>=0) throw new InvalidDataException($"Capability v2 asset {Names[i]} must be UTF-8 without BOM and use LF line endings."); string actual=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); if(!actual.Equals(p[0],StringComparison.Ordinal)) throw new InvalidDataException($"Capability v2 checksum failed for {Names[i]} (actual {actual}, expected {p[0]})."); values.Add(Names[i],Encoding.UTF8.GetString(bytes)); }
        using (JsonDocument schema = JsonDocument.Parse(values[Names[0]]))
        using (JsonDocument manifest = JsonDocument.Parse(values[Names[1]]))
        {
            ValidateManifestAgainstSchema(schema.RootElement, manifest.RootElement);
        }
        return new SqlServerCapabilityV2AssetCatalog(values,Convert.ToHexString(SHA256.HashData(checksumBytes)).ToLowerInvariant());
    }

    internal static void ValidateManifestJson(string schemaJson, string manifestJson)
    {
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        using JsonDocument manifest = JsonDocument.Parse(manifestJson);
        ValidateManifestAgainstSchema(schema.RootElement, manifest.RootElement);
    }

    private static void ValidateManifestAgainstSchema(JsonElement schema, JsonElement manifest)
    {
        ValidateJsonAgainstSchema(manifest, schema, "$manifest");
    }

    private static void ValidateJsonAgainstSchema(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(value, constant))
            throw new InvalidDataException($"Capability v2 schema const mismatch at {path}.");

        if (schema.TryGetProperty("enum", out JsonElement allowed))
        {
            if (allowed.ValueKind != JsonValueKind.Array || !allowed.EnumerateArray().Any(candidate => JsonElement.DeepEquals(value, candidate)))
                throw new InvalidDataException($"Capability v2 schema enum mismatch at {path}.");
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            string? expectedType = type.GetString();
            bool valid = expectedType switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false,
            };
            if (!valid) throw new InvalidDataException($"Capability v2 schema type mismatch at {path}.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement requiredName in required.EnumerateArray())
                {
                    string name = requiredName.GetString() ?? throw new InvalidDataException($"Capability v2 schema required name is invalid at {path}.");
                    if (!value.TryGetProperty(name, out _)) throw new InvalidDataException($"Capability v2 required field is missing at {path}.{name}.");
                }
            }

            JsonElement properties = schema.TryGetProperty("properties", out JsonElement declared) ? declared : default;
            bool closed = schema.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.False;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                {
                    if (closed) throw new InvalidDataException($"Capability v2 schema rejects unknown field at {path}.{property.Name}.");
                    continue;
                }
                ValidateJsonAgainstSchema(property.Value, propertySchema, $"{path}.{property.Name}");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("uniqueItems", out JsonElement unique) && unique.ValueKind == JsonValueKind.True)
            {
                JsonElement[] items = value.EnumerateArray().ToArray();
                for (int i = 0; i < items.Length; i++)
                    for (int j = i + 1; j < items.Length; j++)
                        if (JsonElement.DeepEquals(items[i], items[j])) throw new InvalidDataException($"Capability v2 schema rejects duplicate array item at {path}.");
            }
            if (schema.TryGetProperty("items", out JsonElement itemSchema))
            {
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray()) ValidateJsonAgainstSchema(item, itemSchema, $"{path}[{index++}]");
            }
        }
    }
}
