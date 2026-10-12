using System.Globalization;
using SqlObserver.Application.Ports;

namespace SqlObserver.Server;

internal sealed class SavedIncidentListCursorCodec(ApiCursorSigner signer)
{
    private const string Purpose = "saved-incident-list";

    internal string Encode(SavedIncidentListCursor cursor) => signer.Encode(Purpose, string.Join('\n', "si1",
        cursor.TargetId.ToString("N"), cursor.PublicationRevision.ToString(CultureInfo.InvariantCulture),
        cursor.SnapshotUtc.Ticks.ToString(CultureInfo.InvariantCulture), cursor.SavedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
        cursor.ThreadId.ToString("N"), cursor.Limit.ToString(CultureInfo.InvariantCulture)));

    internal SavedIncidentListCursor Decode(string encoded)
    {
        try
        {
            string[] parts = signer.Decode(Purpose, encoded).Split('\n');
            if (parts.Length != 7 || parts[0] != "si1"
                || !Guid.TryParseExact(parts[1], "N", out Guid target) || target == Guid.Empty
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision < 1
                || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long snapshot)
                || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out long saved)
                || !Guid.TryParseExact(parts[5], "N", out Guid thread) || thread == Guid.Empty
                || !int.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out int limit) || limit is < 1 or > 50)
                throw Invalid();
            var cursor = new SavedIncidentListCursor(target, revision, new(snapshot, TimeSpan.Zero), new(saved, TimeSpan.Zero), thread, limit);
            if (snapshot % TimeSpan.TicksPerMicrosecond != 0 || saved % TimeSpan.TicksPerMicrosecond != 0
                || saved > snapshot || Encode(cursor) != encoded) throw Invalid();
            return cursor;
        }
        catch (ArgumentOutOfRangeException) { throw Invalid(); }
    }

    private static ArgumentException Invalid() => new("The saved incident cursor is invalid.", "cursor");
}
