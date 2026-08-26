using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public static class OperationalHealthBounds
{
    public const int BackupMaximumRows = 1_537;
    public const int AgentMaximumRows = 512;
    public const int AgentScanRows = 4_096;
    public const int TempDbMaximumFiles = 128;
    public const int AvailabilityMaximumRows = 2_048;
    public const int MaximumCursorBytes = 1_024;
    public const int MaximumPageSize = 200;
}

/// <summary>Converts a SQL Server local backup timestamp only when its offset is trustworthy.</summary>
public static class SqlServerTimestamp
{
    public static (DateTimeOffset? Utc, DateTime Local, bool SourceTimeUnknown) ToUtc(
        DateTime local, short? timeZoneOffsetMinutes)
    {
        if (timeZoneOffsetMinutes is >= -2_880 and <= 2_880 && timeZoneOffsetMinutes.Value % 15 == 0)
        {
            var offset = TimeSpan.FromMinutes(timeZoneOffsetMinutes.Value);
            DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return (new DateTimeOffset(unspecified, offset).ToUniversalTime(), local, false);
        }

        return (null, local, true);
    }
}

/// <summary>Stable, opaque failure identity; no job names, commands, or messages are included.</summary>
public static class SqlAgentFailureIdentity
{
    public static string Compute(
        int schemaVersion, MonitoredInstanceId targetId, Guid jobId, long historyInstanceId,
        int stepId, int status)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        WriteCanonical(writer, "sqlobserver.agent-failure.v1");
        WriteCanonical(writer, schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteCanonical(writer, targetId.Value.ToString("D"));
        WriteCanonical(writer, jobId.ToString("D"));
        WriteCanonical(writer, historyInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteCanonical(writer, stepId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteCanonical(writer, status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    public static string Compute(
        int schemaVersion, MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        Guid jobId, long historyInstanceId, int stepId, int status)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        WriteCanonical(writer, "sqlobserver.agent-failure.v2");
        WriteCanonical(writer, schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteCanonical(writer, targetId.Value.ToString("D"));
        WriteCanonical(writer, targetRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteCanonical(writer, jobId.ToString("D"));
        WriteCanonical(writer, historyInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteCanonical(writer, stepId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteCanonical(writer, status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}

public sealed record OperationalHealthCursor(
    MonitoredInstanceId TargetId,
    DateTimeOffset SnapshotUtc,
    string TieKey)
{
    public string Encode()
    {
        string value = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TargetId.Value:D}|{SnapshotUtc:O}|{TieKey}"));
        return value.Length <= OperationalHealthBounds.MaximumCursorBytes ? value : throw new ArgumentException("Cursor exceeds its bound.");
    }

    public static OperationalHealthCursor Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > OperationalHealthBounds.MaximumCursorBytes) throw new ArgumentException("Cursor is missing or exceeds its bound.", nameof(encoded));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); } catch (FormatException exception) { throw new ArgumentException("Cursor encoding is invalid.", nameof(encoded), exception); }
        string[] parts = Encoding.UTF8.GetString(bytes).Split('|', 3);
        if (parts.Length != 3 || !Guid.TryParse(parts[0], out Guid target) || !DateTimeOffset.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset snapshot) || parts[2].Length is < 1 or > 512) throw new ArgumentException("Cursor shape is invalid.", nameof(encoded));
        return new OperationalHealthCursor(new MonitoredInstanceId(target), snapshot, parts[2]);
    }
}
