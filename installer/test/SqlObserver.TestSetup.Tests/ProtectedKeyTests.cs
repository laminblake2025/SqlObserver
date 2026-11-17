using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.Windows;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;

namespace SqlObserver.TestSetup.Tests;

public sealed class ProtectedKeyTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ProvisionedKeysHaveProtectedAclsAndBothRuntimeProtectorsCanReadThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "SqlObserver-key-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(acl);
            string path = Path.Combine(root, "test.dpapi");
            WindowsHost.WriteProtectedKey(path);
            Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
            using var live = new LiveActivityProtector(path);
            using var query = new QuerySensitiveContentProtector(path);
            Assert.True(live.IsAvailable);
            Assert.True(query.IsAvailable);
            Guid target = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("SELECT 1 /* synthetic installer fixture */");
            ProtectedSensitivePayload livePayload = live.Protect(target, content);
            Assert.Equal(content, live.Unprotect(target, livePayload));
            ProtectedSensitivePayload? queryPayload = await query.ProtectAsync(target, SensitivePayloadKind.QueryText, content, CancellationToken.None);
            Assert.NotNull(queryPayload);
            Assert.Equal(content, await query.UnprotectAsync(target, queryPayload, CancellationToken.None));
            Assert.Throws<IOException>(() => WindowsHost.WriteProtectedKey(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
