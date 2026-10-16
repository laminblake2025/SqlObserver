using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

namespace SqlObserver.TestSetup;

[SupportedOSPlatform("windows")]
internal static class WindowsHost
{
    private const uint ScmConnect = 1, ScmCreate = 2, ServiceAll = 0xF01FF;
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdminSid = new("S-1-5-32-544");

    public static void AssertProtectedInstallRoot(string root)
    {
        SetupPolicy.AssertOrdinaryPath(root);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
        if (!root.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("Install the test package below Program Files.");
        for (DirectoryInfo? dir = new(root); dir is not null && dir.FullName.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase); dir = dir.Parent)
        {
            foreach (FileSystemAccessRule rule in dir.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
                    FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                    rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544" or "S-1-3-0") &&
                    rule.IdentityReference.Value != "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
                    throw new SetupException("The installation directory permits writes by an unexpected identity.");
            }
        }
    }

    public static string AccountSid(string name)
    {
        if (name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) return "S-1-5-18";
        if (name.Equals(@"NT AUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase)) return "S-1-5-20";
        if (name.Equals(@"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase)) return "S-1-5-19";
        return ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
    }

    public static string OperatorGroupSid(string name)
    {
        uint bytes = 0, domainLength = 0;
        _ = LookupAccountName(null, name, null, ref bytes, null, ref domainLength, out _);
        if (bytes is 0 or > 1024 || domainLength > 1024) throw new SetupException("The operator group could not be resolved.");
        byte[] sid = new byte[bytes];
        var domain = new char[domainLength];
        if (!LookupAccountName(null, name, sid, ref bytes, domain, ref domainLength, out int kind)) throw new Win32Exception();
        if (kind is not (2 or 4)) throw new SetupException("Select an existing dedicated Windows group, rather than a user account.");
        return new SecurityIdentifier(sid, 0).Value;
    }

    public static void ProtectDirectory(string path, params string[] readerSids)
    {
        SetupPolicy.AssertOrdinaryPath(path);
        Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(AdminSid);
        foreach (SecurityIdentifier sid in new[] { SystemSid, AdminSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (string sid in readerSids.Distinct(StringComparer.Ordinal))
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    public static void CreateFreshDataRoot(string path)
    {
        SetupPolicy.AssertOrdinaryPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("Choose a fresh direct child of ProgramData.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(AdminSid);
        foreach (SecurityIdentifier sid in new[] { SystemSid, AdminSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        byte[] descriptor = acl.GetSecurityDescriptorBinaryForm();
        GCHandle pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            // Atomic create with a protected DACL: never adopt a directory planted during the review prompt.
            if (!CreateDirectory(path, ref attributes)) throw new SetupException("Cannot create a fresh protected data directory. Existing paths are never adopted.");
        }
        finally { pin.Free(); }
    }

    public static string CreateOperatorGroup()
    {
        // Do not silently reuse a pre-existing group or change its members.
        var info = new GroupInfo { Name = SetupPolicy.OperatorGroup, Comment = "SQL Observer unsigned test deployment operators" };
        int code = NetLocalGroupAdd(null, 1, ref info, out _);
        if (code != 0) throw new SetupException("The test operator group already exists or could not be created. Choose an existing dedicated group instead.");
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        byte[] sid = new byte[current.User!.BinaryLength];
        current.User.GetBinaryForm(sid, 0);
        GCHandle pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
        try
        {
            var member = new GroupMember { Sid = pinned.AddrOfPinnedObject() };
            if (NetLocalGroupAddMembers(null, SetupPolicy.OperatorGroup, 0, ref member, 1) != 0)
                throw new SetupException("The operator group was created, but membership failed. Inspect the group before retrying.");
        }
        finally { pinned.Free(); }
        return AccountSid(Environment.MachineName + "\\" + SetupPolicy.OperatorGroup);
    }

    public static bool ServiceExists(string name)
    {
        nint manager = OpenSCManager(null, null, ScmConnect);
        if (manager == 0) throw new Win32Exception();
        try
        {
            nint service = OpenService(manager, name, 1);
            if (service == 0)
            {
                if (Marshal.GetLastWin32Error() == 1060) return false;
                throw new Win32Exception();
            }
            CloseServiceHandle(service);
            return true;
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void CreateService(string name, string command, string account, string? password)
    {
        nint manager = OpenSCManager(null, null, ScmCreate);
        if (manager == 0) throw new Win32Exception();
        try
        {
            nint service = CreateServiceNative(manager, name, name == SetupPolicy.ServerService ? "SQL Observer Test Server" : "SQL Observer Test Collector",
                ServiceAll, 0x10, 2, 1, command, null, 0, null, account, password);
            if (service == 0) throw new Win32Exception();
            try
            {
                // Delayed automatic start and bounded restart; Windows grants virtual accounts service logon.
                var delayed = new DelayedAutoStart { Enabled = true };
                if (!ChangeServiceConfig2(service, 3, ref delayed)) throw new Win32Exception();
                var actions = new[] { new ServiceAction { Type = 1, Delay = 10000 }, new ServiceAction { Type = 1, Delay = 30000 }, new ServiceAction { Type = 1, Delay = 60000 }, new ServiceAction() };
                int size = Marshal.SizeOf<ServiceAction>();
                nint buffer = Marshal.AllocHGlobal(size * actions.Length);
                try
                {
                    for (int i = 0; i < actions.Length; i++) Marshal.StructureToPtr(actions[i], buffer + i * size, false);
                    var failure = new FailureActions { ResetPeriod = 86400, Count = (uint)actions.Length, Actions = buffer };
                    if (!SetFailureActions(service, 2, ref failure)) throw new Win32Exception();
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void Start(string name)
    {
        WithService(name, service => { if (!StartService(service, 0, 0) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception(); });
    }

    public static void AssertRunning(string name) => WithService(name, service =>
    {
        if (!QueryServiceStatusEx(service, 0, out ServiceProcessStatus status, (uint)Marshal.SizeOf<ServiceProcessStatus>(), out _)) throw new Win32Exception();
        if (status.State != 4) throw new SetupException("A test service stopped during startup. Inspect its Windows Application event log.");
        nint process = OpenProcess(0x1000, false, status.ProcessId);
        if (process == 0) throw new Win32Exception();
        try
        {
            if (!OpenProcessToken(process, 8, out nint token)) throw new Win32Exception();
            try
            {
                using var identity = new WindowsIdentity(token);
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem)
                    throw new SetupException("The test service is running with administrator authority. Use a dedicated non-admin service identity.");
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    });

    public static void RemoveOwnedServices(string appRoot)
    {
        foreach (string name in new[] { SetupPolicy.CollectorService, SetupPolicy.ServerService })
        {
            if (!ServiceExists(name)) continue;
            WithService(name, service =>
            {
                QueryServiceConfig(service, 0, 0, out uint bytes);
                if (bytes is 0 or > 65536) throw new SetupException("Cannot verify test service ownership.");
                nint buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    if (!QueryServiceConfig(service, buffer, bytes, out _)) throw new Win32Exception();
                    var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    string command = Marshal.PtrToStringUni(config.BinaryPath) ?? "";
                    string component = name == SetupPolicy.ServerService ? "Server" : "Collector";
                    string prefix = "\"" + Path.Combine(appRoot, component, $"SqlObserver.{component}.exe") + "\" --contentRoot \"";
                    if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !command.EndsWith('"'))
                        throw new SetupException("Refusing to remove a service whose executable does not belong to this installation.");
                }
                finally { Marshal.FreeHGlobal(buffer); }
                if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062) throw new Win32Exception();
                DateTime deadline = DateTime.UtcNow.AddSeconds(45);
                while (true)
                {
                    if (!QueryServiceStatus(service, out ServiceStatus status)) throw new Win32Exception();
                    if (status.State == 1) break;
                    if (DateTime.UtcNow >= deadline) throw new SetupException("The test service did not stop; files were preserved.");
                    Thread.Sleep(250);
                }
                if (!DeleteService(service)) throw new Win32Exception();
            });
        }
    }

    private static void WithService(string name, Action<nint> action)
    {
        nint manager = OpenSCManager(null, null, ScmConnect);
        if (manager == 0) throw new Win32Exception();
        try
        {
            nint service = OpenService(manager, name, ServiceAll);
            if (service == 0) throw new Win32Exception();
            try { action(service); } finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void GrantServiceLogon(string account)
    {
        string sidValue = AccountSid(account);
        if (sidValue is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || sidValue.StartsWith("S-1-5-32-", StringComparison.Ordinal))
            throw new SetupException("Use a dedicated domain service account or gMSA.");
        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        uint code = LsaOpenPolicy(0, ref attributes, 0x810, out nint policy);
        if (code != 0) throw new Win32Exception((int)LsaNtStatusToWinError(code));
        byte[] sid = new byte[new SecurityIdentifier(sidValue).BinaryLength];
        new SecurityIdentifier(sidValue).GetBinaryForm(sid, 0);
        GCHandle pin = GCHandle.Alloc(sid, GCHandleType.Pinned);
        nint text = Marshal.StringToHGlobalUni("SeServiceLogonRight");
        try
        {
            var right = new LsaString { Buffer = text, Length = 38, MaximumLength = 40 };
            code = LsaAddAccountRights(policy, pin.AddrOfPinnedObject(), ref right, 1);
            if (code != 0) throw new Win32Exception((int)LsaNtStatusToWinError(code));
        }
        finally { Marshal.FreeHGlobal(text); pin.Free(); _ = LsaClose(policy); }
    }

    public static X509Certificate2 CreateTlsCertificate(string dnsName)
    {
        using RSA rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=" + dnsName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (string name in new[] { dnsName, Environment.MachineName, "localhost" }.Distinct(StringComparer.OrdinalIgnoreCase)) san.AddDnsName(name);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365));
    }

    public static void WriteProtectedKey(string path)
    {
        byte[] raw = RandomNumberGenerator.GetBytes(32);
        try { using FileStream file = new(path, FileMode.CreateNew); file.Write(ProtectedData.Protect(raw, null, DataProtectionScope.LocalMachine)); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct GroupInfo { [MarshalAs(UnmanagedType.LPWStr)] public string Name; [MarshalAs(UnmanagedType.LPWStr)] public string Comment; }
    [StructLayout(LayoutKind.Sequential)] private struct GroupMember { public nint Sid; }
    [StructLayout(LayoutKind.Sequential)] private struct DelayedAutoStart { [MarshalAs(UnmanagedType.Bool)] public bool Enabled; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceProcessStatus { public uint Type, State, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceAction { public uint Type, Delay; }
    [StructLayout(LayoutKind.Sequential)] private struct FailureActions { public uint ResetPeriod; public nint RebootMessage, Command; public uint Count; public nint Actions; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig { public uint Type, StartType, ErrorControl; public nint BinaryPath, LoadOrderGroup; public uint TagId; public nint Dependencies, StartName, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct LsaObjectAttributes { public int Length; public nint RootDirectory, ObjectName; public uint Attributes; public nint SecurityDescriptor, SecurityQuality; }
    [StructLayout(LayoutKind.Sequential)] private struct LsaString { public ushort Length, MaximumLength; public nint Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupAdd(string? server, int level, ref GroupInfo info, out int error);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupAddMembers(string? server, string group, int level, ref GroupMember member, int count);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupAccountName(string? system, string name, byte[]? sid, ref uint sidLength, [Out] char[]? domain, ref uint domainLength, out int kind);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateServiceNative(nint manager, string name, string display, uint access, uint type, uint start, uint error, string path, string? group, nint tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2(nint service, uint level, ref DelayedAutoStart data);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFailureActions(nint service, uint level, ref FailureActions data);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(nint service, int count, nint args);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(nint service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(nint service, uint level, out ServiceProcessStatus status, uint bytes, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(nint service, nint data, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(nint service);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(nint system, ref LsaObjectAttributes attributes, uint access, out nint policy);
    [DllImport("advapi32.dll")] private static extern uint LsaAddAccountRights(nint policy, nint sid, ref LsaString rights, uint count);
    [DllImport("advapi32.dll")] private static extern uint LsaNtStatusToWinError(uint code);
    [DllImport("advapi32.dll")] private static extern uint LsaClose(nint policy);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
