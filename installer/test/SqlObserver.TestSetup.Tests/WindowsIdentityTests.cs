using System.Runtime.Versioning;
using System.Security.Principal;
using System.Diagnostics;
using System.Text.Json;

namespace SqlObserver.TestSetup.Tests;

public sealed class WindowsIdentityTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProcessIdentityMembershipCheckHasRequiredTokenAccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        bool expected = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator) || current.IsSystem;
        Assert.Equal(expected, WindowsHost.IsProcessAdministrator((uint)Environment.ProcessId));
    }

    [Fact]
    public void SetupFailureDiagnosticNeverSerializesExceptionDetails()
    {
        var error = new System.Security.SecurityException("secret-password; Host=private; SQL payload");
        string json = JsonSerializer.Serialize(SetupFailureDiagnostic.Create("server-identity-check", error));
        Assert.Contains("server-identity-check", json, StringComparison.Ordinal);
        Assert.Contains("SecurityException", json, StringComparison.Ordinal);
        Assert.Contains("0x", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SQL payload", json, StringComparison.Ordinal);
    }

    [InstallerEventLogFact]
    [SupportedOSPlatform("windows")]
    public void ApplicationEventSourceProvisioningIsIdempotentAndRejectsOtherLogs()
    {
        string source = "SqlObserver.SetupTest." + Guid.NewGuid().ToString("N");
        string conflict = "SqlObserver.SetupConflict." + Guid.NewGuid().ToString("N");
        bool sourceCreated = false, conflictCreated = false;
        try
        {
            WindowsHost.EnsureApplicationEventSource(source);
            sourceCreated = true;
            WindowsHost.EnsureApplicationEventSource(source);
            Assert.Equal("Application", EventLog.LogNameFromSourceName(source, "."));
            EventLog.WriteEntry(source, "SQL Observer disposable installer event-source verification.", EventLogEntryType.Information);
            EventLog.CreateEventSource(conflict, "System");
            conflictCreated = true;
            Assert.Throws<SetupException>(() => WindowsHost.EnsureApplicationEventSource(conflict));
            Assert.Equal("System", EventLog.LogNameFromSourceName(conflict, "."));
        }
        finally
        {
            if (sourceCreated) EventLog.DeleteEventSource(source);
            if (conflictCreated) EventLog.DeleteEventSource(conflict);
        }
    }
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

public sealed class InstallerEventLogFactAttribute : FactAttribute
{
    public InstallerEventLogFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SQLOBSERVER_INSTALLER_EVENTLOG_TEST") != "1")
            Skip = "Requires explicit permission for disposable Windows Event Log source registration tests.";
        else
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                Skip = "Windows Event Log source registration requires an elevated Windows administrator token.";
        }
    }
}
