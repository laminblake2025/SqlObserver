using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged, bounded protected cache content; not registered in production.</summary>
internal sealed class PlanCacheContentSourceBundle
{
    internal const string ExpectedBundleChecksum = "82409fe67c91f9e9b6ffc31b31516306f494b1a0328773bfb6e4133cdc302cd8";
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.PlanCacheContentSourceAssets.";
    private readonly IReadOnlyDictionary<int, string> sql;

    private PlanCacheContentSourceBundle(IReadOnlyDictionary<int, string> sql) => this.sql = sql;

    internal string GetSql(int major) => sql.TryGetValue(major, out string? query) ? query :
        throw new ArgumentOutOfRangeException(nameof(major), "The cache source SQL version is not supported.");

    internal static PlanCacheContentSourceBundle LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(PlanCacheContentSourceBundle).Assembly;
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

    internal async Task<PlanCacheContentSourceRead> ReadAsync(SqlConnection connection,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        int maximumRows, int maximumContentEntries, IQuerySensitiveContentProtector protector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(protector);
        cancellationToken.ThrowIfCancellationRequested();
        if (connection.State != ConnectionState.Open || maximumRows is < 1 or > PlanCacheRuntimeSourceReader.MaximumRows
            || maximumContentEntries is < 0 or > PlanCacheContentSourceReader.MaximumEntries || maximumContentEntries > maximumRows)
            throw new ArgumentException("Cache source requires an open connection and bounded candidate limit.");
        int major = int.Parse(connection.ServerVersion.Split('.')[0], CultureInfo.InvariantCulture);
        await using var command = new SqlCommand(GetSql(major), connection) { CommandTimeout = 20 };
        command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value = maximumRows;
        int effectiveEntries = protector.IsAvailable ? maximumContentEntries : 0;
        command.Parameters.Add("@maximum_content_entries", SqlDbType.Int).Value = effectiveEntries;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,
                deadline.Token).ConfigureAwait(false);
            PlanCacheContentSourceRead result = await PlanCacheContentSourceReader.ReadAsync(reader, targetId, targetRevision,
                maximumRows, effectiveEntries, protector, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception exception) when (deadline.IsCancellationRequested)
        { throw new TimeoutException("Cache content source deadline expired.", exception); }
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ??
            throw new InvalidDataException("Cache source resource is missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
}
