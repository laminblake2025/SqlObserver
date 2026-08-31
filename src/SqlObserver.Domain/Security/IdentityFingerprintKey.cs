namespace SqlObserver.Domain.Security;

/// <summary>Process-local handle for the secret used to derive opaque identities.</summary>
public sealed class IdentityFingerprintKey
{
    public const int RequiredLength = 32;
    private readonly byte[] value;

    public IdentityFingerprintKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != RequiredLength) throw new ArgumentException("The identity fingerprint key must be exactly 32 bytes.", nameof(key));
        value = key.ToArray();
    }

    internal ReadOnlySpan<byte> Span => value;

}

/// <summary>Supplies the process secret without exposing or persisting key material.</summary>
public interface IIdentityFingerprintKeyProvider
{
    IdentityFingerprintKey GetRequiredKey();
}

/// <summary>Shared fail-closed provider; the caller supplies a secret-backed configuration lookup.</summary>
public sealed class ConfigurationIdentityFingerprintKeyProvider : IIdentityFingerprintKeyProvider
{
    private readonly Func<string?> read;
    private readonly Lazy<IdentityFingerprintKey> key;

    public ConfigurationIdentityFingerprintKeyProvider(Func<string?> read)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        key = new Lazy<IdentityFingerprintKey>(Load);
    }

    public IdentityFingerprintKey GetRequiredKey() => key.Value;

    private IdentityFingerprintKey Load()
    {
        string? configured = read();
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("SqlObserver:IdentityFingerprintKey must be configured in the collector/server secret store.");
        try
        {
            byte[] bytes = configured.Length == IdentityFingerprintKey.RequiredLength * 2
                ? Convert.FromHexString(configured)
                : Convert.FromBase64String(configured);
            return new IdentityFingerprintKey(bytes);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new InvalidOperationException("SqlObserver:IdentityFingerprintKey is not a valid 32-byte secret.", exception);
        }
    }
}
