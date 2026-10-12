using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Checksum-pinned passive lifecycle source. Native evidence qualifies only exercised branches.</summary>
public sealed class SqlServerLifecycleAssetCatalog
{
    private const string Resource = "SqlObserver.Infrastructure.SqlServer.LifecycleSourceAssets.server.lifecycle.sqlserver15-17-windows.v1.sql";
    private const string ExpectedChecksum = "ff1ac5e01a9cd64f5be0302ac4a5f2195418e3dc8cfbf6f0ae7097d16a6eaa45";
    private readonly string query;

    private SqlServerLifecycleAssetCatalog(string query) => this.query = query;

    public string GetQuery(int major) => major is >= 15 and <= 17
        ? query : throw new ArgumentOutOfRangeException(nameof(major));

    public static SqlServerLifecycleAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerLifecycleAssetCatalog).Assembly;
        using Stream source = assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidDataException("The lifecycle source asset is missing.");
        if (source.Length is <= 0 or > 65_536)
            throw new InvalidDataException("The lifecycle source asset exceeds its bound.");
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        byte[] value = bytes.ToArray();
        if (value.Contains((byte)'\r') || value.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            !Convert.ToHexString(SHA256.HashData(value)).Equals(ExpectedChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The lifecycle source asset is not approved UTF-8 LF.");
        return new SqlServerLifecycleAssetCatalog(new UTF8Encoding(false, true).GetString(value));
    }
}
