using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Immutable pinned passive assets. Loading these assets does not activate collection.</summary>
public sealed class SqlServerAgentActivityCaptureAssetCatalog
{
    public const string BodyChecksum = "11e3036e17e0b509eeb121a271fef21fae24e132799a135354d907b20578659a";
    public const string ServiceProjectionChecksum = "3798715b1aa9fcc71eb445e2fcd7f7fcb1b0f64b532672d07757e0fb36b9a99c";
    private const string ProjectionMarker = "/*SQLAGENT_SERVICE_PROJECTION_V2*/";
    private readonly string[] queries;
    private SqlServerAgentActivityCaptureAssetCatalog(string[] queries, string projection)
    { this.queries = queries; ServiceProjectionSql = projection; }

    /// <summary>Exact pinned SELECT used twice by every v2 capture; contains no terminating semicolon.
    /// Quoted executable tokens allow opaque arguments after space/tab. Unquoted whitespace
    /// remains unavailable rather than attempting Windows command-line resolution.</summary>
    public string ServiceProjectionSql { get; }

    public string GetQuery(int major) => major is >= 15 and <= 17 ? queries[major - 15] : throw new ArgumentOutOfRangeException(nameof(major));

    public static SqlServerAgentActivityCaptureAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerAgentActivityCaptureAssetCatalog).Assembly;
        string projection = Read("sql-agent.activity.service-projection.v2.sql", ServiceProjectionChecksum);
        var queries = new string[3];
        for (int i = 0; i < queries.Length; i++)
        {
            string name = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"sql-agent.activity.sqlserver{i + 15}-windows.v2.sql");
            string body = Read(name, BodyChecksum);
            if (body.Split(ProjectionMarker, StringSplitOptions.None).Length != 3)
                throw new InvalidDataException("The Agent service projection markers are invalid.");
            queries[i] = body.Replace(ProjectionMarker, projection, StringComparison.Ordinal);
            if (Encoding.UTF8.GetByteCount(queries[i]) > 65_536) throw new InvalidDataException("The expanded Agent activity asset exceeds its bound.");
        }
        return new SqlServerAgentActivityCaptureAssetCatalog(queries, projection);

        string Read(string name, string checksum)
        {
            using Stream stream = assembly.GetManifestResourceStream("SqlObserver.Infrastructure.SqlServer.AgentActivitySourceAssets." + name) ?? throw new InvalidDataException("The Agent activity asset is missing.");
            if (stream.Length is <= 0 or > 65_536) throw new InvalidDataException("The Agent activity asset exceeds its bound.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return ValidateAsset(bytes.ToArray(), checksum);
        }
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
