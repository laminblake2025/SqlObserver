using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed record SqlServerConfigurationValue(
    int ConfigurationId, string Name, string SourceType,
    string ConfiguredValue, string InUseValue, bool IsDynamic, bool IsAdvanced);

internal sealed record SqlServerConfigurationRead(
    DateTimeOffset ObservedAtUtc, IReadOnlyList<SqlServerConfigurationValue> Values,
    int SourceRowsRead, int ResponseBytes,
    bool SourceRowLimitReached, bool ResponseByteLimitReached)
{
    internal bool Incomplete => SourceRowLimitReached || ResponseByteLimitReached;
}

/// <summary>Reads a complete, bounded SQL Server configuration scan or withholds it.</summary>
internal static class SqlServerConfigurationSource
{
    internal const int MaximumConfigurations = 512;
    internal const int MaximumResponseBytes = 65_536;
    private const string ResourcePrefix =
        "SqlObserver.Infrastructure.SqlServer.ConfigurationSourceAssets.engine.configurations.sqlserver";
    private const string QuerySha256 =
        "3b3e7abb786706d5e766762373cd005d38fadf23d28227fd27638c54b40f469f";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string LoadPinnedQuery(int majorVersion)
    {
        if (majorVersion is < 15 or > 17)
            throw new ArgumentOutOfRangeException(nameof(majorVersion));
        string resourceName = $"{ResourcePrefix}{majorVersion}-windows.v1.sql";
        using Stream stream = typeof(SqlServerConfigurationSource).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("The configuration source asset is missing.");
        if (stream.Length is <= 0 or > 16_384)
            throw new InvalidDataException("The configuration source asset exceeds its byte bound.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (bytes.Contains((byte)'\r') ||
            bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(QuerySha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The configuration source asset failed its pinned checksum.");
        return StrictUtf8.GetString(bytes);
    }

    internal static async ValueTask<SqlServerConfigurationRead> ReadAsync(
        DbDataReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.FieldCount != 9)
            throw new InvalidDataException("The configuration source returned an unexpected column shape.");
        var values = new List<SqlServerConfigurationValue>();
        int sourceRowsRead = 0;
        int responseBytes = 0;
        int previousId = 0;
        DateTimeOffset observedAt = default;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sourceRowsRead++;
            if (sourceRowsRead > MaximumConfigurations)
                return new SqlServerConfigurationRead(observedAt, [], sourceRowsRead,
                    responseBytes, SourceRowLimitReached: true, ResponseByteLimitReached: false);
            if (reader.IsDBNull(0))
                throw new InvalidDataException("The configuration source omitted a required value.");
            DateTime timestamp = reader.GetDateTime(0);
            if (timestamp.Kind == DateTimeKind.Local)
                throw new InvalidDataException("The configuration scan time is not UTC.");
            var rowTime = new DateTimeOffset(new DateTime(timestamp.Ticks -
                timestamp.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc));
            if (reader.IsDBNull(1))
                throw new InvalidDataException("The configuration source omitted a required value.");
            int id = reader.GetInt32(1);
            if (reader.IsDBNull(2))
                throw new InvalidDataException("The configuration source omitted a required value.");
            string name = reader.GetString(2);
            if (reader.IsDBNull(3))
                throw new InvalidDataException("The configuration source omitted a required value.");
            string configuredType = reader.GetString(3);
            if (reader.IsDBNull(4))
                throw new InvalidDataException("The configuration source omitted a required value.");
            string inUseType = reader.GetString(4);
            if (reader.IsDBNull(5))
                throw new InvalidDataException("The configuration source omitted a required value.");
            string configured = reader.GetString(5);
            if (reader.IsDBNull(6))
                throw new InvalidDataException("The configuration source omitted a required value.");
            string inUse = reader.GetString(6);
            if (reader.IsDBNull(7))
                throw new InvalidDataException("The configuration source omitted a required value.");
            bool isDynamic = reader.GetBoolean(7);
            if (reader.IsDBNull(8))
                throw new InvalidDataException("The configuration source omitted a required value.");
            bool isAdvanced = reader.GetBoolean(8);
            if (id is < 1 or > 1_000_000 || id <= previousId ||
                sourceRowsRead > 1 && rowTime != observedAt ||
                !ValidText(name, 256) || !ValidType(configuredType) ||
                !string.Equals(configuredType, inUseType, StringComparison.Ordinal) ||
                !ValidValue(configuredType, configured) ||
                !ValidValue(inUseType, inUse))
                throw new InvalidDataException("The configuration source returned invalid evidence.");
            previousId = id;
            observedAt = rowTime;
            int rowBytes = checked(64 + StrictUtf8.GetByteCount(name) +
                StrictUtf8.GetByteCount(configuredType) +
                StrictUtf8.GetByteCount(configured) + StrictUtf8.GetByteCount(inUse));
            if (responseBytes + rowBytes > MaximumResponseBytes)
                return new SqlServerConfigurationRead(observedAt, [], sourceRowsRead,
                    responseBytes, SourceRowLimitReached: false, ResponseByteLimitReached: true);
            responseBytes += rowBytes;
            values.Add(new SqlServerConfigurationValue(id, name, configuredType,
                configured, inUse, isDynamic, isAdvanced));
        }
        if (sourceRowsRead == 0)
            throw new InvalidDataException("The configuration source returned no visible rows.");
        return new SqlServerConfigurationRead(observedAt, values.AsReadOnly(),
            sourceRowsRead, responseBytes, false, false);
    }

    private static bool ValidText(string value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            return false;
        try { return StrictUtf8.GetByteCount(value) <= maximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool ValidType(string value) =>
        value is "bit" or "tinyint" or "smallint" or "int" or "bigint";

    private static bool ValidValue(string sourceType, string value)
    {
        if (!ValidText(value, 128) || value.Trim() != value ||
            !long.TryParse(value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long parsed) ||
            parsed.ToString(CultureInfo.InvariantCulture) != value)
            return false;
        return sourceType switch
        {
            "bit" => parsed is 0 or 1,
            "tinyint" => parsed is >= 0 and <= byte.MaxValue,
            "smallint" => parsed is >= short.MinValue and <= short.MaxValue,
            "int" => parsed is >= int.MinValue and <= int.MaxValue,
            "bigint" => true,
            _ => false,
        };
    }
}
