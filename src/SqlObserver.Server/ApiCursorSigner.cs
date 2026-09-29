using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Server;

/// <summary>Purpose-bound signing shared by API cursor shapes.</summary>
public sealed class ApiCursorSigner
{
    private const int MaximumTokenLength = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _key;

    public ApiCursorSigner(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is < 32 or > 4096)
            throw new ArgumentException("API cursor signing key must contain 32 to 4096 bytes.", nameof(key));
        _key = key.ToArray();
    }

    public string Encode(string purpose, string payload)
    {
        ValidatePurpose(purpose);
        ArgumentNullException.ThrowIfNull(payload);
        byte[] body = StrictUtf8.GetBytes(payload);
        if (body.Length is < 1 or > 256)
            throw new ArgumentException("API cursor payload exceeds its byte bound.", nameof(payload));
        byte[] tag = HMACSHA256.HashData(_key, SignedBytes(purpose, body));
        return string.Concat(Base64Url(body), ".", Base64Url(tag));
    }

    public string Decode(string purpose, string token)
    {
        ValidatePurpose(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > MaximumTokenLength ||
            token.Any(static character => !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
            throw Invalid();
        string[] parts = token.Split('.');
        if (parts.Length != 2) throw Invalid();
        try
        {
            byte[] body = FromBase64Url(parts[0]);
            byte[] tag = FromBase64Url(parts[1]);
            if (body.Length is < 1 or > 256 || tag.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(
                    HMACSHA256.HashData(_key, SignedBytes(purpose, body)), tag))
                throw Invalid();
            return StrictUtf8.GetString(body);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw Invalid();
        }
    }

    private static byte[] SignedBytes(string purpose, byte[] body) =>
        [.. "sqlobserver-api-cursor-v1\n"u8.ToArray(),
         .. StrictUtf8.GetBytes(purpose), (byte)'\n', .. body];

    private static void ValidatePurpose(string purpose)
    {
        if (string.IsNullOrEmpty(purpose) || purpose.Length > 64 ||
            purpose.Any(static character => !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
            throw new ArgumentException("API cursor purpose is invalid.", nameof(purpose));
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw Invalid();
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        byte[] decoded = Convert.FromBase64String(padded);
        if (!string.Equals(Base64Url(decoded), value, StringComparison.Ordinal))
            throw Invalid();
        return decoded;
    }

    private static ArgumentException Invalid() =>
        new("The API continuation cursor is invalid.", "cursor");
}
