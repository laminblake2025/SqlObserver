using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class QuerySensitiveContentProtectorTests
{
    [Fact]
    public async Task MissingKeyFailsClosed()
    {
        using var protector = new QuerySensitiveContentProtector(null);
        Assert.False(protector.IsAvailable);
        Assert.Null(await protector.ProtectAsync(Guid.NewGuid(), SensitivePayloadKind.QueryText,
            "select 1"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task ProtectedContentIsTargetAndKindBoundAndRejectsTampering()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "SqlObserver-query-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "key.dpapi");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var directoryAcl = new DirectorySecurity();
            directoryAcl.SetAccessRuleProtection(true, false);
            directoryAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directoryAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),
                FileSystemRights.Read, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(directoryAcl);
            File.WriteAllBytes(path, ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine));
            using (var broadDirectory = new QuerySensitiveContentProtector(path))
                Assert.False(broadDirectory.IsAvailable);
            directoryAcl.PurgeAccessRules(new SecurityIdentifier("S-1-5-32-545"));
            new DirectoryInfo(directory).SetAccessControl(directoryAcl);
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),
                FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
            using (var broadAccess = new QuerySensitiveContentProtector(path))
                Assert.False(broadAccess.IsAvailable);
            acl.PurgeAccessRules(new SecurityIdentifier("S-1-5-32-545"));
            new FileInfo(path).SetAccessControl(acl);

            using var protector = new QuerySensitiveContentProtector(path);
            Assert.True(protector.IsAvailable);
            Guid target = Guid.NewGuid();
            Guid otherTarget = Guid.NewGuid();
            byte[] content = "select 'private value'"u8.ToArray();
            var first = Assert.IsType<ProtectedSensitivePayload>(await protector.ProtectAsync(
                target, SensitivePayloadKind.QueryText, content, CancellationToken.None));
            var repeat = Assert.IsType<ProtectedSensitivePayload>(await protector.ProtectAsync(
                target, SensitivePayloadKind.QueryText, content, CancellationToken.None));
            var other = Assert.IsType<ProtectedSensitivePayload>(await protector.ProtectAsync(
                otherTarget, SensitivePayloadKind.QueryText, content, CancellationToken.None));
            var plan = Assert.IsType<ProtectedSensitivePayload>(await protector.ProtectAsync(
                target, SensitivePayloadKind.ExecutionPlan, content, CancellationToken.None));

            Assert.Equal(content, await protector.UnprotectAsync(target, first, CancellationToken.None));
            Assert.Equal(content, await protector.UnprotectAsync(target, plan, CancellationToken.None));
            Assert.Equal(first.Fingerprint, repeat.Fingerprint);
            Assert.False(first.GetNonce().AsSpan().SequenceEqual(repeat.GetNonce()));
            Assert.False(first.GetCiphertext().AsSpan().SequenceEqual(repeat.GetCiphertext()));
            Assert.NotEqual(first.Fingerprint, other.Fingerprint);
            Assert.NotEqual(first.Fingerprint, plan.Fingerprint);
            await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
                await protector.UnprotectAsync(otherTarget, first, CancellationToken.None));

            byte[] changedCiphertext = first.GetCiphertext();
            changedCiphertext[0] ^= 1;
            var tampered = new ProtectedSensitivePayload(first.Kind, first.Fingerprint,
                first.ProtectionAlgorithm, first.KeyIdentifier, first.GetNonce(),
                first.GetAuthenticationTag(), changedCiphertext);
            await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
                await protector.UnprotectAsync(target, tampered, CancellationToken.None));
            var wrongFingerprint = new ProtectedSensitivePayload(first.Kind, other.Fingerprint,
                first.ProtectionAlgorithm, first.KeyIdentifier, first.GetNonce(),
                first.GetAuthenticationTag(), first.GetCiphertext());
            await Assert.ThrowsAsync<CryptographicException>(async () =>
                await protector.UnprotectAsync(target, wrongFingerprint, CancellationToken.None));
            await Assert.ThrowsAsync<CryptographicException>(async () => await protector.ProtectAsync(target,
                SensitivePayloadKind.QueryText, new byte[16 * 1024 + 1], CancellationToken.None));
            await Assert.ThrowsAsync<CryptographicException>(async () => await protector.ProtectAsync(target,
                SensitivePayloadKind.ExecutionPlan,
                new byte[ProtectedSensitivePayload.MaximumCiphertextBytes + 1], CancellationToken.None));
            await Assert.ThrowsAsync<CryptographicException>(async () => await protector.ProtectAsync(Guid.Empty,
                SensitivePayloadKind.QueryText, content, CancellationToken.None));
            protector.Dispose();
            Assert.False(protector.IsAvailable);
            Assert.Null(await protector.ProtectAsync(target, SensitivePayloadKind.QueryText,
                content, CancellationToken.None));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
