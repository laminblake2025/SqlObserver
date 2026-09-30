using System.Buffers.Binary;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Server;

internal sealed class BackupEvidenceCursorCodec(ApiCursorSigner signer)
{
    private const string Purpose = "backup-v2-databases";
    internal string Encode(BackupEvidenceCursor cursor)
    {
        Validate(cursor);
        byte[] payload = new byte[70]; payload[0] = 2; payload[1] = 1;
        cursor.TargetId.Value.TryWriteBytes(payload.AsSpan(2, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(18, 8), cursor.AtUtc.Ticks);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(26, 8), cursor.TargetRevision);
        cursor.RunId.TryWriteBytes(payload.AsSpan(34, 16));
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(50, 4), cursor.DatabaseOrdinal);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(54, 8), cursor.PolicyHistoryCount);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(62, 8), cursor.QualificationCommandRevision);
        return signer.Encode(Purpose, Convert.ToBase64String(payload));
    }
    internal BackupEvidenceCursor Decode(string encoded)
    {
        try
        {
            byte[] payload = Convert.FromBase64String(signer.Decode(Purpose, encoded));
            if (payload.Length != 70 || payload[0] != 2 || payload[1] != 1) throw Invalid();
            var cursor = new BackupEvidenceCursor(new(new Guid(payload.AsSpan(2, 16))),
                new(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(18, 8)), TimeSpan.Zero),
                BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(26, 8)), new(payload.AsSpan(34, 16)),
                BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(50, 4)),
                BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(54, 8)), BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(62, 8)));
            Validate(cursor);
            if (Encode(cursor) != encoded) throw Invalid();
            return cursor;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { throw Invalid(); }
    }
    private static void Validate(BackupEvidenceCursor cursor) =>
        _ = new BackupEvidenceRequest(cursor.TargetId, cursor.AtUtc, 1, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cursor: cursor);
    private static ArgumentException Invalid() => new("The backup evidence cursor is invalid.", "cursor");
}
