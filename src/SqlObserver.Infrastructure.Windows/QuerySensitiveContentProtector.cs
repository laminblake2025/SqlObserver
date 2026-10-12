using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>
/// Protects bounded query text and plan bytes with a machine-protected key.
/// A target and payload kind are authenticated and included in the keyed
/// deduplication fingerprint, so one target cannot reuse another's payload.
/// </summary>
public sealed class QuerySensitiveContentProtector : IQuerySensitiveContentProtector, IDisposable
{
    private const int QueryTextMaximumBytes = 16 * 1024;
    private const int ExecutionPlanMaximumBytes = ProtectedSensitivePayload.MaximumCiphertextBytes;
    private static readonly byte[] FingerprintDomain = "SqlObserver.query-content.v1"u8.ToArray();
    private readonly byte[]? key;
    private readonly byte[]? fingerprintKey;
    private readonly string keyId = "unavailable";
    private bool disposed;

    public QuerySensitiveContentProtector(string? protectedKeyPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(protectedKeyPath)) return;
        try
        {
            var file = new FileInfo(protectedKeyPath);
            if (!file.Exists || file.Length > 4096 || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                file.Directory is not { } directory || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
            if (!IsRestricted(file.GetAccessControl()) || !IsRestricted(directory.GetAccessControl())) return;
            byte[] candidate = ProtectedData.Unprotect(File.ReadAllBytes(protectedKeyPath), null,
                DataProtectionScope.LocalMachine);
            if (candidate.Length != 32)
            {
                CryptographicOperations.ZeroMemory(candidate);
                return;
            }
            key = candidate;
            fingerprintKey = HMACSHA256.HashData(candidate, "SqlObserver.query-content.fingerprint-key.v1"u8);
            keyId = Convert.ToHexString(SHA256.HashData(candidate))[..32];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or SecurityException) { }
    }

    public bool IsAvailable => key is not null && fingerprintKey is not null && !disposed;

    [SupportedOSPlatform("windows")]
    private static bool IsRestricted(FileSystemSecurity acl)
    {
        if (!acl.AreAccessRulesProtected) return false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            string sid = rule.IdentityReference.Value;
            if (rule.AccessControlType == AccessControlType.Allow &&
                sid is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545" or "S-1-5-4") return false;
        }
        return true;
    }

    public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? currentKey = key;
        byte[]? currentFingerprintKey = fingerprintKey;
        if (currentKey is null || currentFingerprintKey is null || disposed)
            return ValueTask.FromResult<ProtectedSensitivePayload?>(null);
        Validate(targetId, kind, content.Length);
        Span<byte> context = stackalloc byte[17];
        targetId.TryWriteBytes(context);
        context[16] = (byte)kind;
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[content.Length];
        using (var aes = new AesGcm(currentKey, 16))
            aes.Encrypt(nonce, content.Span, ciphertext, tag, context);
        SensitivePayloadFingerprint fingerprint = Fingerprint(currentFingerprintKey, context, content.Span);
        return ValueTask.FromResult<ProtectedSensitivePayload?>(new ProtectedSensitivePayload(kind,
            fingerprint, "AES-256-GCM", keyId, nonce, tag, ciphertext));
    }

    public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);
        byte[]? currentKey = key;
        byte[]? currentFingerprintKey = fingerprintKey;
        if (currentKey is null || currentFingerprintKey is null || disposed ||
            payload.KeyIdentifier != keyId || payload.ProtectionAlgorithm != "AES-256-GCM")
            throw new CryptographicException("Query protection unavailable.");
        Validate(targetId, payload.Kind, payload.CiphertextLengthBytes);
        Span<byte> context = stackalloc byte[17];
        targetId.TryWriteBytes(context);
        context[16] = (byte)payload.Kind;
        byte[] ciphertext = payload.GetCiphertext();
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(currentKey, 16);
            aes.Decrypt(payload.GetNonce(), ciphertext, payload.GetAuthenticationTag(), plaintext, context);
            SensitivePayloadFingerprint actual = Fingerprint(currentFingerprintKey, context, plaintext);
            if (!CryptographicOperations.FixedTimeEquals(actual.ToArray(), payload.Fingerprint.ToArray()))
                throw new CryptographicException("Query payload fingerprint mismatch.");
            return ValueTask.FromResult(plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static SensitivePayloadFingerprint Fingerprint(byte[] key, ReadOnlySpan<byte> context,
        ReadOnlySpan<byte> content)
    {
        using IncrementalHash hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hash.AppendData(FingerprintDomain);
        hash.AppendData(context);
        hash.AppendData(content);
        byte[] digest = hash.GetHashAndReset();
        try { return new SensitivePayloadFingerprint(digest); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private static void Validate(Guid targetId, SensitivePayloadKind kind, int length)
    {
        if (targetId == Guid.Empty || kind is not (SensitivePayloadKind.QueryText or SensitivePayloadKind.ExecutionPlan) ||
            length < 1 || length > (kind == SensitivePayloadKind.QueryText ? QueryTextMaximumBytes : ExecutionPlanMaximumBytes))
            throw new CryptographicException("Invalid protected query content.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        if (fingerprintKey is not null) CryptographicOperations.ZeroMemory(fingerprintKey);
    }
}
