using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SqlObserver.CpuProcessAccess
{
    // Administrative deployment component. Never loaded by the Collector.
    public sealed class Policy
    {
        public string SqlService { get; set; }
        public string SqlImagePath { get; set; }
        public string SqlImageSha256 { get; set; }
        public string SqlAccountSid { get; set; }
        public string CollectorService { get; set; }
        public string MonitorSid { get; set; }
        public string Binding { get { Validate(); return AclRules.Hash(Encoding.UTF8.GetBytes(string.Join("\n", new string[] { SqlService, SqlImagePath, SqlImageSha256, SqlAccountSid, CollectorService, MonitorSid }))); } }
        public void Validate()
        {
            if (!Regex.IsMatch(SqlService ?? "", @"^(MSSQLSERVER|MSSQL\$[A-Za-z0-9_]{1,100})$")) throw new InvalidOperationException("invalid-sql-service");
            if (!Regex.IsMatch(CollectorService ?? "", @"^[A-Za-z0-9_.-]{1,100}$") || CollectorService == SqlService) throw new InvalidOperationException("invalid-collector-service");
            if (string.IsNullOrEmpty(SqlImagePath) || !Regex.IsMatch(SqlImagePath, @"^[A-Za-z]:\\") || Path.GetFullPath(SqlImagePath) != SqlImagePath || !string.Equals(Path.GetFileName(SqlImagePath), "sqlservr.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("invalid-sql-image-path");
            if (!Regex.IsMatch(SqlImageSha256 ?? "", "^[0-9a-f]{64}$")) throw new InvalidOperationException("invalid-image-hash");
            SecurityIdentifier monitor = new SecurityIdentifier(MonitorSid), sql = new SecurityIdentifier(SqlAccountSid);
            // This initial deployment slice supports a dedicated local/domain account or gMSA.
            // Service-SID grants require a separate actual-service-token qualification.
            if (!monitor.IsAccountSid() || !MonitorSid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || monitor.Equals(sql)) throw new InvalidOperationException("monitor-must-be-separate-account-sid");
        }
    }

    public sealed class AclSnapshot
    {
        public readonly ControlFlags Flags;
        public readonly RawAcl Acl;
        public string Digest { get { return AclRules.Hash(Bytes()); } }
        public AclSnapshot(ControlFlags flags, RawAcl acl) { Flags = flags; Acl = acl; }
        public byte[] Bytes() { if (Acl == null) throw new InvalidOperationException("null-acl"); byte[] result = new byte[4 + Acl.BinaryLength]; Array.Copy(BitConverter.GetBytes((int)Flags), result, 4); Acl.GetBinaryForm(result, 4); return result; }
        public static AclSnapshot Decode(byte[] bytes) { if (bytes == null || bytes.Length < 12 || bytes.Length > 65536) throw new InvalidOperationException("acl-bound"); var result = new AclSnapshot((ControlFlags)BitConverter.ToInt32(bytes, 0), new RawAcl(bytes, 4)); if (!AclRules.Equal(bytes, result.Bytes())) throw new InvalidOperationException("noncanonical-acl-record"); AclRules.Validate(result); return result; }
    }

    public static class AclRules
    {
        public const int GrantMask = 0x101000;
        public static byte[] AceBytes(GenericAce ace) { byte[] result = new byte[ace.BinaryLength]; ace.GetBinaryForm(result, 0); return result; }
        public static string Hash(byte[] value) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        public static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        public static void Validate(AclSnapshot value)
        {
            const ControlFlags supported = ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected;
            if (value.Acl == null || (value.Flags & ControlFlags.DiscretionaryAclPresent) == 0 || (value.Flags & ~supported) != 0) throw new InvalidOperationException("unsupported-or-null-dacl");
            if (!new CommonSecurityDescriptor(false, false, new RawSecurityDescriptor(value.Flags, null, null, null, value.Acl)).IsDiscretionaryAclCanonical) throw new InvalidOperationException("noncanonical-dacl");
            if (value.Acl.Count > 256 || value.Acl.BinaryLength > 60000) throw new InvalidOperationException("acl-bound");
            foreach (GenericAce entry in value.Acl) { CommonAce ace = entry as CommonAce; if (ace == null || ace.IsCallback || (ace.AceQualifier != AceQualifier.AccessAllowed && ace.AceQualifier != AceQualifier.AccessDenied)) throw new InvalidOperationException("unsupported-ace"); }
        }
        public static AclSnapshot Add(AclSnapshot before, SecurityIdentifier sid)
        {
            Validate(before); RawAcl acl = new RawAcl(before.Acl.Revision, before.Acl.Count + 1); int insert = before.Acl.Count;
            for (int i = 0; i < before.Acl.Count; i++) { CommonAce ace = (CommonAce)before.Acl[i]; if (ace.SecurityIdentifier.Equals(sid)) throw new InvalidOperationException("preexisting-monitor-ace"); if (insert == before.Acl.Count && (ace.AceFlags & AceFlags.Inherited) != 0) insert = i; acl.InsertAce(i, GenericAce.CreateFromBinaryForm(AceBytes(ace), 0)); }
            acl.InsertAce(insert, OwnedAce(sid)); var after = new AclSnapshot(before.Flags, acl); Validate(after); return after;
        }
        public static CommonAce OwnedAce(SecurityIdentifier sid) { return new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, GrantMask, sid, false, null); }
        // Valid only with an independently validated persisted ownership record for this process.
        // Preserve CURRENT unrelated ACE bytes and flags; never restore an old whole DACL.
        public static AclSnapshot RemoveOwned(AclSnapshot current, SecurityIdentifier sid)
        {
            Validate(current); int found = -1; byte[] expected = AceBytes(OwnedAce(sid));
            for (int i = 0; i < current.Acl.Count; i++) { CommonAce ace = (CommonAce)current.Acl[i]; if (!ace.SecurityIdentifier.Equals(sid)) continue; if (found >= 0 || !Equal(AceBytes(ace), expected)) throw new InvalidOperationException("monitor-ace-ownership-ambiguous"); found = i; }
            if (found < 0) throw new InvalidOperationException("owned-ace-missing");
            var result = new RawAcl(current.Acl.Revision, current.Acl.Count - 1);
            for (int i = 0; i < current.Acl.Count; i++) if (i != found) result.InsertAce(result.Count, GenericAce.CreateFromBinaryForm(AceBytes(current.Acl[i]), 0));
            var after = new AclSnapshot(current.Flags, result); Validate(after); return after;
        }
    }

    public sealed class Receipt
    {
        public string State { get; set; }
        public uint ProcessId { get; set; }
        public long CreationFileTime { get; set; }
        public string DaclSha256 { get; set; }
        public string PolicySha256 { get; set; }
        public int AccessMask { get { return AclRules.GrantMask; } }
        public bool DebugPrivilegeRemoved { get; set; }
    }

    internal sealed class Record
    {
        internal string Binding; internal uint Pid; internal long Creation; internal AclSnapshot Before; internal AclSnapshot After;
        // 0 = write-ahead proposal, 1 = verified applied, 2 = removed/absent.
        // An applied grant later removed by an administrator must NOT be silently regranted.
        internal int Phase;
        internal string ReconcileState(AclSnapshot current)
        {
            if (Phase == 2) throw new InvalidOperationException("removed-generation-refuses-regrant");
            if (current.Digest == After.Digest) return "grant-verified";
            if (current.Digest != Before.Digest || Phase != 0) throw new InvalidOperationException("same-generation-dacl-drift");
            return "pending-preimage";
        }
        internal static Record Load(string path, Policy policy)
        {
            if (!File.Exists(path)) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length > 131200 || reader.ReadInt32() != 1) throw new InvalidOperationException("ownership-record-invalid");
                var record = new Record { Binding = reader.ReadString(), Pid = reader.ReadUInt32(), Creation = reader.ReadInt64(), Phase = reader.ReadInt32(), Before = ReadAcl(reader), After = ReadAcl(reader) };
                if (stream.Position != stream.Length || record.Pid == 0 || record.Creation <= 0 || record.Phase < 0 || record.Phase > 2 || record.Binding != policy.Binding || AclRules.Add(record.Before, new SecurityIdentifier(policy.MonitorSid)).Digest != record.After.Digest) throw new InvalidOperationException("ownership-record-binding-invalid");
                return record;
            }
        }
        private static AclSnapshot ReadAcl(BinaryReader reader) { int size = reader.ReadInt32(); if (size < 12 || size > 65536) throw new InvalidOperationException("ownership-record-acl-bound"); return AclSnapshot.Decode(reader.ReadBytes(size)); }
        internal void Save(string path, bool protectOwner = false)
        {
            string temp = path + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new BinaryWriter(stream, Encoding.UTF8)) { writer.Write(1); writer.Write(Binding); writer.Write(Pid); writer.Write(Creation); writer.Write(Phase); foreach (AclSnapshot value in new AclSnapshot[] { Before, After }) { byte[] bytes = value.Bytes(); writer.Write(bytes.Length); writer.Write(bytes); } writer.Flush(); stream.Flush(true); }
                if (protectOwner) { FileSecurity security = File.GetAccessControl(temp); security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)); File.SetAccessControl(temp, security); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public static class Provisioner
    {
        private const uint QuerySync = 0x101000, ReadControl = 0x20000, WriteDac = 0x40000;
        public static string AccountSid(string service)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service))
            {
                if (key == null) throw new InvalidOperationException("service-missing"); string account = key.GetValue("ObjectName") as string;
                if (string.IsNullOrEmpty(account)) throw new InvalidOperationException("service-account-missing");
                if (account == "LocalSystem") return "S-1-5-18";
                if (account.StartsWith(@".\", StringComparison.Ordinal)) account = Environment.MachineName + account.Substring(1);
                return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
            }
        }
        public static void ValidateServices(Policy policy)
        {
            policy.Validate();
            if (AccountSid(policy.CollectorService) != policy.MonitorSid || AccountSid(policy.SqlService) != policy.SqlAccountSid) throw new InvalidOperationException("service-account-drift");
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + policy.SqlService))
            {
                string command = key.GetValue("ImagePath") as string;
                if (string.IsNullOrEmpty(command)) throw new InvalidOperationException("sql-image-configuration-missing");
                command = Environment.ExpandEnvironmentVariables(command.Trim());
                string path;
                if (command.StartsWith("\"", StringComparison.Ordinal)) { int end = command.IndexOf('"', 1); if (end < 2) throw new InvalidOperationException("sql-image-configuration-invalid"); path = command.Substring(1, end - 1); }
                else { int end = command.IndexOf(' '); path = end < 0 ? command : command.Substring(0, end); }
                if (!string.Equals(Path.GetFullPath(path), policy.SqlImagePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("sql-image-configuration-drift");
            }
        }
        public static Receipt Execute(Policy policy, string statePath, string operation, bool apply)
        {
            if (operation != "Inspect" && operation != "Reconcile" && operation != "Remove") throw new InvalidOperationException("invalid-operation");
            bool noDebug = RemoveDebugPrivilege(); ValidateServices(policy);
            Record record = Record.Load(statePath, policy);
            uint pid = ServicePid(policy.SqlService);
            if (pid == 0) { if (record != null && !PriorExited(record)) throw new InvalidOperationException("prior-process-still-live-or-unverifiable"); return new Receipt { State = "sql-not-running", PolicySha256 = policy.Binding, DebugPrivilegeRemoved = noDebug }; }
            IntPtr handle = OpenProcess(QuerySync | ReadControl | ((apply && operation != "Inspect") ? WriteDac : 0), false, pid);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "process-access-denied");
            try
            {
                long creation = Creation(handle); string image = Image(handle);
                if (!string.Equals(image, policy.SqlImagePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("live-image-mismatch");
                RejectReparseAncestors(image);
                // Prevent replacement/writes to the pinned executable while validating this generation.
                using (var executable = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (SHA256 sha = SHA256.Create()) if (BitConverter.ToString(sha.ComputeHash(executable)).Replace("-", "").ToLowerInvariant() != policy.SqlImageSha256) throw new InvalidOperationException("sql-image-hash-drift");
                    Verify(handle, pid, creation, policy);
                    AclSnapshot current = Read(handle); AclRules.Validate(current);
                    bool same = record != null && record.Pid == pid && record.Creation == creation;
                    if (record != null && !same && !PriorExited(record)) throw new InvalidOperationException("prior-process-still-live-or-unverifiable");
                    var receipt = new Receipt { ProcessId = pid, CreationFileTime = creation, DaclSha256 = current.Digest, PolicySha256 = policy.Binding, DebugPrivilegeRemoved = noDebug };
                    if (operation == "Remove")
                    {
                        if (!same) { receipt.State = "no-owned-ace-for-current-generation"; return receipt; }
                        if (current.Digest == record.Before.Digest) { receipt.State = "owned-grant-absent"; if (apply) { record.Phase = 2; record.Save(statePath, true); } return receipt; }
                        if (record.Phase == 2) throw new InvalidOperationException("removed-record-cannot-own-new-ace");
                        AclSnapshot without = AclRules.RemoveOwned(current, new SecurityIdentifier(policy.MonitorSid));
                        receipt.State = "would-remove-owned-ace";
                        if (apply) { GuardedWrite(handle, pid, creation, policy, current, without); record.Phase = 2; record.Save(statePath, true); receipt.State = "owned-ace-removed"; receipt.DaclSha256 = without.Digest; }
                        return receipt;
                    }
                    if (same)
                    {
                        if (record.ReconcileState(current) == "grant-verified") { receipt.State = "grant-verified"; if (operation == "Reconcile" && apply && record.Phase == 0) { record.Phase = 1; record.Save(statePath, true); } return receipt; }
                        // Write-ahead record may survive a crash before SetSecurityInfo: retry only exact preimage.
                    }
                    else record = new Record { Binding = policy.Binding, Pid = pid, Creation = creation, Before = current, After = AclRules.Add(current, new SecurityIdentifier(policy.MonitorSid)) };
                    receipt.State = "would-add-exact-grant";
                    if (operation == "Reconcile" && apply)
                    {
                        record.Save(statePath, true); Record confirmed = Record.Load(statePath, policy);
                        if (confirmed == null || confirmed.Pid != pid || confirmed.Creation != creation || confirmed.After.Digest != record.After.Digest) throw new InvalidOperationException("ownership-record-readback-failed");
                        GuardedWrite(handle, pid, creation, policy, current, record.After); record.Phase = 1; record.Save(statePath, true); receipt.State = "grant-applied"; receipt.DaclSha256 = record.After.Digest;
                    }
                    return receipt;
                }
            }
            finally { CloseHandle(handle); }
        }
        public static void RejectReparseAncestors(string path)
        {
            string cursor = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(cursor)) { if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("reparse-path-refused"); cursor = Path.GetDirectoryName(cursor); }
        }
        private static bool PriorExited(Record record)
        {
            IntPtr handle = OpenProcess(QuerySync, false, record.Pid);
            if (handle == IntPtr.Zero) { if (Marshal.GetLastWin32Error() == 87) return true; throw new Win32Exception(Marshal.GetLastWin32Error(), "prior-process-unverifiable"); }
            try { return Creation(handle) != record.Creation || WaitForSingleObject(handle, 0) == 0; } finally { CloseHandle(handle); }
        }
        private static void Verify(IntPtr handle, uint pid, long creation, Policy policy)
        {
            ValidateServices(policy);
            if (WaitForSingleObject(handle, 0) != 258 || ServicePid(policy.SqlService) != pid || Creation(handle) != creation || !string.Equals(Image(handle), policy.SqlImagePath, StringComparison.OrdinalIgnoreCase) || ProcessAccountSid(handle) != policy.SqlAccountSid) throw new InvalidOperationException("process-identity-drift");
        }
        private static void GuardedWrite(IntPtr handle, uint pid, long creation, Policy policy, AclSnapshot before, AclSnapshot after)
        {
            Verify(handle, pid, creation, policy); if (Read(handle).Digest != before.Digest) throw new InvalidOperationException("dacl-drift-before-write");
            byte[] acl = new byte[after.Acl.BinaryLength]; after.Acl.GetBinaryForm(acl, 0); IntPtr memory = Marshal.AllocHGlobal(acl.Length);
            try { Marshal.Copy(acl, 0, memory, acl.Length); uint protection = (after.Flags & ControlFlags.DiscretionaryAclProtected) != 0 ? 0x80000000u : 0x20000000u; uint error = SetSecurityInfo(handle, 6, 4 | protection, IntPtr.Zero, IntPtr.Zero, memory, IntPtr.Zero); if (error != 0) throw new Win32Exception((int)error); }
            finally { Marshal.FreeHGlobal(memory); }
            if (Read(handle).Digest != after.Digest) throw new InvalidOperationException("dacl-readback-failed"); Verify(handle, pid, creation, policy);
        }
        private static AclSnapshot Read(IntPtr handle)
        {
            IntPtr owner, group, dacl, sacl, descriptor; uint error = GetSecurityInfo(handle, 6, 4, out owner, out group, out dacl, out sacl, out descriptor);
            if (error != 0) throw new Win32Exception((int)error);
            try { uint length = GetSecurityDescriptorLength(descriptor); if (length < 20 || length > 65536) throw new InvalidOperationException("descriptor-bound"); byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length); var raw = new RawSecurityDescriptor(bytes, 0); return new AclSnapshot(raw.ControlFlags, raw.DiscretionaryAcl); } finally { LocalFree(descriptor); }
        }
        private static uint ServicePid(string name)
        {
            IntPtr manager = OpenSCManager(null, null, 1), service = IntPtr.Zero; if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { service = OpenService(manager, name, 4); if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); Status status; int needed; if (!QueryServiceStatusEx(service, 0, out status, Marshal.SizeOf(typeof(Status)), out needed)) throw new Win32Exception(Marshal.GetLastWin32Error()); if (status.ServiceType != 0x10) throw new InvalidOperationException("sql-service-must-own-process"); if (status.CurrentState != 4) return 0; if (status.ProcessId == 0) throw new InvalidOperationException("sql-pid-missing"); return status.ProcessId; }
            finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
        private static long Creation(IntPtr handle) { long start, end, kernel, user; if (!GetProcessTimes(handle, out start, out end, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error()); return start; }
        private static string Image(IntPtr handle) { var text = new StringBuilder(32768); uint length = (uint)text.Capacity; if (!QueryFullProcessImageName(handle, 0, text, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error()); return text.ToString(); }
        private static string ProcessAccountSid(IntPtr handle)
        {
            IntPtr token;
            if (!OpenProcessToken(handle, 8, out token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "sql-token-query-denied");
            try
            {
                int needed;
                GetTokenInformation(token, 1, IntPtr.Zero, 0, out needed);
                if (Marshal.GetLastWin32Error() != 122 || needed < IntPtr.Size || needed > 4096) throw new InvalidOperationException("sql-token-user-bound");
                IntPtr buffer = Marshal.AllocHGlobal(needed);
                try { if (!GetTokenInformation(token, 1, buffer, needed, out needed)) throw new Win32Exception(Marshal.GetLastWin32Error()); return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value; }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        // SE_PRIVILEGE_REMOVED, not merely disabled: this child cannot re-enable debug privilege.
        private static bool RemoveDebugPrivilege()
        {
            IntPtr token; if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Luid luid; if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid)) throw new Win32Exception(Marshal.GetLastWin32Error()); var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 4 }; if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); int error = Marshal.GetLastWin32Error(); if (error != 0 && error != 1300) throw new Win32Exception(error); return true; } finally { CloseHandle(token); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Status { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint rights, bool inherit, uint id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder image, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern uint SetSecurityInfo(IntPtr handle, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out Status status, int size, out int needed);
        [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr buffer, int length, out int returned);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr returned);
    }
}
