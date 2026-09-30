using System.Buffers.Binary;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Server;

/// <summary>Authenticates the complete AG stream key within the shared signer's byte bound.</summary>
internal sealed class AvailabilityGroupEvidenceCursorCodec(ApiCursorSigner signer)
{
    private static readonly RepositoryCallTimeout ValidationTimeout = new(TimeSpan.FromSeconds(5));
    private const int ReplicaBytes = 114;
    private const int DatabaseBytes = 146;

    internal string Encode(AvailabilityGroupEvidenceCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Validate(cursor);
        byte[] payload = new byte[cursor.Stream == AvailabilityGroupEvidenceKind.Replicas ? ReplicaBytes : DatabaseBytes];
        payload[0] = 2;
        payload[1] = (byte)cursor.Stream;
        cursor.TargetId.Value.TryWriteBytes(payload.AsSpan(2, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(18, 8), cursor.AtUtc.Ticks);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(26, 8), cursor.TargetRevision);
        cursor.RunId.TryWriteBytes(payload.AsSpan(34, 16));
        Convert.FromHexString(cursor.GroupFingerprint).CopyTo(payload, 50);
        Convert.FromHexString(cursor.ReplicaFingerprint).CopyTo(payload, 82);
        if (cursor.DatabaseFingerprint is not null)
            Convert.FromHexString(cursor.DatabaseFingerprint).CopyTo(payload, 114);
        return signer.Encode(Purpose(cursor.Stream), Convert.ToBase64String(payload));
    }

    internal AvailabilityGroupEvidenceCursor Decode(string encoded, AvailabilityGroupEvidenceKind stream)
    {
        try
        {
            byte[] payload = Convert.FromBase64String(signer.Decode(Purpose(stream), encoded));
            if (payload.Length != (stream == AvailabilityGroupEvidenceKind.Replicas ? ReplicaBytes : DatabaseBytes) ||
                payload[0] != 2 || payload[1] != (byte)stream)
                throw Invalid();
            var cursor = new AvailabilityGroupEvidenceCursor(new MonitoredInstanceId(new Guid(payload.AsSpan(2, 16))),
                new DateTimeOffset(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(18, 8)), TimeSpan.Zero),
                BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(26, 8)), new Guid(payload.AsSpan(34, 16)), stream,
                Convert.ToHexStringLower(payload.AsSpan(50, 32)), Convert.ToHexStringLower(payload.AsSpan(82, 32)),
                stream == AvailabilityGroupEvidenceKind.Databases ? Convert.ToHexStringLower(payload.AsSpan(114, 32)) : null);
            Validate(cursor);
            if (!string.Equals(Encode(cursor), encoded, StringComparison.Ordinal)) throw Invalid();
            return cursor;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw Invalid();
        }
    }

    private static void Validate(AvailabilityGroupEvidenceCursor cursor) =>
        _ = new AvailabilityGroupEvidenceRequest(cursor.TargetId, cursor.AtUtc, cursor.Stream, 1, ValidationTimeout, cursor);

    private static string Purpose(AvailabilityGroupEvidenceKind stream) => stream switch
    {
        AvailabilityGroupEvidenceKind.Replicas => "availability-groups-v2-replicas",
        AvailabilityGroupEvidenceKind.Databases => "availability-groups-v2-databases",
        _ => throw Invalid(),
    };

    private static ArgumentException Invalid() => new("The AG evidence cursor is invalid.", "cursor");
}
