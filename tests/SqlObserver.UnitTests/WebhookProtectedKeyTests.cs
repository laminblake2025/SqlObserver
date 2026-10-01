using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class WebhookProtectedKeyTests
{
    [Fact]
    public void ProtectedKeyRequiresMachineProtectionRestrictedFileAndDirectoryAndExactly32Bytes()
    {
        var provider = new DpapiWebhookSigningKeyProvider();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidOperationException>(() => provider.ReadKey("/nonwindows/key.dpapi"));
            return;
        }
        string directory = Path.Combine(Path.GetTempPath(), "SqlObserver-webhook-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "key.dpapi");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var directoryAcl = new DirectorySecurity(); directoryAcl.SetAccessRuleProtection(true, false);
            directoryAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(directoryAcl);
            File.WriteAllBytes(path, ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine));
            var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false);
            fileAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(fileAcl);
            byte[] loaded = provider.ReadKey(path);
            try { Assert.Equal(key, loaded); } finally { CryptographicOperations.ZeroMemory(loaded); }
            var broad = new SecurityIdentifier("S-1-5-32-545");
            fileAcl.AddAccessRule(new FileSystemAccessRule(broad, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(fileAcl);
            Assert.Throws<InvalidOperationException>(() => provider.ReadKey(path));
            fileAcl.PurgeAccessRules(broad); new FileInfo(path).SetAccessControl(fileAcl);
            directoryAcl.AddAccessRule(new FileSystemAccessRule(broad, FileSystemRights.Read, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(directoryAcl);
            Assert.Throws<InvalidOperationException>(() => provider.ReadKey(path));
            directoryAcl.PurgeAccessRules(broad); new DirectoryInfo(directory).SetAccessControl(directoryAcl);
            File.WriteAllBytes(path, ProtectedData.Protect(new byte[31], null, DataProtectionScope.LocalMachine));
            Assert.Throws<InvalidOperationException>(() => provider.ReadKey(path));
            File.WriteAllBytes(path, key); // A plaintext key file is never accepted.
            Assert.Throws<InvalidOperationException>(() => provider.ReadKey(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            File.Delete(path); Directory.Delete(directory);
        }
    }
}
