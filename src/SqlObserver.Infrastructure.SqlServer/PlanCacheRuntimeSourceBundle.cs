using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged, checksum-pinned native statement counters for the cache delta cutover.</summary>
internal sealed class PlanCacheRuntimeSourceBundle
{
    internal const string ExpectedBundleChecksum = "c30d5b67f02984a8860ba288de4c75d5060ccf8a19ca13b2925320b4d2369a31";
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.PlanCacheRuntimeSourceAssets.";
    private readonly IReadOnlyDictionary<int, string> sql;

    private PlanCacheRuntimeSourceBundle(IReadOnlyDictionary<int, string> sql) => this.sql = sql;

    internal string GetSql(int major) => sql.TryGetValue(major, out string? query) ? query :
        throw new ArgumentOutOfRangeException(nameof(major), "The cache source SQL version is not supported.");

    internal static PlanCacheRuntimeSourceBundle LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(PlanCacheRuntimeSourceBundle).Assembly;
        byte[] index = Read(assembly, "assets.sha256");
        if (!Convert.ToHexString(SHA256.HashData(index)).Equals(ExpectedBundleChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cache source bundle digest is not approved.");
        string[] lines = Encoding.UTF8.GetString(index).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 3) throw new InvalidDataException("Cache source asset count is not approved.");
        var queries = new Dictionary<int, string>();
        for (int offset = 0; offset < 3; offset++)
        {
            int major = 15 + offset;
            string name = $"read.sqlserver{major}-windows.v1.sql";
            string[] fields = lines[offset].Split("  ", StringSplitOptions.None);
            byte[] bytes = Read(assembly, name);
            if (fields.Length != 2 || fields[1] != name ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(fields[0], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cache source SQL checksum is invalid.");
            queries.Add(major, new UTF8Encoding(false, true).GetString(bytes));
        }
        return new(queries);
    }

    internal async Task<PlanCacheRuntimeSourcePage> ReadAsync(SqlConnection connection,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        int maximumRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        cancellationToken.ThrowIfCancellationRequested();
        if (connection.State != ConnectionState.Open || maximumRows is < 1 or > PlanCacheRuntimeSourceReader.MaximumRows)
            throw new ArgumentException("Cache source requires an open connection and bounded candidate limit.");
        int major = int.Parse(connection.ServerVersion.Split('.')[0], CultureInfo.InvariantCulture);
        await using var command = new SqlCommand(GetSql(major), connection) { CommandTimeout = 20 };
        command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value = maximumRows;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,
            cancellationToken).ConfigureAwait(false);
        return await PlanCacheRuntimeSourceReader.ReadAsync(reader, targetId, targetRevision,
            maximumRows, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ??
            throw new InvalidDataException("Cache source resource is missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
}
