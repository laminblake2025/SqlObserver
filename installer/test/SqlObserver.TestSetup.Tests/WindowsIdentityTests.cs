using System.Runtime.Versioning;
using System.Security.Principal;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace SqlObserver.TestSetup.Tests;

public sealed class WindowsIdentityTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void QueryOnlyProcessTokenMatchesWindowsPrincipalMembership()
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        bool expected = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator) || current.IsSystem;
        Assert.Equal(expected, WindowsHost.IsProcessAdministrator((uint)Environment.ProcessId));
    }

    [Theory]
    [InlineData(0U, false)]
    [InlineData(4U, true)]
    [InlineData(7U, true)]
    [InlineData(16U, false)]
    [InlineData(20U, false)]
    [SupportedOSPlatform("windows")]
    public void AdministratorGroupMustBeEnabledAndCannotBeDenyOnly(uint attributes, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, WindowsProcessIdentity.IsEnabledAdministrator(new SecurityIdentifier("S-1-5-32-544"), attributes));
        Assert.False(WindowsProcessIdentity.IsEnabledAdministrator(new SecurityIdentifier("S-1-5-32-545"), attributes));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void MissingProcessKeepsNativeOperationAndErrorNumber()
    {
        if (!OperatingSystem.IsWindows()) return;
        SetupWin32Exception error = Assert.Throws<SetupWin32Exception>(() => WindowsHost.IsProcessAdministrator(uint.MaxValue));
        Assert.Equal(87, error.NativeErrorCode);
        Assert.Equal(SetupNativeOperation.OpenProcess, error.Operation);
        SetupFailureDiagnostic diagnostic = SetupFailureDiagnostic.Create("server-identity-check", error);
        Assert.Equal(87, diagnostic.NativeErrorCode);
        Assert.Equal("OpenProcess", diagnostic.NativeOperation);
    }

    [Fact]
    public void Win32DiagnosticIncludesNumericErrorWithoutProviderMessage()
    {
        var error = new System.ComponentModel.Win32Exception(5, "secret-password; Host=private; SQL payload");
        string json = JsonSerializer.Serialize(SetupFailureDiagnostic.Create("server-identity-check", error));
        Assert.Contains("\"NativeErrorCode\":5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SQL payload", json, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DebugPrivilegeIsRestoredOrFailsClosedWhenNotAssigned()
    {
        if (!OperatingSystem.IsWindows()) return;
        uint? before = DebugPrivilegeAttributes();
        if (before is null)
        {
            SetupWin32Exception error = Assert.Throws<SetupWin32Exception>(() => WindowsProcessIdentity.DebugPrivilegeScope.Enable());
            Assert.Equal(1300, error.NativeErrorCode); // ERROR_NOT_ALL_ASSIGNED, not a successful enable.
            Assert.Equal(SetupNativeOperation.EnableDebugPrivilege, error.Operation);
            Assert.Null(DebugPrivilegeAttributes());
            return;
        }
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = WindowsProcessIdentity.DebugPrivilegeScope.Enable();
            Assert.True((DebugPrivilegeAttributes() & 2) != 0);
            using (WindowsProcessIdentity.DebugPrivilegeScope.Enable()) { }
            Assert.True((DebugPrivilegeAttributes() & 2) != 0); // Nested scope preserves enabled state.
            throw new InvalidOperationException("fixture");
        }));
        Assert.Equal(before, DebugPrivilegeAttributes());
    }

    [InstallerIdentityFact]
    [SupportedOSPlatform("windows")]
    public void CrossAccountServiceInspectionAndSystemRejectionRestoreInspectorPrivilege()
    {
        uint? before = DebugPrivilegeAttributes();
        WindowsHost.AssertRunning("nsi"); // Existing LocalService process; no service mutations.
        Assert.Equal(before, DebugPrivilegeAttributes());
        Process[] systemProcesses = Process.GetProcessesByName("services");
        try
        {
            Process system = Assert.Single(systemProcesses);
            Assert.True(WindowsHost.IsProcessAdministrator((uint)system.Id));
            Assert.Equal(before, DebugPrivilegeAttributes());
        }
        finally { foreach (Process process in systemProcesses) process.Dispose(); }
    }

    [SupportedOSPlatform("windows")]
    private static uint? DebugPrivilegeAttributes()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.True(LookupPrivilegeValue(null, "SeDebugPrivilege", out long debug));
        _ = GetTokenInformation(identity.AccessToken.DangerousGetHandle(), 3, 0, 0, out uint bytes);
        Assert.Equal(122, Marshal.GetLastWin32Error());
        nint buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            Assert.True(GetTokenInformation(identity.AccessToken.DangerousGetHandle(), 3, buffer, bytes, out _));
            int count = Marshal.ReadInt32(buffer);
            for (int index = 0; index < count; index++)
            {
                nint entry = buffer + 4 + index * 12; // DWORD count, followed by LUID_AND_ATTRIBUTES entries.
                if (Marshal.ReadInt64(entry) == debug) return (uint)Marshal.ReadInt32(entry, 8);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int kind, nint buffer, uint bytes, out uint needed);

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

public sealed class InstallerIdentityFactAttribute : FactAttribute
{
    public InstallerIdentityFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SQLOBSERVER_INSTALLER_IDENTITY_TEST") != "1")
            Skip = "Requires explicitly enabled cross-account Windows service token inspection tests.";
        else
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                Skip = "Cross-account service inspection requires an elevated Windows administrator token.";
        }
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
