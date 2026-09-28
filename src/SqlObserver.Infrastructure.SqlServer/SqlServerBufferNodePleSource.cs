using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed record BufferNodePle(int NodeId, long PageLifeExpectancySeconds);

internal sealed record BufferNodePleRead(
    DateTimeOffset ObservedAtUtc, IReadOnlyList<BufferNodePle> Nodes,
    int SourceRowsRead, int ResponseBytes,
    bool SourceRowLimitReached, bool ResponseByteLimitReached)
{
    internal bool Incomplete => SourceRowLimitReached || ResponseByteLimitReached;
}

/// <summary>
/// A bounded, pinned SQL Server source for individual Buffer Node PLE values.
/// The reader withholds the entire sample when it cannot prove full coverage.
/// </summary>
internal static class SqlServerBufferNodePleSource
{
    private const string ResourceName =
        "SqlObserver.Infrastructure.SqlServer.BufferNodeAssets.engine.buffer-node-ple.sqlserver15-17-windows.v1.sql";
    private const string QuerySha256 =
        "9839f0e268760be61980696506c93b59a0432595bc8401930c5eb6f50e7482cf";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string LoadPinnedQuery()
    {
        using Stream stream = typeof(SqlServerBufferNodePleSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Buffer Node PLE source asset is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (bytes.Length is 0 or > 16_384 || bytes.Contains((byte)'\r') ||
            bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(QuerySha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Buffer Node PLE source asset failed its pinned checksum.");
        return StrictUtf8.GetString(bytes);
    }

    internal static async ValueTask<BufferNodePleRead> ReadAsync(DbDataReader reader,
        int maximumNodes, int maximumResponseBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumNodes is < 1 or > 128 || maximumResponseBytes is < 64 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        if (reader.FieldCount != 3)
            throw new InvalidDataException("The Buffer Node PLE source returned an unexpected column shape.");

        var nodes = new List<BufferNodePle>(maximumNodes);
        int sourceRowsRead = 0;
        int responseBytes = 0;
        int previousNode = -1;
        DateTimeOffset observedAt = default;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sourceRowsRead++;
            if (sourceRowsRead > maximumNodes)
                return new BufferNodePleRead(observedAt, [], sourceRowsRead, responseBytes,
                    SourceRowLimitReached: true, ResponseByteLimitReached: false);
            DateTime value = reader.GetDateTime(0);
            if (value.Kind == DateTimeKind.Local)
                throw new InvalidDataException("The Buffer Node PLE observation time is not UTC.");
            DateTimeOffset rowTime = new(new DateTime(value.Ticks -
                value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc));
            if (reader.IsDBNull(1))
                throw new InvalidDataException("The Buffer Node PLE source omitted a node identity or value.");
            int nodeId = reader.GetInt32(1);
            if (reader.IsDBNull(2))
                throw new InvalidDataException("The Buffer Node PLE source omitted a node identity or value.");
            long seconds = reader.GetInt64(2);
            if (nodeId is < 0 or > 1023 || nodeId <= previousNode || seconds < 0 ||
                sourceRowsRead > 1 && rowTime != observedAt)
                throw new InvalidDataException("The Buffer Node PLE source returned invalid node evidence.");
            previousNode = nodeId;
            observedAt = rowTime;
            const int rowBytes = 64;
            if (responseBytes + rowBytes > maximumResponseBytes)
                return new BufferNodePleRead(observedAt, [], sourceRowsRead, responseBytes,
                    SourceRowLimitReached: false, ResponseByteLimitReached: true);
            responseBytes += rowBytes;
            nodes.Add(new BufferNodePle(nodeId, seconds));
        }
        if (sourceRowsRead == 0)
            throw new InvalidDataException("The Buffer Node PLE source returned no visible nodes.");
        return new BufferNodePleRead(observedAt, nodes.AsReadOnly(), sourceRowsRead,
            responseBytes, false, false);
    }
}
