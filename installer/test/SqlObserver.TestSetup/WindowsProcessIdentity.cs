using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SqlObserver.TestSetup;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessIdentity
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorSid = new("S-1-5-32-544");
    private static readonly object PrivilegeLock = new();

    internal static bool IsAdministrator(uint processId)
    {
        try { return QueryAdministrator(processId); }
        catch (SetupWin32Exception error) when (error.NativeErrorCode == 5 &&
            error.Operation is SetupNativeOperation.OpenProcess or SetupNativeOperation.OpenProcessToken)
        {
            // Cross-account service tokens may require SeDebugPrivilege even for an elevated
            // administrator. Enable only an already-assigned privilege and restore its exact
            // previous state before leaving this synchronous inspection. Never change a service.
            lock (PrivilegeLock)
            {
                using var privilege = DebugPrivilegeScope.Enable();
                return QueryAdministrator(processId);
            }
        }
    }

    private static bool QueryAdministrator(uint processId)
    {
        nint process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) throw Failure(SetupNativeOperation.OpenProcess);
        try
        {
            // Read SIDs and their attributes directly. WindowsPrincipal.IsInRole requires
            // token duplication; this check needs TOKEN_QUERY only.
            if (!OpenProcessToken(process, 0x0008, out nint token)) throw Failure(SetupNativeOperation.OpenProcessToken);
            try
            {
                nint user = ReadTokenInformation(token, 1, SetupNativeOperation.GetTokenUser, out _);
                try
                {
                    if (new SecurityIdentifier(Marshal.PtrToStructure<SidAndAttributes>(user).Sid).Equals(SystemSid)) return true;
                }
                finally { Marshal.FreeHGlobal(user); }
                nint groups = ReadTokenInformation(token, 2, SetupNativeOperation.GetTokenGroups, out uint bytes);
                try
                {
                    uint count = (uint)Marshal.ReadInt32(groups);
                    int offset = (int)Marshal.OffsetOf<TokenGroups>(nameof(TokenGroups.FirstGroup));
                    int size = Marshal.SizeOf<SidAndAttributes>();
                    if (bytes < offset || count > (bytes - offset) / size)
                        throw new SetupWin32Exception(13, SetupNativeOperation.GetTokenGroups); // ERROR_INVALID_DATA
                    for (int index = 0; index < count; index++)
                    {
                        var group = Marshal.PtrToStructure<SidAndAttributes>(groups + offset + index * size);
                        if (IsEnabledAdministrator(new SecurityIdentifier(group.Sid), group.Attributes)) return true;
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(groups); }
            }
            finally { _ = CloseHandle(token); }
        }
        finally { _ = CloseHandle(process); }
    }

    internal static bool IsEnabledAdministrator(SecurityIdentifier sid, uint attributes) =>
        sid.Equals(AdministratorSid) && (attributes & (0x0004 | 0x0010)) == 0x0004; // ENABLED, not USE_FOR_DENY_ONLY

    private static nint ReadTokenInformation(nint token, int kind, SetupNativeOperation operation, out uint bytes)
    {
        _ = GetTokenInformation(token, kind, 0, 0, out bytes);
        int code = Marshal.GetLastWin32Error();
        if (code != 122) throw new SetupWin32Exception(code, operation); // ERROR_INSUFFICIENT_BUFFER
        if (bytes is < 8 or > 1048576) throw new SetupWin32Exception(13, operation);
        nint buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (!GetTokenInformation(token, kind, buffer, bytes, out _)) throw Failure(operation);
            return buffer;
        }
        catch { Marshal.FreeHGlobal(buffer); throw; }
    }

    internal sealed class DebugPrivilegeScope : IDisposable
    {
        private nint token;
        private TokenPrivilege previous;

        private DebugPrivilegeScope(nint token, TokenPrivilege previous) { this.token = token; this.previous = previous; }

        internal static DebugPrivilegeScope Enable()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out nint token))
                throw Failure(SetupNativeOperation.OpenInspectorToken);
            try
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out Luid luid)) throw Failure(SetupNativeOperation.LookupDebugPrivilege);
                var desired = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 0x0002 };
                bool adjusted = AdjustTokenPrivileges(token, false, ref desired, (uint)Marshal.SizeOf<TokenPrivilege>(), out TokenPrivilege previous, out _);
                int code = Marshal.GetLastWin32Error();
                // AdjustTokenPrivileges may return true and ERROR_NOT_ALL_ASSIGNED. Do not
                // retry inspection unless Windows confirms this token has the privilege.
                if (!adjusted || code != 0) throw new SetupWin32Exception(code, SetupNativeOperation.EnableDebugPrivilege);
                return new DebugPrivilegeScope(token, previous);
            }
            catch { _ = CloseHandle(token); throw; }
        }

        public void Dispose()
        {
            nint handle = Interlocked.Exchange(ref token, 0);
            if (handle == 0) return;
            try
            {
                if (previous.Count != 0)
                {
                    bool restored = RestoreTokenPrivileges(handle, false, ref previous, 0, 0, 0);
                    int code = Marshal.GetLastWin32Error();
                    if (!restored || code != 0) throw new SetupWin32Exception(code, SetupNativeOperation.RestoreDebugPrivilege);
                }
            }
            finally { _ = CloseHandle(handle); }
        }
    }

    private static SetupWin32Exception Failure(SetupNativeOperation operation) => new(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenGroups { public uint Count; public SidAndAttributes FirstGroup; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int kind, nint buffer, uint bytes, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivilege desired, uint bytes, out TokenPrivilege previous, out uint needed);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RestoreTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivilege desired, uint bytes, nint previous, nint needed);
}
