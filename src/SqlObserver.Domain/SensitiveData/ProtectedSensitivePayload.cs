using System.Buffers.Binary;

namespace SqlObserver.Domain.SensitiveData;

/// <summary>Classifies sensitive, untrusted diagnostic content without exposing the content.</summary>
public enum SensitivePayloadKind
{
    QueryText = 1,
    ExecutionPlan = 2,
    DeadlockXml = 3,
    SqlServerError = 4,
    SqlAgentJobStep = 5,
    ObjectName = 6,
    DiagnosticXml = 7,
    OtherDiagnosticContent = 8,
}

/// <summary>A SHA-256 digest used for collision-safe payload lookup.</summary>
public readonly record struct SensitivePayloadFingerprint
{
    public const int RequiredLength = 32;

    private readonly ulong _first;
    private readonly ulong _second;
    private readonly ulong _third;
    private readonly ulong _fourth;

    public SensitivePayloadFingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RequiredLength)
        {
            throw new ArgumentException(
                $"A payload fingerprint must contain exactly {RequiredLength} bytes.",
                nameof(bytes));
        }

        _first = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _second = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        _third = BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]);
        _fourth = BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < RequiredLength)
        {
            throw new ArgumentException(
                $"The destination must contain at least {RequiredLength} bytes.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _first);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _second);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _third);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _fourth);
    }

    public byte[] ToArray()
    {
        var result = new byte[RequiredLength];
        CopyTo(result);
        return result;
    }

    public string ToHexString() => Convert.ToHexString(ToArray());

    public override string ToString() => ToHexString();
}

public sealed record SensitivePayloadId
{
    public SensitivePayloadId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A sensitive-payload identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

/// <summary>A safe reference suitable for event envelopes and audit metadata.</summary>
public sealed record SensitivePayloadReference
{
    public SensitivePayloadReference(
        SensitivePayloadId payloadId,
        SensitivePayloadKind kind,
        SensitivePayloadFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(payloadId);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        PayloadId = payloadId;
        Kind = kind;
        Fingerprint = fingerprint;
    }

    public SensitivePayloadId PayloadId { get; }

    public SensitivePayloadKind Kind { get; }

    public SensitivePayloadFingerprint Fingerprint { get; }
}

/// <summary>
/// An already-protected sensitive payload. This contract intentionally has no unprotected-content member.
/// </summary>
public sealed class ProtectedSensitivePayload
{
    public const int MinimumNonceBytes = 8;
    public const int MaximumNonceBytes = 32;
    public const int MinimumAuthenticationTagBytes = 12;
    public const int MaximumAuthenticationTagBytes = 32;
    public const int MaximumCiphertextBytes = 1_048_576;
    public const int MaximumProtectionAlgorithmLength = 64;
    public const int MaximumKeyIdentifierUtf8Bytes = 128;

    private readonly byte[] _nonce;
    private readonly byte[] _authenticationTag;
    private readonly byte[] _ciphertext;

    public ProtectedSensitivePayload(
        SensitivePayloadKind kind,
        SensitivePayloadFingerprint fingerprint,
        string protectionAlgorithm,
        string keyIdentifier,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> authenticationTag,
        ReadOnlySpan<byte> ciphertext)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (nonce.Length is < MinimumNonceBytes or > MaximumNonceBytes)
        {
            throw new ArgumentException(
                $"The nonce must contain between {MinimumNonceBytes} and {MaximumNonceBytes} bytes.",
                nameof(nonce));
        }

        if (authenticationTag.Length is < MinimumAuthenticationTagBytes or > MaximumAuthenticationTagBytes)
        {
            throw new ArgumentException(
                $"The authentication tag must contain between {MinimumAuthenticationTagBytes} and {MaximumAuthenticationTagBytes} bytes.",
                nameof(authenticationTag));
        }

        if (ciphertext.Length is 0 or > MaximumCiphertextBytes)
        {
            throw new ArgumentException(
                $"The ciphertext must contain between 1 and {MaximumCiphertextBytes} bytes.",
                nameof(ciphertext));
        }

        Kind = kind;
        Fingerprint = fingerprint;
        ProtectionAlgorithm = DomainValidation.RequireAsciiToken(
            protectionAlgorithm,
            nameof(protectionAlgorithm),
            MaximumProtectionAlgorithmLength,
            static character => DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '-' or '_' or '.');
        KeyIdentifier = DomainValidation.RequireSafeText(
            keyIdentifier,
            nameof(keyIdentifier),
            MaximumKeyIdentifierUtf8Bytes);
        _nonce = nonce.ToArray();
        _authenticationTag = authenticationTag.ToArray();
        _ciphertext = ciphertext.ToArray();
    }

    public SensitivePayloadKind Kind { get; }

    public SensitivePayloadFingerprint Fingerprint { get; }

    public string ProtectionAlgorithm { get; }

    public string KeyIdentifier { get; }

    public int NonceLengthBytes => _nonce.Length;

    public int AuthenticationTagLengthBytes => _authenticationTag.Length;

    public int CiphertextLengthBytes => _ciphertext.Length;

    public int ProtectedSizeBytes => checked(_nonce.Length + _authenticationTag.Length + _ciphertext.Length);

    public byte[] GetNonce() => (byte[])_nonce.Clone();

    public byte[] GetAuthenticationTag() => (byte[])_authenticationTag.Clone();

    public byte[] GetCiphertext() => (byte[])_ciphertext.Clone();
}
