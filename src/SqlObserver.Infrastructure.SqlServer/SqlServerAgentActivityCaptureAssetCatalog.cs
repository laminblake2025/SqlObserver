using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Immutable pinned passive assets. Loading these assets does not activate collection.</summary>
public sealed class SqlServerAgentActivityCaptureAssetCatalog
{
    private const string Checksum = "a2a6ffed75e28aeb8b0ba1b1328a53007558fc55d0f45c16096f0b5de4b8962d";
    private readonly string[] queries;
    private SqlServerAgentActivityCaptureAssetCatalog(string[] queries) => this.queries = queries;

    public string GetQuery(int major) => major is >= 15 and <= 17 ? queries[major - 15] : throw new ArgumentOutOfRangeException(nameof(major));

    public static SqlServerAgentActivityCaptureAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerAgentActivityCaptureAssetCatalog).Assembly;
        var queries = new string[3];
        for (int i = 0; i < queries.Length; i++)
        {
            string resource = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"SqlObserver.Infrastructure.SqlServer.AgentActivitySourceAssets.sql-agent.activity.sqlserver{i + 15}-windows.v1.sql");
            using Stream stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("The Agent activity asset is missing.");
            if (stream.Length is <= 0 or > 65_536) throw new InvalidDataException("The Agent activity asset exceeds its bound.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            queries[i] = ValidateAsset(bytes.ToArray(), Checksum);
        }
        return new SqlServerAgentActivityCaptureAssetCatalog(queries);
    }

    internal static string ValidateAsset(byte[] bytes, string checksum)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is <= 0 or > 65_536 || bytes.Contains((byte)'\r') ||
            bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Agent activity asset is not approved UTF-8 LF.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
