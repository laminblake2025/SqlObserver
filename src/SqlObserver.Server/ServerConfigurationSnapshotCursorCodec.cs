using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Server;

internal sealed class ServerConfigurationSnapshotCursorCodec(ApiCursorSigner signer)
{
    private const string Purpose = "server-configuration-snapshot";

    internal string Encode(ServerConfigurationSnapshotCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        string value = string.Join('\n', "ss1", cursor.TargetId.Value.ToString("N"),
            cursor.TargetRevision.ToString(CultureInfo.InvariantCulture),
            cursor.RunId.ToString("N"),
            cursor.ConfigurationId.ToString(CultureInfo.InvariantCulture));
        return signer.Encode(Purpose, value);
    }

    internal ServerConfigurationSnapshotCursor Decode(string encoded)
    {
        try
        {
            string[] parts = signer.Decode(Purpose, encoded).Split('\n');
            if (parts.Length != 5 || parts[0] != "ss1" ||
                !Guid.TryParseExact(parts[1], "N", out Guid target) || target == Guid.Empty ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture,
                    out long revision) || revision < 1 ||
                !Guid.TryParseExact(parts[3], "N", out Guid run) || run == Guid.Empty ||
                !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture,
                    out int configurationId) || configurationId is < 1 or > 1_000_000)
                throw Invalid();
            var cursor = new ServerConfigurationSnapshotCursor(
                new MonitoredInstanceId(target), revision, run, configurationId);
            if (!string.Equals(Encode(cursor), encoded, StringComparison.Ordinal))
                throw Invalid();
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw Invalid();
        }
    }

    private static ArgumentException Invalid() =>
        new("The configuration snapshot cursor is invalid.", "cursor");
}
