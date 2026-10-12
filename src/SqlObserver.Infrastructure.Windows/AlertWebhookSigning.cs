using System.Globalization;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;

namespace SqlObserver.Infrastructure.Windows;

public interface IAlertWebhookConfigurationResolver
{
    AlertWebhookConfiguration ResolveWebhook(string configurationReference, long? revision = null, string? digest = null);
}

/// <summary>Transfers ownership of a fresh key buffer to the configuration snapshot.</summary>
public interface IWebhookSigningKeyProvider
{
    byte[] ReadKey(string protectedKeyPath);
}

public sealed class DpapiWebhookSigningKeyProvider : IWebhookSigningKeyProvider
{
    public byte[] ReadKey(string protectedKeyPath)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(protectedKeyPath))
            throw new InvalidOperationException("A Windows protected signing key is required.");
        try
        {
            var file = new FileInfo(protectedKeyPath);
            if (!file.Exists || file.Length is < 1 or > 4096 || file.Directory is not { } directory ||
                !Restricted(file.GetAccessControl()) || !Restricted(directory.GetAccessControl()))
                throw new InvalidOperationException("Signing key protection is invalid.");
            for (DirectoryInfo? parent = directory; parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Signing key protection is invalid.");
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Signing key protection is invalid.");
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 4096) throw new InvalidOperationException("Signing key protection is invalid.");
            byte[] protectedBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(protectedBytes);
            byte[] key;
            try { key = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("Signing key protection is invalid.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or SecurityException)
        {
            // Provider/path/key details must never enter an administrative error or log.
            throw new InvalidOperationException("Signing key is unavailable.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Restricted(FileSystemSecurity acl) => acl.AreAccessRulesProtected &&
        !acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545" or "S-1-5-4");
}

/// <summary>One destination snapshot. Disposing it erases its private signing key.</summary>
public sealed class AlertWebhookConfiguration : IDisposable
{
    private readonly byte[]? key;
    private bool disposed;
    internal AlertWebhookConfiguration(Uri uri, string allowlist, string? keyId, byte[]? key)
    {
        Uri = uri; Allowlist = allowlist; KeyId = keyId; this.key = key;
    }
    public Uri Uri { get; }
    public string Allowlist { get; }
    public string? KeyId { get; }
    public string ConfigurationCanonical
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string canonical = $"kind=https-webhook|uri={Uri.AbsoluteUri}|host={Uri.Host}|port={Uri.Port}|path={Uri.AbsolutePath}|allowlist={Allowlist}|proxy=false|redirect=false|tls=https";
            return key is null ? canonical : canonical + $"|signing=hmac-sha256-v1|keyId={KeyId}|keyDigest={Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()}";
        }
    }
    public string ConfigurationDigest(long revision) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{ConfigurationCanonical}|revision={revision}"))).ToLowerInvariant();

    public void Sign(HttpRequestMessage request, ReadOnlySpan<byte> payload, Guid deliveryId, DateTimeOffset sentAtUtc)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (key is null) return;
        if (deliveryId == Guid.Empty || request.Method != HttpMethod.Post || request.RequestUri != Uri)
            throw new ArgumentException("Webhook signature identity is invalid.");
        string timestamp = sentAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string delivery = deliveryId.ToString("D");
        byte[] prefix = Encoding.UTF8.GetBytes($"sqlobserver.webhook.v1\nPOST\n{Uri.AbsoluteUri}\n{KeyId}\n{timestamp}\n{delivery}\n");
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(prefix); hmac.AppendData(payload);
        byte[] signature = hmac.GetHashAndReset();
        try
        {
            request.Headers.Add("X-SqlObserver-Key-Id", KeyId);
            request.Headers.Add("X-SqlObserver-Timestamp", timestamp);
            request.Headers.Add("X-SqlObserver-Delivery-Id", delivery);
            request.Headers.Add("X-SqlObserver-Signature", "v1=" + Convert.ToHexString(signature).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }
    public void Dispose()
    {
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        disposed = true;
    }
}
