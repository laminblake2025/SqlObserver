using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>The configuration contains only a file path. The file contains a machine-DPAPI protected AES key.</summary>
public sealed class LiveActivityProtector : ILiveActivityProtector, IDisposable
{
    private readonly byte[]? key;
    private readonly string keyId = "unavailable";

    public LiveActivityProtector(string? protectedKeyPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(protectedKeyPath)) return;
        try
        {
            var file = new FileInfo(protectedKeyPath);
            if (!file.Exists || file.Length > 4096 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var acl = file.GetAccessControl();
            if (!acl.AreAccessRulesProtected) return;
            // Provisioning admits administrators, SYSTEM, and the exact service identities only.
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                string sid = rule.IdentityReference.Value;
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (sid is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545" or "S-1-5-4")) return;
            }
            byte[] candidate = ProtectedData.Unprotect(File.ReadAllBytes(protectedKeyPath), null, DataProtectionScope.LocalMachine);
            if (candidate.Length != 32) { CryptographicOperations.ZeroMemory(candidate); return; }
            key = candidate;
            keyId = Convert.ToHexString(SHA256.HashData(key))[..32];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
    }

    public bool IsAvailable => key is not null;
    public ProtectedSensitivePayload Protect(Guid targetId, ReadOnlySpan<byte> text)
    {
        if (key is null || text.Length is < 1 or > 16384) throw new CryptographicException("Query protection unavailable.");
        byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], ciphertext = new byte[text.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, text, ciphertext, tag, targetId.ToByteArray());
        // A keyed fingerprint prevents dictionary lookup against captured SQL.
        return new(SensitivePayloadKind.QueryText, new SensitivePayloadFingerprint(HMACSHA256.HashData(key, text)),
            "AES-256-GCM", keyId, nonce, tag, ciphertext);
    }

    public byte[] Unprotect(Guid targetId, ProtectedSensitivePayload payload)
    {
        if (key is null || payload.KeyIdentifier != keyId || payload.ProtectionAlgorithm != "AES-256-GCM" ||
            payload.Kind != SensitivePayloadKind.QueryText) throw new CryptographicException("Query protection unavailable.");
        byte[] ciphertext = payload.GetCiphertext();
        if (ciphertext.Length > 16384) throw new CryptographicException("Invalid query payload.");
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        try { aes.Decrypt(payload.GetNonce(), ciphertext, payload.GetAuthenticationTag(), plaintext, targetId.ToByteArray()); return plaintext; }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }

    public void Dispose() { if (key is not null) CryptographicOperations.ZeroMemory(key); }
}
