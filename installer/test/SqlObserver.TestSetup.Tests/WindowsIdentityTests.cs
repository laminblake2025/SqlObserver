using System.Runtime.Versioning;
using System.Security.Principal;

namespace SqlObserver.TestSetup.Tests;

public sealed class WindowsIdentityTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void NativeGroupLookupDistinguishesGroupsFromUserAccounts()
    {
        if (!OperatingSystem.IsWindows()) return;
        string group = new SecurityIdentifier("S-1-5-32-544").Translate(typeof(NTAccount)).Value;
        Assert.Equal("S-1-5-32-544", WindowsHost.OperatorGroupSid(group));
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        Assert.Throws<SetupException>(() => WindowsHost.OperatorGroupSid(current.Name));
        Assert.Equal("S-1-5-20", WindowsHost.AccountSid(@"NT AUTHORITY\NetworkService"));
        Assert.Equal("S-1-5-19", WindowsHost.AccountSid(@"NT AUTHORITY\LocalService"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void NativeServiceLookupDoesNotInventAMissingService()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(WindowsHost.ServiceExists("SqlObserver-absent-" + Guid.NewGuid().ToString("N")));
    }
}
