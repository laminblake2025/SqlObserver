using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.UnitTests;

public sealed class SensitivePayloadContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void FingerprintRequiresExactlySha256Length(int length)
    {
        Assert.Throws<ArgumentException>(() => new SensitivePayloadFingerprint(new byte[length]));
    }

    [Fact]
    public void FingerprintRoundTripsAllBytes()
    {
        byte[] bytes = Enumerable.Range(0, SensitivePayloadFingerprint.RequiredLength)
            .Select(static value => (byte)value)
            .ToArray();
        var fingerprint = new SensitivePayloadFingerprint(bytes);

        bytes[0] = byte.MaxValue;
        byte[] returned = fingerprint.ToArray();

        Assert.Equal(0, returned[0]);
        Assert.Equal(SensitivePayloadFingerprint.RequiredLength * 2, fingerprint.ToHexString().Length);
    }

    [Fact]
    public void ProtectedPayloadCopiesCipherMaterialAndExposesNoUnprotectedContentMember()
    {
        byte[] nonce = Enumerable.Repeat((byte)1, 12).ToArray();
        byte[] tag = Enumerable.Repeat((byte)2, 16).ToArray();
        byte[] ciphertext = [3, 4, 5];
        var payload = CreatePayload(nonce, tag, ciphertext);

        nonce[0] = 9;
        tag[0] = 9;
        ciphertext[0] = 9;
        byte[] returnedCiphertext = payload.GetCiphertext();
        returnedCiphertext[0] = 8;

        Assert.Equal(1, payload.GetNonce()[0]);
        Assert.Equal(2, payload.GetAuthenticationTag()[0]);
        Assert.Equal(3, payload.GetCiphertext()[0]);
        Assert.Equal(31, payload.ProtectedSizeBytes);
        Assert.DoesNotContain(
            typeof(ProtectedSensitivePayload).GetMembers(),
            static member => member.Name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProtectedPayloadAcceptsCiphertextAtMaximumBoundary()
    {
        var payload = CreatePayload(
            new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            new byte[ProtectedSensitivePayload.MaximumCiphertextBytes]);

        Assert.Equal(ProtectedSensitivePayload.MaximumCiphertextBytes, payload.CiphertextLengthBytes);
    }

    [Fact]
    public void ProtectedPayloadRejectsOversizedOrMalformedMaterial()
    {
        Assert.Throws<ArgumentException>(() => CreatePayload(
            new byte[ProtectedSensitivePayload.MinimumNonceBytes - 1],
            new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            [1]));
        Assert.Throws<ArgumentException>(() => CreatePayload(
            new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes - 1],
            [1]));
        Assert.Throws<ArgumentException>(() => CreatePayload(
            new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => CreatePayload(
            new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            new byte[ProtectedSensitivePayload.MaximumCiphertextBytes + 1]));
        Assert.Throws<ArgumentException>(() => new SensitivePayloadId(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SensitivePayloadReference(
            new SensitivePayloadId(Guid.NewGuid()),
            (SensitivePayloadKind)0,
            new SensitivePayloadFingerprint(new byte[SensitivePayloadFingerprint.RequiredLength])));
    }

    private static ProtectedSensitivePayload CreatePayload(
        byte[] nonce,
        byte[] tag,
        byte[] ciphertext) =>
        new(
            SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(new byte[SensitivePayloadFingerprint.RequiredLength]),
            "AES-256-GCM",
            "windows-key-v1",
            nonce,
            tag,
            ciphertext);
}
