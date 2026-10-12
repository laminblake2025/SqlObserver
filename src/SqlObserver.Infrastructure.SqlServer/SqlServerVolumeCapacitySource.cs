using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed record SqlVolumeSourceRead(
    SqlVolumeObservationBatch Volumes,
    int SourceRowsRead,
    int ResponseBytes,
    bool SourceRowLimitReached,
    bool ResponseByteLimitReached);

/// <summary>Reads the pinned SQL volume query without retaining raw OS identifiers.</summary>
internal static class SqlServerVolumeCapacitySource
{
    private const string ResourceName = "SqlObserver.Infrastructure.SqlServer.StorageVolumeAssets.storage.volume.sqlserver15-17-windows.v1.sql";
    private const string QuerySha256 = "0b8be57ac4225687a9e267138a46683b1dcbbc7391df8a8b1550f1f6dfd94a75";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string LoadPinnedQuery()
    {
        using Stream stream = typeof(SqlServerVolumeCapacitySource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The SQL volume source asset is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (bytes.Length is 0 or > 64_000 || bytes.Contains((byte)'\r') ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(QuerySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The SQL volume source asset failed its pinned checksum.");
        return StrictUtf8.GetString(bytes);
    }

    internal static async ValueTask<SqlVolumeSourceRead> ReadAsync(
        DbDataReader reader,
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        CollectorRunId runId,
        IdentityFingerprintKey key,
        int maximumRows,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(key);
        if (maximumRows is < 1 or > SqlVolumeObservationBatch.MaximumItems || maximumResponseBytes is < 1 or > 4_194_304)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));

        var byKey = new Dictionary<string, SqlVolumeObservation>(StringComparer.Ordinal);
        int rowsRead = 0;
        int responseBytes = 0;
        (int DatabaseId, int FileId) previous = (0, 0);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowsRead++;
            if (rowsRead > maximumRows)
                return Incomplete(rowsRead, responseBytes, rowLimit: true);

            DateTimeOffset observedAt = ReadUtc(reader, 0);
            int databaseId = reader.GetInt32(1);
            int fileId = reader.GetInt32(2);
            if (databaseId is < 1 or > 32_767 || fileId is < 1 or > 65_535 ||
                (databaseId, fileId).CompareTo(previous) <= 0)
                throw new InvalidDataException("SQL volume source file identities are invalid or unordered.");
            previous = (databaseId, fileId);
            string node = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();
            string volumeId = reader.IsDBNull(4) ? "" : reader.GetString(4).Trim();
            string mount = reader.IsDBNull(5) ? "" : reader.GetString(5).Trim();
            long? total = reader.IsDBNull(6) ? null : reader.GetInt64(6);
            long? free = reader.IsDBNull(7) ? null : reader.GetInt64(7);
            int rowBytes;
            try
            {
                rowBytes = checked(96 + StrictUtf8.GetByteCount(node) +
                    StrictUtf8.GetByteCount(volumeId) + StrictUtf8.GetByteCount(mount));
            }
            catch (EncoderFallbackException)
            {
                throw new InvalidDataException("SQL volume source identity contains invalid Unicode.");
            }
            int nextResponseBytes = checked(responseBytes + rowBytes);
            if (nextResponseBytes > maximumResponseBytes)
                return Incomplete(rowsRead, responseBytes, rowLimit: false);
            responseBytes = nextResponseBytes;

            SqlVolumeIdentityKind kind = node.Length == 0 || (volumeId.Length == 0 && mount.Length == 0)
                ? SqlVolumeIdentityKind.FileScopedUnknown
                : volumeId.Length > 0 ? SqlVolumeIdentityKind.VolumeId : SqlVolumeIdentityKind.MountPoint;
            string identity = kind switch
            {
                SqlVolumeIdentityKind.VolumeId => volumeId.ToUpperInvariant(),
                SqlVolumeIdentityKind.MountPoint => mount.ToUpperInvariant(),
                _ => string.Concat(runId.Value.ToString("D"), "|", databaseId, "|", fileId),
            };
            string fingerprint = ReplicationIdentityFingerprint.FromTypedIdentity(
                "storage-volume",
                Encoding.UTF8.GetBytes(string.Concat(targetId.Value.ToString("D"), "|", targetRevision.Value,
                    "|", node.ToUpperInvariant(), "|", kind, "|", identity)), key);
            var current = new SqlVolumeObservation(targetId, targetRevision, fingerprint, kind, 1, total, free, observedAt);
            if (byKey.TryGetValue(fingerprint, out SqlVolumeObservation? existing))
            {
                if (existing.IdentityKind != kind || existing.TotalBytes != current.TotalBytes ||
                    existing.AvailableBytes.HasValue != current.AvailableBytes.HasValue)
                    throw new InvalidDataException("A SQL volume returned inconsistent capacity during one source read.");
                byKey[fingerprint] = new SqlVolumeObservation(targetId, targetRevision, fingerprint, kind,
                    checked(existing.MappedFileCount + 1), total,
                    existing.AvailableBytes is long priorFree && free is long currentFree
                        ? Math.Min(priorFree, currentFree) : null,
                    observedAt > existing.ObservedAtUtc ? observedAt : existing.ObservedAtUtc);
            }
            else
            {
                byKey.Add(fingerprint, current);
            }
        }

        if (rowsRead == 0)
            throw new InvalidDataException("SQL volume source returned no visible database files.");

        return new SqlVolumeSourceRead(
            new SqlVolumeObservationBatch(byKey.Values.OrderBy(static item => item.VolumeKey, StringComparer.Ordinal).ToArray()),
            rowsRead, responseBytes, false, false);
    }

    private static SqlVolumeSourceRead Incomplete(int rows, int bytes, bool rowLimit) => new(
        new SqlVolumeObservationBatch([]), rows, bytes, rowLimit, !rowLimit);

    private static DateTimeOffset ReadUtc(DbDataReader reader, int ordinal)
    {
        DateTime value = reader.GetDateTime(ordinal);
        if (value.Kind == DateTimeKind.Local)
            throw new InvalidDataException("SQL volume observation time is not UTC.");
        long ticks = value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond;
        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }
}
