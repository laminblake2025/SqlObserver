using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Inactive, checksum-pinned incremental XE source. No checkpoint is persisted here.</summary>
internal sealed class XeFileSourceBundle
{
    internal const string ExpectedBundleChecksum = "4b00f8c40a3b0359fc1308b748e945f4df46ac0daae2b647f6c165478a951822";
    private const string ResourcePrefix = "SqlObserver.Infrastructure.SqlServer.XeFileSourceAssets.";
    private readonly IReadOnlyDictionary<int, string> scripts;

    private XeFileSourceBundle(IReadOnlyDictionary<int, string> scripts) => this.scripts = scripts;

    internal static XeFileSourceBundle LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(XeFileSourceBundle).Assembly;
        byte[] manifest = Read(assembly, "assets.sha256", 1_024);
        if (!Convert.ToHexString(SHA256.HashData(manifest)).Equals(ExpectedBundleChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("XE source bundle digest is not approved.");
        string[] lines = Encoding.UTF8.GetString(manifest).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 3) throw new InvalidDataException("XE source bundle has an unexpected asset count.");
        var scripts = new Dictionary<int, string>();
        for (int major = 15; major <= 17; major++)
        {
            string name = $"read.sqlserver{major}-windows.v1.sql";
            string[] parts = lines[major - 15].Split("  ", StringSplitOptions.None);
            byte[] bytes = Read(assembly, name, 65_536);
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (parts.Length != 2 || parts[1] != name || parts[0] != actual)
                throw new InvalidDataException("XE source asset checksum failed.");
            scripts.Add(major, Encoding.UTF8.GetString(bytes));
        }
        return new(scripts);
    }

    internal async Task<XeFileSourcePage> ReadAsync(SqlConnection connection, MonitoredInstanceId target,
        ObservationTargetRevision revision, XeReadBookmark? previous, XeReadIdentity? expectedSource,
        XeFileReadLimits limits, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(limits);
        if (connection.State != ConnectionState.Open ||
            !int.TryParse(connection.ServerVersion.Split('.')[0], out int major) || !scripts.TryGetValue(major, out string? sql))
            throw new ArgumentException("XE file source requires an open supported SQL Server connection.", nameof(connection));
        XeReadIdentity? identity = expectedSource ?? previous?.Identity;
        if (identity is not null && (identity.TargetId != target || identity.TargetRevision != revision) ||
            previous is not null && previous.Identity != identity)
            throw new ArgumentException("XE source requires one exact target revision and incarnation.");

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
        command.Parameters.Add("@maximum_metadata_rows", SqlDbType.Int).Value = limits.MetadataRows;
        command.Parameters.Add("@maximum_deadlocks", SqlDbType.Int).Value = limits.Deadlocks;
        command.Parameters.Add("@maximum_response_bytes", SqlDbType.Int).Value = limits.ResponseBytes;
        command.Parameters.Add("@expected_epoch", SqlDbType.VarChar, 64).Value = (object?)identity?.SourceEpoch ?? DBNull.Value;
        command.Parameters.Add("@initial_generation", SqlDbType.BigInt).Value = (object?)previous?.Position.FileGeneration ?? DBNull.Value;
        command.Parameters.Add("@initial_index", SqlDbType.Int).Value = (object?)previous?.Position.RolloverIndex ?? DBNull.Value;
        command.Parameters.Add("@initial_offset", SqlDbType.BigInt).Value = (object?)previous?.Position.BufferOffset ?? DBNull.Value;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
        XeFileSourcePage page = await XeFileSourceReader.ReadAsync(reader, target, revision, previous, limits, token).ConfigureAwait(false);
        if (page.State == XeFileSourceState.Ready && identity is not null && page.Source != identity)
            throw new InvalidDataException("Ready XE source changed its expected incarnation.");
        return page;
    }

    private static byte[] Read(Assembly assembly, string name, int maximumBytes)
    {
        using Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidDataException("XE source asset is missing.");
        if (stream.Length > maximumBytes) throw new InvalidDataException("XE source asset exceeds its reviewed bound.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
