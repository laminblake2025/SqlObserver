using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Server;

internal sealed class ServerConfigurationChangeCursorCodec(ApiCursorSigner signer)
{
    private const string Purpose = "server-configuration-change";

    internal string Encode(ServerConfigurationChangeCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        string value = string.Join('\n', "sc1", cursor.TargetId.Value.ToString("N"),
            cursor.FromUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            cursor.ToUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            cursor.TargetRevision.ToString(CultureInfo.InvariantCulture),
            cursor.PublicationGeneration.ToString(CultureInfo.InvariantCulture),
            cursor.DetectedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            cursor.RunId.ToString("N"),
            cursor.ConfigurationId.ToString(CultureInfo.InvariantCulture));
        return signer.Encode(Purpose, value);
    }

    internal ServerConfigurationChangeCursor Decode(string encoded)
    {
        try
        {
            string value = signer.Decode(Purpose, encoded);
            string[] parts = value.Split('\n');
            if (parts.Length != 9 || parts[0] != "sc1" ||
                !Guid.TryParseExact(parts[1], "N", out Guid target) || target == Guid.Empty ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long fromTicks) ||
                !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long toTicks) ||
                !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ||
                !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out long generation) ||
                !long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out long detectedTicks) ||
                !Guid.TryParseExact(parts[7], "N", out Guid run) || run == Guid.Empty ||
                !int.TryParse(parts[8], NumberStyles.None, CultureInfo.InvariantCulture, out int configurationId))
                throw Invalid();
            var cursor = new ServerConfigurationChangeCursor(
                new MonitoredInstanceId(target), new DateTimeOffset(fromTicks, TimeSpan.Zero),
                new DateTimeOffset(toTicks, TimeSpan.Zero), revision, generation,
                new DateTimeOffset(detectedTicks, TimeSpan.Zero), run, configurationId);
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
        new("The configuration history cursor is invalid.", "cursor");
}
