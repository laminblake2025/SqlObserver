using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>
/// Checksum-pinned SQL Server 2022 group reads. Publication uses the fenced
/// repository writer so cumulative source values cannot be mistaken for deltas.
/// </summary>
internal sealed class QueryStoreGroupedSourceBundle
{
    internal const string ExpectedBundleChecksum =
        "dd90330a342fd560535ea7ab74cc93928756018acd1ed0648c472d13ad825f04";
    private const string ResourcePrefix = "SqlObserver.Infrastructure.SqlServer.QueryStoreGroupedSourceAssets.";
    private const string RuntimeName = "runtime.sqlserver16-windows.v1.sql";
    private const string WaitName = "waits.sqlserver16-windows.v1.sql";

    private QueryStoreGroupedSourceBundle(string runtimeSql, string waitSql)
    {
        RuntimeSql = runtimeSql;
        WaitSql = waitSql;
    }

    internal string RuntimeSql { get; }
    internal string WaitSql { get; }

    internal static QueryStoreGroupedSourceBundle LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(QueryStoreGroupedSourceBundle).Assembly;
        byte[] checksumBytes = Read(assembly, "assets.sha256");
        if (!Convert.ToHexString(SHA256.HashData(checksumBytes)).Equals(
                ExpectedBundleChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Query Store group source bundle digest is not approved.");
        string[] lines = Encoding.UTF8.GetString(checksumBytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 2)
            throw new InvalidDataException("Query Store group bundle has an unexpected asset count.");
        byte[] runtime = Verify(assembly, lines[0], RuntimeName);
        byte[] waits = Verify(assembly, lines[1], WaitName);
        return new QueryStoreGroupedSourceBundle(Encoding.UTF8.GetString(runtime), Encoding.UTF8.GetString(waits));
    }

    internal async Task<(QueryStoreRuntimeSourcePage Runtime, QueryStoreWaitSourcePage? Waits,
        QueryStoreGroupOutputBatch Output)> ReadDatabaseAsync(
        SqlConnection connection, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, int databaseId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int maximumGroups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open || databaseId <= 0 ||
            !connection.ServerVersion.StartsWith("16.", StringComparison.Ordinal) ||
            fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            fromUtc >= toUtc || maximumGroups is <= 0 or > QueryStoreGroupedSourceReader.MaximumGroups)
            throw new ArgumentException("Query Store group read is outside its bounded source contract.");

        // An empty source page has no row identity to validate. Check the
        // selected database before reading either source so empty is meaningful.
        await using (var database = new SqlCommand("SELECT CONVERT(int, DB_ID());", connection)
        { CommandTimeout = 5 })
        {
            object? selectedId = await database.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (selectedId is not int actualId || actualId != databaseId)
                throw new InvalidDataException("Query Store group connection selected another database.");
        }

        QueryStoreRuntimeSourcePage runtime;
        await using (var command = CreateCommand(connection, RuntimeSql, fromUtc, toUtc, maximumGroups))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            runtime = await QueryStoreGroupedSourceReader.ReadRuntimeAsync(reader, targetId,
                targetRevision, databaseId, maximumGroups, cancellationToken).ConfigureAwait(false);

        // A capped runtime page cannot support complete wait coverage; do not
        // spend another SQL read or allow a caller to advance any watermark.
        if (!runtime.Complete)
            return (runtime, null, MapOutput(runtime, null, targetId, targetRevision, fromUtc, toUtc));

        QueryStoreWaitSourcePage waits;
        await using (var command = CreateCommand(connection, WaitSql, fromUtc, toUtc, maximumGroups))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            waits = await QueryStoreGroupedSourceReader.ReadWaitsAsync(reader, targetId,
                targetRevision, databaseId, maximumGroups, cancellationToken).ConfigureAwait(false);
        return (runtime, waits, MapOutput(runtime, waits, targetId, targetRevision, fromUtc, toUtc));
    }

    internal static QueryStoreGroupOutputBatch MapOutput(QueryStoreRuntimeSourcePage runtime,
        QueryStoreWaitSourcePage? waits, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!runtime.Complete)
            return new QueryStoreGroupOutputBatch(targetId, targetRevision, fromUtc, toUtc,
                false, QueryStoreGroupWaitCoverage.Unavailable, []);
        if (waits is null)
            throw new InvalidDataException("Complete Query Store runtime coverage requires a wait source status.");
        QueryStoreGroupWaitCoverage coverage = waits.CaptureMode switch
        {
            QueryStoreWaitCaptureMode.On when waits.Complete => QueryStoreGroupWaitCoverage.Complete,
            QueryStoreWaitCaptureMode.On => QueryStoreGroupWaitCoverage.Truncated,
            QueryStoreWaitCaptureMode.Off => QueryStoreGroupWaitCoverage.CaptureOff,
            QueryStoreWaitCaptureMode.Unavailable => QueryStoreGroupWaitCoverage.Unavailable,
            _ => throw new InvalidDataException("Query Store wait source status is unsupported."),
        };
        IReadOnlyDictionary<QueryStoreRuntimeWatermarkKey, QueryStoreWaitWatermark> completeWaits =
            QueryStoreGroupedSourceReader.CompleteWaitSnapshots(runtime, waits)?
                .ToDictionary(static item => item.Key) ??
            new Dictionary<QueryStoreRuntimeWatermarkKey, QueryStoreWaitWatermark>();
        QueryStoreGroupObservation[] observations = runtime.Rows.Select(row =>
            new QueryStoreGroupObservation(row.Watermark,
                completeWaits.GetValueOrDefault(row.Watermark.Key))).ToArray();
        return new QueryStoreGroupOutputBatch(targetId, targetRevision, fromUtc, toUtc,
            true, coverage, observations);
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int maximumGroups)
    {
        var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@probe_rows", SqlDbType.Int).Value = maximumGroups + 1;
        command.Parameters.Add("@window_start", SqlDbType.DateTimeOffset).Value = fromUtc;
        command.Parameters.Add("@window_end", SqlDbType.DateTimeOffset).Value = toUtc;
        return command;
    }

    private static byte[] Verify(Assembly assembly, string line, string name)
    {
        string[] parts = line.Split("  ", StringSplitOptions.None);
        byte[] bytes = Read(assembly, name);
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (parts.Length != 2 || parts[1] != name ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(parts[0])))
            throw new InvalidDataException("Query Store group asset checksum failed.");
        return bytes;
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidDataException("Query Store group asset is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
