using System.Buffers.Binary;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class AvailabilityGroupEvidenceCursorTests
{
    [Theory]
    [InlineData(AvailabilityGroupEvidenceKind.Replicas)]
    [InlineData(AvailabilityGroupEvidenceKind.Databases)]
    public void FullIdentityRoundTripsAtMaximumRevisionWithinTheSharedByteBound(AvailabilityGroupEvidenceKind stream)
    {
        var signer = new ApiCursorSigner(new byte[32]);
        var codec = new AvailabilityGroupEvidenceCursorCodec(signer);
        AvailabilityGroupEvidenceCursor cursor = Cursor(stream);
        string encoded = codec.Encode(cursor);
        Assert.InRange(encoded.Length, 1, 512);
        Assert.Equal(cursor, codec.Decode(encoded, stream));
        Assert.Equal(encoded, codec.Encode(codec.Decode(encoded, stream)));
        AvailabilityGroupEvidenceKind other = stream == AvailabilityGroupEvidenceKind.Replicas
            ? AvailabilityGroupEvidenceKind.Databases : AvailabilityGroupEvidenceKind.Replicas;
        Assert.Throws<ArgumentException>(() => codec.Decode(encoded, other));
        Assert.Throws<ArgumentException>(() => signer.Decode("server-configuration-snapshot", encoded));
        Assert.Throws<ArgumentException>(() => new AvailabilityGroupEvidenceCursorCodec(
            new ApiCursorSigner(Enumerable.Repeat((byte)1, 32).ToArray())).Decode(encoded, stream));
        Assert.Throws<ArgumentException>(() => codec.Decode(encoded[..^1] + (encoded[^1] == 'A' ? 'B' : 'A'), stream));
    }

    [Fact]
    public void SignedMalformedBinaryKeysAndNoncanonicalEncodingAreRejected()
    {
        const string purpose = "availability-groups-v2-databases";
        var signer = new ApiCursorSigner(new byte[32]);
        var codec = new AvailabilityGroupEvidenceCursorCodec(signer);
        string encoded = codec.Encode(Cursor(AvailabilityGroupEvidenceKind.Databases));
        byte[] original = Convert.FromBase64String(signer.Decode(purpose, encoded));
        for (int scenario = 0; scenario < 8; scenario++)
        {
            byte[] payload = original.ToArray();
            switch (scenario)
            {
                case 0: payload[0] = 1; break;
                case 1: payload[1] = 1; break;
                case 2: payload.AsSpan(2, 16).Clear(); break;
                case 3: BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(18, 8), -1); break;
                case 4: BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(18, 8), DateTimeOffset.UtcNow.Ticks / 10 * 10 + 1); break;
                case 5: BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(26, 8), 0); break;
                case 6: payload.AsSpan(34, 16).Clear(); break;
                case 7: payload = payload[..^1]; break;
            }
            string malformed = signer.Encode(purpose, Convert.ToBase64String(payload));
            Assert.Throws<ArgumentException>(() => codec.Decode(malformed, AvailabilityGroupEvidenceKind.Databases));
        }
        string whitespace = signer.Encode(purpose, Convert.ToBase64String(original) + "\n");
        Assert.Throws<ArgumentException>(() => codec.Decode(whitespace, AvailabilityGroupEvidenceKind.Databases));
        Assert.Throws<ArgumentException>(() => codec.Decode(new string('a', 513), AvailabilityGroupEvidenceKind.Databases));
    }

    private static AvailabilityGroupEvidenceCursor Cursor(AvailabilityGroupEvidenceKind stream) =>
        new(new MonitoredInstanceId(Guid.NewGuid()),
            new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_990),
            long.MaxValue, Guid.NewGuid(), stream, new string('a', 64), new string('b', 64),
            stream == AvailabilityGroupEvidenceKind.Databases ? new string('c', 64) : null);
}
