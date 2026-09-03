[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
    [string] $Profile = 'Release',
    [Parameter(Mandatory = $true)] [string] $CaseId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$MaximumOutput = 65536
$MaximumError = 8192
$MaximumMilliseconds = 180000
$ApprovedSqlContractSha256 = 'bcee1b97f6405d48dd4ecb786c53236a85da52b158077c65057cc20847038f33'
$ApprovedSqlContractSchemaSha256 = '5539bba4fe0139b92aadbcd6203526cb3377b689d732e68c70aff60bac843406'

if ($IsWindows -and -not ('M12OutputFile' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class M12OutputFile
{
    public readonly record struct Identity(uint Volume, uint High, uint Low, uint Links, string FinalPath);
    public static Identity Read(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) throw new InvalidOperationException();
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new InvalidOperationException();
        return new Identity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow, info.NumberOfLinks, buffer.ToString());
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
'@
}
if ($IsWindows -and -not ('M12SuspendedProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public sealed class M12SuspendedProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint KillOnClose = 0x2000;
    private const int ExtendedLimits = 9;
    private const int ProcessIdList = 3;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 1;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _jobHandle;
    private readonly SafeFileHandle _stdoutRead;
    private readonly SafeFileHandle _stderrRead;
    private M12SuspendedProcess(IntPtr processHandle, IntPtr jobHandle, int processId, SafeFileHandle stdoutRead, SafeFileHandle stderrRead)
    {
        _processHandle = processHandle; _jobHandle = jobHandle; ProcessId = processId; _stdoutRead = stdoutRead; _stderrRead = stderrRead;
        ManagedProcess = Process.GetProcessById(processId);
    }
    public int ProcessId { get; }
    public Process ManagedProcess { get; }
    public Stream StandardOutput => new FileStream(_stdoutRead, FileAccess.Read, 4096, false);
    public Stream StandardError => new FileStream(_stderrRead, FileAccess.Read, 4096, false);
    public static M12SuspendedProcess Start(string executable, string[] args, string workingDirectory)
    {
        SafeFileHandle stdoutRead = null!, stdoutWrite = null!, stderrRead = null!, stderrWrite = null!;
        IntPtr processHandle = IntPtr.Zero, threadHandle = IntPtr.Zero, jobHandle = IntPtr.Zero;
        try
        {
            SecurityAttributes pipeAttributes = new() { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref pipeAttributes, 0) || !SetHandleInformation(stdoutRead, HandleFlagInherit, 0) ||
                !CreatePipe(out stderrRead, out stderrWrite, ref pipeAttributes, 0) || !SetHandleInformation(stderrRead, HandleFlagInherit, 0)) throw new InvalidOperationException();
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), dwFlags = StartfUseStdHandles, hStdOutput = stdoutWrite.DangerousGetHandle(), hStdError = stderrWrite.DangerousGetHandle(), hStdInput = GetStdHandle(-10) };
            string command = BuildCommandLine(executable, args);
            var commandLine = new StringBuilder(command);
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended | CreateNoWindow, IntPtr.Zero, workingDirectory, ref startup, out var information)) throw new InvalidOperationException();
            processHandle = information.hProcess; threadHandle = information.hThread;
            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) throw new InvalidOperationException();
            var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnClose } };
            if (!SetInformationJobObject(jobHandle, ExtendedLimits, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) || !AssignProcessToJobObject(jobHandle, processHandle) || ResumeThread(threadHandle) == uint.MaxValue) throw new InvalidOperationException();
            if (!CloseHandle(threadHandle)) throw new InvalidOperationException(); threadHandle = IntPtr.Zero; stdoutWrite.Dispose(); stdoutWrite = null!; stderrWrite.Dispose(); stderrWrite = null!;
            return new M12SuspendedProcess(processHandle, jobHandle, information.dwProcessId, stdoutRead, stderrRead);
        }
        catch
        {
            var cleanupFailures = new System.Collections.Generic.List<Exception>();
            try { if (processHandle != IntPtr.Zero && !TerminateProcess(processHandle, 1)) throw new InvalidOperationException(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            try { if (processHandle != IntPtr.Zero && WaitForSingleObject(processHandle, 2000) == 0xFFFFFFFF) throw new InvalidOperationException(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            try { if (jobHandle != IntPtr.Zero && !TerminateJobObject(jobHandle, 1)) throw new InvalidOperationException(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            try { if (threadHandle != IntPtr.Zero && !CloseHandle(threadHandle)) throw new InvalidOperationException(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            try { if (processHandle != IntPtr.Zero && !CloseHandle(processHandle)) throw new InvalidOperationException(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            try { if (jobHandle != IntPtr.Zero && !CloseHandle(jobHandle)) throw new InvalidOperationException(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            try { stdoutRead?.Dispose(); stdoutWrite?.Dispose(); stderrRead?.Dispose(); stderrWrite?.Dispose(); } catch (Exception ex) { cleanupFailures.Add(ex); }
            if (cleanupFailures.Count != 0) throw new AggregateException(cleanupFailures);
            throw;
        }
    }
    private static string BuildCommandLine(string executable, string[] args)
    {
        var builder = new StringBuilder(Quote(executable));
        foreach (string arg in args) builder.Append(' ').Append(Quote(arg));
        return builder.ToString();
    }
    private static string Quote(string value)
    {
        var builder = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') { builder.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; } builder.Append('\\', slashes).Append(c); slashes = 0; }
        builder.Append('\\', slashes * 2).Append('"'); return builder.ToString();
    }
    public int ActiveProcesses
    {
        get { IntPtr buffer = Marshal.AllocHGlobal(65536); try { if (!QueryInformationJobObject(_jobHandle, ProcessIdList, buffer, 65536, IntPtr.Zero)) throw new InvalidOperationException(); return Marshal.ReadInt32(buffer); } finally { Marshal.FreeHGlobal(buffer); } }
    }
    public int[] ProcessIds
    {
        get { IntPtr buffer = Marshal.AllocHGlobal(65536); try { if (!QueryInformationJobObject(_jobHandle, ProcessIdList, buffer, 65536, IntPtr.Zero)) throw new InvalidOperationException(); int count = Marshal.ReadInt32(buffer, 4); var ids = new int[count]; int offset = IntPtr.Size == 8 ? 8 : 8; for (int i = 0; i < count; i++) ids[i] = IntPtr.Size == 8 ? unchecked((int)Marshal.ReadInt64(buffer, offset + i * IntPtr.Size)) : Marshal.ReadInt32(buffer, offset + i * IntPtr.Size); return ids; } finally { Marshal.FreeHGlobal(buffer); } }
    }
    public void Terminate() { if (!TerminateJobObject(_jobHandle, 1)) throw new InvalidOperationException(); }
    public bool WaitForZero(int milliseconds) { long deadline = Environment.TickCount64 + milliseconds; while (Environment.TickCount64 <= deadline) { if (ActiveProcesses == 0) return true; System.Threading.Thread.Sleep(25); } return ActiveProcesses == 0; }
    public void Dispose() { var failures = new System.Collections.Generic.List<Exception>(); try { if (ActiveProcesses > 0 && !TerminateJobObject(_jobHandle, 1)) throw new InvalidOperationException(); if (!WaitForZero(2000)) throw new TimeoutException(); } catch (Exception ex) { failures.Add(ex); } try { _stdoutRead.Dispose(); } catch (Exception ex) { failures.Add(ex); } try { _stderrRead.Dispose(); } catch (Exception ex) { failures.Add(ex); } try { if (_processHandle != IntPtr.Zero && !CloseHandle(_processHandle)) throw new InvalidOperationException(); } catch (Exception ex) { failures.Add(ex); } try { if (_jobHandle != IntPtr.Zero && !CloseHandle(_jobHandle)) throw new InvalidOperationException(); } catch (Exception ex) { failures.Add(ex); } if (failures.Count != 0) throw new AggregateException(failures); }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public int cb; public IntPtr lpReserved; public IntPtr lpDesktop; public IntPtr lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public uint dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations; public ulong WriteOperations; public ulong OtherOperations; public ulong ReadBytes; public ulong WriteBytes; public ulong OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobProcessUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
'@
}

function Fail-M12([string] $Code) { throw "M12-SQLSERVER-$Code" }
$script:M12OutputIdentities = @{}
$script:M12OutputHashes = @{}
$script:M12OutputBytes = @{}
function Assert-M12TrustedTree([string] $Path) { $full=[IO.Path]::GetFullPath($Path); $root=[IO.Path]::GetPathRoot($full); $cursor=$root; foreach($part in ($full.Substring($root.Length)-split '[\\/]')) { if([string]::IsNullOrEmpty($part)){continue}; $cursor=Join-Path $cursor $part; if(-not(Test-Path -LiteralPath $cursor)){continue}; $item=Get-Item -LiteralPath $cursor -Force; if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){Fail-M12 'PATH'}; if([IO.Path]::GetFileName($cursor)-match '[:\x00]'){Fail-M12 'PATH'} } }
function Get-M12RequiredValue([string] $Name) { $v=[Environment]::GetEnvironmentVariable($Name); if([String]::IsNullOrWhiteSpace($v)-or $v.Length -gt 4096){Fail-M12 'INPUT'}; return $v }
function Normalize-M12NativePath([string] $Path) { if($Path.StartsWith('\\?\UNC\',[StringComparison]::OrdinalIgnoreCase)){return '\\'+$Path.Substring(8)};if($Path.StartsWith('\\?\',[StringComparison]::OrdinalIgnoreCase)){return $Path.Substring(4)};return $Path }
function Assert-M12OutputFileIdentity([string] $Path,[string] $ExpectedDirectory) { $full=[IO.Path]::GetFullPath($Path);$directory=[IO.Path]::GetFullPath($ExpectedDirectory).TrimEnd('\\','/');if(-not $full.StartsWith($directory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or [IO.Path]::GetFileName($full)-notmatch '^[a-z0-9-]+\.(json|trx)$'){Fail-M12 'OUTPUT'};Assert-M12TrustedTree $full;$s=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);try{$i=[M12OutputFile]::Read($s);if($i.Links -ne 1-or [IO.Path]::GetFullPath((Normalize-M12NativePath $i.FinalPath))-ine $full){Fail-M12 'OUTPUT'};return $i}finally{$s.Dispose()} }
function Open-M12ExecutableHandle([string]$Path){$full=[IO.Path]::GetFullPath($Path);Assert-M12TrustedTree $full;$s=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$i=[M12OutputFile]::Read($s);if($i.Links-ne 1-or[IO.Path]::GetFullPath((Normalize-M12NativePath $i.FinalPath))-ine$full){$s.Dispose();Fail-M12 'EXECUTABLE'};return [pscustomobject]@{Stream=$s;Identity=$i}}
function Write-M12Utf8Json([string] $Path,[object] $Value,[string] $ExpectedDirectory) { $full=[IO.Path]::GetFullPath($Path);if(Test-Path -LiteralPath $full){Fail-M12 'OUTPUT'};$b=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 32 -Compress)+"`n");if($b.Length -lt 1-or$b.Length -gt 32768-or @($b|Where-Object{$_-eq 13}).Count-ne 0-or @($b|Where-Object{$_-eq 10}).Count-ne 1){Fail-M12 'OUTPUT'};$s=[IO.FileStream]::new($full,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$before=$null;try{$s.Write($b,0,$b.Length);$s.Flush($true);$before=[M12OutputFile]::Read($s);if($before.Links -ne 1-or [IO.Path]::GetFullPath((Normalize-M12NativePath $before.FinalPath))-ine $full){Fail-M12 'OUTPUT'}}finally{$s.Dispose()};$after=Assert-M12OutputFileIdentity $full $ExpectedDirectory;if($null-eq$before-or$after.Volume-ne$before.Volume-or$after.High-ne$before.High-or$after.Low-ne$before.Low-or$after.Links-ne 1){Fail-M12 'OUTPUT'};$name=[IO.Path]::GetFileName($full);if($script:M12OutputIdentities.ContainsKey($name)){Fail-M12 'OUTPUT'};$script:M12OutputIdentities[$name]=$after;$script:M12OutputHashes[$name]=Get-M12Sha256 $full;$script:M12OutputBytes[$name]=$b }
function Read-M12LockedBytes([string]$Path,[string]$ExpectedDirectory,[int]$MaximumBytes){$full=[IO.Path]::GetFullPath($Path);$directory=[IO.Path]::GetFullPath($ExpectedDirectory).TrimEnd('\\','/');if(-not$full.StartsWith($directory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){Fail-M12 'OUTPUT'};Assert-M12TrustedTree $full;$s=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None);try{$identity=[M12OutputFile]::Read($s);if($identity.Links-ne 1-or[IO.Path]::GetFullPath((Normalize-M12NativePath $identity.FinalPath))-ine$full-or$s.Length-lt 1-or$s.Length-gt$MaximumBytes){Fail-M12 'OUTPUT'};$bytes=[byte[]]::new([int]$s.Length);$offset=0;while($offset-lt$bytes.Length){$n=$s.Read($bytes,$offset,$bytes.Length-$offset);if($n-le 0){Fail-M12 'OUTPUT'};$offset+=$n};return [pscustomobject]@{Identity=$identity;Bytes=$bytes}}finally{$s.Dispose()}}
function Get-M12Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-M12ClosedJson([string] $Path,[string[]]$ExpectedNames=$null) { $info=Get-Item -LiteralPath $Path -Force -ErrorAction Stop;$b=[IO.File]::ReadAllBytes($Path);if($info.PSIsContainer-or$info.Length-lt 1-or$info.Length-gt 32768-or$b.Length-ne$info.Length-or$b[-1]-ne 10-or @($b|Where-Object{$_-eq 13}).Count-ne 0-or @($b|Where-Object{$_-eq 10}).Count-ne 1-or($b.Length-ge 3-and$b[0]-eq 239-and$b[1]-eq 187-and$b[2]-eq 191)){Fail-M12 'OUTPUT'};$doc=$null;try{$doc=[Text.Json.JsonDocument]::Parse($b);if($doc.RootElement.ValueKind-ne [Text.Json.JsonValueKind]::Object){Fail-M12 'OUTPUT'};$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);foreach($p in $doc.RootElement.EnumerateObject()){if(-not$seen.Add($p.Name)){Fail-M12 'OUTPUT'}};$value=[Text.UTF8Encoding]::new($false,$true).GetString($b)|ConvertFrom-Json -DateKind String -ErrorAction Stop}catch{Fail-M12 'OUTPUT'}finally{if($null-ne$doc){$doc.Dispose()}};if($null-ne$ExpectedNames-and(@($value.PSObject.Properties.Name)-join '|')-cne($ExpectedNames-join '|')){Fail-M12 'OUTPUT'};return $value }
function Read-M12ClosedJsonBytes([byte[]]$Bytes,[string[]]$ExpectedNames=$null){if($Bytes.Length-lt 1-or$Bytes.Length-gt 32768-or$Bytes[-1]-ne 10-or@($Bytes|Where-Object{$_-eq 13}).Count-ne 0-or@($Bytes|Where-Object{$_-eq 10}).Count-ne 1-or($Bytes.Length-ge 3-and$Bytes[0]-eq 239-and$Bytes[1]-eq 187-and$Bytes[2]-eq 191)){Fail-M12 'OUTPUT'};$doc=$null;try{$doc=[Text.Json.JsonDocument]::Parse($Bytes);if($doc.RootElement.ValueKind-ne [Text.Json.JsonValueKind]::Object){Fail-M12 'OUTPUT'};$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);foreach($p in $doc.RootElement.EnumerateObject()){if(-not$seen.Add($p.Name)){Fail-M12 'OUTPUT'}};$value=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)|ConvertFrom-Json -DateKind String -ErrorAction Stop}catch{Fail-M12 'OUTPUT'}finally{if($null-ne$doc){$doc.Dispose()}};if($null-ne$ExpectedNames-and(@($value.PSObject.Properties.Name)-join '|')-cne($ExpectedNames-join '|')){Fail-M12 'OUTPUT'};return $value}
function Assert-M12JsonTypes([string]$Path,[hashtable]$ExpectedTypes){$doc=$null;try{$doc=[Text.Json.JsonDocument]::Parse([IO.File]::ReadAllBytes($Path));$k=@{string=[Text.Json.JsonValueKind]::String;number=[Text.Json.JsonValueKind]::Number;integer=[Text.Json.JsonValueKind]::Number};foreach($e in $ExpectedTypes.GetEnumerator()){$p=$doc.RootElement.GetProperty($e.Key);if($e.Value-eq'boolean'){if($p.ValueKind -notin @([Text.Json.JsonValueKind]::True,[Text.Json.JsonValueKind]::False)){Fail-M12 'OUTPUT'}}elseif($p.ValueKind-ne$k[$e.Value]){Fail-M12 'OUTPUT'};if($e.Value-eq'integer'){$n=0;if(-not$p.TryGetInt32([ref]$n)){Fail-M12 'OUTPUT'}}}}catch{Fail-M12 'OUTPUT'}finally{if($null-ne$doc){$doc.Dispose()}}}
function Assert-M12JsonTypesBytes([byte[]]$Bytes,[hashtable]$ExpectedTypes){$doc=$null;try{$doc=[Text.Json.JsonDocument]::Parse($Bytes);$k=@{string=[Text.Json.JsonValueKind]::String;number=[Text.Json.JsonValueKind]::Number;integer=[Text.Json.JsonValueKind]::Number};foreach($e in $ExpectedTypes.GetEnumerator()){$p=$doc.RootElement.GetProperty($e.Key);if($e.Value-eq'boolean'){if($p.ValueKind -notin @([Text.Json.JsonValueKind]::True,[Text.Json.JsonValueKind]::False)){Fail-M12 'OUTPUT'}}elseif($p.ValueKind-ne$k[$e.Value]){Fail-M12 'OUTPUT'};if($e.Value-eq'integer'){$n=0;if(-not$p.TryGetInt32([ref]$n)){Fail-M12 'OUTPUT'}}}}catch{Fail-M12 'OUTPUT'}finally{if($null-ne$doc){$doc.Dispose()}}}
function Assert-M12CollectorEvidence([object]$Result){$items=@($Result.collectorEvidence);if($items.Count-ne 14){Fail-M12 'TEST'};$seen=@{};$s=0;$p=0;$u=0;$ut=0;$uc=0;for($i=0;$i-lt$items.Count;$i++){$parts=[string]$items[$i]-split '\|',4;if($parts.Count-ne 4-or$parts[1]-cne$collectors[$i]-or$seen.ContainsKey($parts[1])){Fail-M12 'TEST'};$seen[$parts[1]]=$true;$expectedOrder=if($i-eq 13){15}else{$i+1};if($parts[0]-ne[string]$expectedOrder){Fail-M12 'TEST'};switch($parts[2]){'Succeeded'{if($parts[3]-cne'Completed'){Fail-M12 'TEST'};$s++}'Partial'{if($parts[3]-notin @('SourceRowLimit','ResponseByteLimit','BlockingGraphLimit','VisibilityIncomplete','OverlapDeduplicated')){Fail-M12 'TEST'};$p++}'Unsupported'{if($parts[3]-eq'TargetUnsupported'){$ut++}elseif($parts[3]-eq'CapabilityMissing'){$uc++}else{Fail-M12 'TEST'};$u++}default{Fail-M12 'TEST'}}};if($s-ne$result.succeededCount-or$p-ne$result.partialCount-or$u-ne$result.unsupportedCount-or$ut-ne$result.unsupportedTargetCount-or$uc-ne$result.unsupportedCapabilityCount){Fail-M12 'TEST'}}
function Read-M12RepoText([string] $Path,[int] $MaximumBytes=65536) { Assert-M12TrustedTree $Path; $i=Get-Item -LiteralPath $Path -Force -ErrorAction Stop; if($i.PSIsContainer -or $i.Length -lt 1 -or $i.Length -gt $MaximumBytes){Fail-M12 'COMMIT'}; try{return [Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($Path))}catch{Fail-M12 'COMMIT'} }
function Resolve-M12RepoPath([string] $Base,[string] $Relative) { if([string]::IsNullOrWhiteSpace($Relative)-or $Relative.Contains('..')-or $Relative.Contains([char]0)){Fail-M12 'COMMIT'}; $p=[IO.Path]::GetFullPath((Join-Path $Base $Relative));Assert-M12TrustedTree $p;return $p }
function Resolve-M12CanonicalRoot([string] $Requested,[string] $Authority) {
    if ([string]::IsNullOrWhiteSpace($Requested) -or [string]::IsNullOrWhiteSpace($Authority)) { Fail-M12 'ROOT' }
    try {
        $requestedFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Requested -ErrorAction Stop).Path)
        $authorityFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Authority -ErrorAction Stop).Path)
        Assert-M12TrustedTree $requestedFull
        Assert-M12TrustedTree $authorityFull
        if (-not [String]::Equals($requestedFull.TrimEnd('\\','/'), $authorityFull.TrimEnd('\\','/'), [StringComparison]::OrdinalIgnoreCase)) { Fail-M12 'ROOT' }
        foreach ($relative in @('tools/run-m12-sqlserver-certification.ps1','tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj','release/certification/m12-sqlserver-passive-contract.v1.json','release/certification/m12-sqlserver-passive-contract.v1.schema.json','release/certification/m12-sqlserver-passive-contract.v1.sha256')) {
            $required = Resolve-M12RepoPath $requestedFull $relative
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { Fail-M12 'ROOT' }
        }
        return $requestedFull
    } catch { Fail-M12 'ROOT' }
}
function Get-M12CurrentCommit([string] $Root) { $entry=Join-Path $Root '.git';Assert-M12TrustedTree $entry;$item=Get-Item -LiteralPath $entry -Force; $gd=if($item.PSIsContainer){[IO.Path]::GetFullPath($entry)}else{$t=(Read-M12RepoText $entry 4096).TrimEnd("`r","`n");if($t -notmatch '^gitdir: ([^\r\n]+)$'){Fail-M12 'COMMIT'};[IO.Path]::GetFullPath((Join-Path $Root $Matches[1]))};Assert-M12TrustedTree $gd;$common=$gd;$cd=Join-Path $gd 'commondir';if(Test-Path -LiteralPath $cd){$b=[IO.File]::ReadAllBytes($cd);if($b.Length -lt 2 -or $b.Length -gt 32){Fail-M12 'COMMIT'};$ct=[Text.UTF8Encoding]::new($false,$true).GetString($b);if($ct -cne "../..`n"){Fail-M12 'COMMIT'};$common=[IO.Path]::GetFullPath((Join-Path $gd '../..'));Assert-M12TrustedTree $common};$h=(Read-M12RepoText (Join-Path $gd 'HEAD') 4096).TrimEnd("`r","`n");if($h -match '^[0-9a-f]{40}$'){return $h};if($h -notmatch '^ref: (refs/[A-Za-z0-9._/-]+)$'){Fail-M12 'COMMIT'};$ref=$Matches[1];$rp=Resolve-M12RepoPath $common $ref;if(Test-Path -LiteralPath $rp){$s=(Read-M12RepoText $rp 4096).Trim();if($s -match '^[0-9a-f]{40}$'){return $s};Fail-M12 'COMMIT'};$p=Join-Path $common 'packed-refs';if(-not(Test-Path -LiteralPath $p)){Fail-M12 'COMMIT'};foreach($line in (Read-M12RepoText $p 1048576)-split "`n"){if($line.TrimEnd("`r") -match ('^([0-9a-f]{40}) '+[regex]::Escape($ref)+'$')){return $Matches[1]}};Fail-M12 'COMMIT' }
function Remove-M12SafeDescendants([string]$Path,[string]$Root){$full=[IO.Path]::GetFullPath($Path);$rootFull=[IO.Path]::GetFullPath($Root);if(-not$full.StartsWith($rootFull+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){Fail-M12 'PROCESS'};$item=Get-Item -LiteralPath $full -Force -ErrorAction Stop;if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){Fail-M12 'PROCESS'};if($item.PSIsContainer){foreach($child in @(Get-ChildItem -LiteralPath $full -Force -ErrorAction Stop)){Remove-M12SafeDescendants $child.FullName $rootFull};Remove-Item -LiteralPath $full -Force -ErrorAction Stop}else{Remove-Item -LiteralPath $full -Force -ErrorAction Stop};if(Test-Path -LiteralPath $full){Fail-M12 'PROCESS'}}
function Remove-M12SafeTree([string] $Path,[string] $AllowedParent) { if(-not(Test-Path -LiteralPath $Path -ErrorAction Stop)){return};$full=[IO.Path]::GetFullPath($Path);$parent=[IO.Path]::GetFullPath($AllowedParent).TrimEnd('\\','/');if(-not $full.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){return};if([IO.Path]::GetFileName($full)-notmatch '^\.(pending|verify)-[0-9a-fA-F-]{36}$'){return};Assert-M12TrustedTree $full;foreach($i in @(Get-ChildItem -LiteralPath $full -Force -ErrorAction Stop)){Remove-M12SafeDescendants $i.FullName $full};Remove-Item -LiteralPath $full -Force -ErrorAction Stop;if(Test-Path -LiteralPath $full){Fail-M12 'PROCESS'} }
function Remove-M12RawTestDirectory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    Assert-M12TrustedTree $Path
    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force)) {
        if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail-M12 'OUTPUT' }
        [void](Assert-M12OutputFileIdentity $entry.FullName $Path)
        Remove-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop
    }
    Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
}
function Assert-M12ReleaseHost([string] $SelectedProfile,[string] $Environment,[string[]] $AllowedEnvironments) { if(-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64 -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64 -or $SelectedProfile -cne 'Release'){Fail-M12 'HOST'};if([Environment]::GetEnvironmentVariable('SQLOBSERVER_VALIDATION_PROFILE') -cne 'Release'){Fail-M12 'HOST'};if($Environment -cnotin $AllowedEnvironments){Fail-M12 'HOST'};try{$os=Get-CimInstance Win32_OperatingSystem -ErrorAction Stop;$expected=if($Environment -ceq 'release-windows-server-2022'){'Microsoft Windows Server 2022'}else{'Microsoft Windows Server 2025'};if($os.ProductType -notin @(2,3)-or -not ([string]$os.Caption).StartsWith($expected,[StringComparison]::Ordinal)){Fail-M12 'HOST'}}catch{Fail-M12 'HOST'} }

function Assert-M12ObservedPidsExited([Collections.Generic.HashSet[int]]$ProcessIds){if(-not$IsWindows){return};foreach($id in $ProcessIds){$remaining=@(Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction Stop);if($remaining.Count-ne 0){Fail-M12 'PROCESS'}}}
function Assert-M12NoDescendants([int[]]$ProcessIds){if(-not$IsWindows){return};$roots=[Collections.Generic.HashSet[int]]::new();foreach($id in $ProcessIds){[void]$roots.Add($id)};$discoveredPids=[Collections.Generic.HashSet[int]]::new();$stableEmptySweeps=0;while($stableEmptySweeps-lt 3){try{$snapshot=@(Get-CimInstance Win32_Process -ErrorAction Stop);if($snapshot.Count-gt 4096){Fail-M12 'PROCESS'};$children=@{};foreach($proc in $snapshot){$parent=[int]$proc.ParentProcessId;if(-not$children.ContainsKey($parent)){$children[$parent]=[Collections.Generic.List[int]]::new()};$children[$parent].Add([int]$proc.ProcessId)};$queue=[Collections.Generic.Queue[int]]::new();foreach($root in $roots){$queue.Enqueue($root)};$seen=[Collections.Generic.HashSet[int]]::new();$descendantsThisSweep=0;while($queue.Count-gt 0){$current=$queue.Dequeue();if(-not$seen.Add($current)){continue};if($children.ContainsKey($current)){foreach($child in $children[$current]){if($roots.Contains($child)){continue};$descendantsThisSweep++;if($descendantsThisSweep-gt 256){Fail-M12 'PROCESS'};[void]$discoveredPids.Add($child);$queue.Enqueue($child)}}};$allIds=[Collections.Generic.List[int]]::new();foreach($id in $roots){$allIds.Add($id)};foreach($id in $discoveredPids){$allIds.Add($id)};foreach($id in $allIds){if(@($snapshot|Where-Object{[int]$_.ProcessId-eq$id}).Count-ne 0){Fail-M12 'PROCESS'}};if($descendantsThisSweep-ne 0){Fail-M12 'PROCESS'};$stableEmptySweeps++}catch{if($_.Exception.Message -like 'M12-SQLSERVER-*'){throw};Fail-M12 'PROCESS'};if($stableEmptySweeps-lt 3){[Threading.Thread]::Sleep(50)}}}
function Invoke-M12BoundedProcess([string]$Exe,[string[]]$Arguments,[string]$WorkingDirectory,[object]$Job){$p=$Job.ManagedProcess;$observedPids=[Collections.Generic.HashSet[int]]::new();[void]$observedPids.Add($p.Id);$o=[IO.MemoryStream]::new();$e=[IO.MemoryStream]::new();$ob=[byte[]]::new(4096);$eb=[byte[]]::new(4096);$ot=$Job.StandardOutput.ReadAsync($ob,0,$ob.Length);$et=$Job.StandardError.ReadAsync($eb,0,$eb.Length);$deadline=[Environment]::TickCount64+$MaximumMilliseconds;$od=$false;$ed=$false;$large=$false;$timeout=$false;try{while(-not($od-and$ed-and$p.HasExited)){foreach($pid in @($Job.ProcessIds)){[void]$observedPids.Add($pid)};[Threading.Tasks.Task]::Delay(25).GetAwaiter().GetResult();if(-not$od-and$ot.IsCompleted){$n=$ot.GetAwaiter().GetResult();if($n-eq 0){$od=$true}elseif($o.Length+$n-gt$MaximumOutput){$large=$true}else{$o.Write($ob,0,$n);$ot=$Job.StandardOutput.ReadAsync($ob,0,$ob.Length)}};if(-not$ed-and$et.IsCompleted){$n=$et.GetAwaiter().GetResult();if($n-eq 0){$ed=$true}elseif($e.Length+$n-gt$MaximumError){$large=$true}else{$e.Write($eb,0,$n);$et=$Job.StandardError.ReadAsync($eb,0,$eb.Length)}};if($large-or[Environment]::TickCount64-ge$deadline){$timeout=-not$large;$Job.Terminate();break}};foreach($pid in @($Job.ProcessIds)){[void]$observedPids.Add($pid)};if(-not$Job.WaitForZero(2000)-or$Job.ActiveProcesses-ne 0){Fail-M12 'PROCESS'};Assert-M12ObservedPidsExited $observedPids;Assert-M12NoDescendants @($observedPids);return [pscustomobject]@{ExitCode=$p.ExitCode;Output=[Text.Encoding]::UTF8.GetString($o.ToArray());Error=[Text.Encoding]::UTF8.GetString($e.ToArray());TooLarge=$large;TimedOut=$timeout}}finally{$cleanupFailures=[Collections.Generic.List[Exception]]::new();try{$o.Dispose()}catch{$cleanupFailures.Add($_.Exception)};try{$e.Dispose()}catch{$cleanupFailures.Add($_.Exception)};if($cleanupFailures.Count-ne 0){throw [AggregateException]::new($cleanupFailures)}}}

$case = switch ($CaseId) {
    'm12-sqlserver-2019-passive' { [pscustomobject]@{ Major = 15; Environment = 'release-windows-server-2022' } }
    'm12-sqlserver-2022-passive' { [pscustomobject]@{ Major = 16; Environment = 'release-windows-server-2022' } }
    'm12-sqlserver-2025-passive' { [pscustomobject]@{ Major = 17; Environment = 'release-windows-server-2025' } }
    default { Fail-M12 'CASE' }
}
$case = if ($null -eq $case) { Fail-M12 'CASE' } else { $case }
$producerId = 'm12-sqlserver-harness'
$artifactKind = 'sqlserver-nonmutation-evidence'
$contractName = 'm12-sqlserver-passive-contract.v1.json'
$schemaName = 'm12-sqlserver-passive-contract.v1.schema.json'
$pinName = 'm12-sqlserver-passive-contract.v1.sha256'
$collectors = @('engine.core','database.inventory','database.files','activity.sessions','activity.requests','waits.server','blocking.current','deadlocks.system-health','queries.performance','backups.status','sql-agent.failures','tempdb.health','availability-groups.health','replication.health')

function Assert-SqlContract([string] $Root) {
    $contractPath = Join-Path $Root "release/certification/$contractName"
    $schemaPath = Join-Path $Root "release/certification/$schemaName"
    $pinPath = Join-Path $Root "release/certification/$pinName"
    foreach ($path in @($contractPath,$schemaPath,$pinPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail-M12 'INPUT' }; Assert-M12TrustedTree $path }
    $pinBytes = [IO.File]::ReadAllBytes($pinPath)
    if (@($pinBytes | Where-Object { $_ -eq 0x0D }).Count -ne 0 -or @($pinBytes | Where-Object { $_ -eq 0x0A }).Count -ne 2 -or $pinBytes[-1] -ne 0x0A) { Fail-M12 'CONTRACT' }
    $pin = [Text.UTF8Encoding]::new($false,$true).GetString($pinBytes)
    $expectedPin = "$(Get-M12Sha256 $contractPath)  $contractName`n$(Get-M12Sha256 $schemaPath)  $schemaName`n"
    if ($pin -cne $expectedPin) { Fail-M12 'CONTRACT' }
    if ((Get-M12Sha256 $contractPath) -cne $ApprovedSqlContractSha256 -or (Get-M12Sha256 $schemaPath) -cne $ApprovedSqlContractSchemaSha256) { Fail-M12 'CONTRACT' }
    try { $contract = [Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($contractPath)) | ConvertFrom-Json -DateKind String -ErrorAction Stop } catch { Fail-M12 'CONTRACT' }
    if ($contract.'$schema' -cne $schemaName -or $contract.schemaVersion -ne 1 -or $contract.contractId -cne 'm12-sqlserver-passive' -or $contract.producerId -cne $producerId -or $contract.operationalMode -cne 'passive' -or $contract.platform -cne 'windows') { Fail-M12 'CONTRACT' }
    $ordered = @($contract.collectors | Sort-Object executionOrder)
    $actual = @($ordered | ForEach-Object { $_.collectorId })
    $orders = @($ordered | ForEach-Object { [int]$_.executionOrder })
    if (($actual -join '|') -cne ($collectors -join '|') -or ($orders -join '|') -cne '1|2|3|4|5|6|7|8|9|10|11|12|13|15' -or @($contract.collectors).Count -ne 14 -or @($actual | Select-Object -Unique).Count -ne 14) { Fail-M12 'CONTRACT' }
    $assets = [ordered]@{ 'm4-core-health'='1dd0cc6cbdc4171ff656c658974cf4105c8e2594e5d1f26a5fc66011adaa284e'; 'm5-activity'='86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e'; 'm6-deadlocks'='72570fba287327e1dec64a56d6211b9c24a7d597b35010c9b9ac765615d2963f'; 'm7-query-performance'='da915ffb11e60bc0f1f019cb3e3e81ccafabc2c3ff94b26520378574562855eb'; 'm9-operational-health'='5697aaf35aee3f30f339de5fd973041978b6a0d767759e30cd223eb829e74484'; 'm10-replication'='7e06e0e3d1c71dd3c9e5a2e2acd14412984e761009921a63bf1141a5b34d18aa' }
    foreach ($asset in $assets.Keys) { if ([string]$contract.assetBundles.$asset -cne $assets[$asset]) { Fail-M12 'CONTRACT' } }
    if ($contract.mutationPolicy.passiveMutation -ne $false -or $contract.mutationPolicy.allowMutationTokens -ne $false -or $contract.mutationPolicy.snapshotRequired -ne $true) { Fail-M12 'CONTRACT' }
    return $contract
}

function Assert-SqlConnectionContract([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 4096) { Fail-M12 'INPUT' }
    try {
        $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($match in [regex]::Matches($Value, '(?:^|;)\s*([^=;]+)\s*=')) { if (-not $keys.Add($match.Groups[1].Value.Trim())) { Fail-M12 'INPUT' } }
        if (-not ('Microsoft.Data.SqlClient.SqlConnectionStringBuilder' -as [type])) {
            $clientAssembly = Join-Path $PSScriptRoot '../tests/SqlObserver.IntegrationTests.SqlServer/bin/Release/net10.0/Microsoft.Data.SqlClient.dll'
            if (Test-Path -LiteralPath $clientAssembly -PathType Leaf) { Add-Type -Path $clientAssembly }
        }
        $builder = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($Value)
        if ([string]::IsNullOrWhiteSpace($builder.DataSource) -or -not $builder.IntegratedSecurity -or $builder.UserID.Length -ne 0 -or $builder.Password.Length -ne 0) { Fail-M12 'INPUT' }
        $dataSource = $builder.DataSource.Trim(); $machine = [Environment]::MachineName
        if ($dataSource -eq '.' -or $dataSource -match '(?i)^\(local\)(?:,|\\|$)|LOCALDB|LOCALHOST|^127(?:\.\d{1,3}){3}|^tcp:127\.|^::1(?:,|\\|$)|^tcp:\[::1\]|^lpc:|^np:|^\\\\\.|DESKTOP|WORKSTATION' -or $dataSource.IndexOf($machine,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or (Test-M12ResolvesLocalAddress $dataSource)) { Fail-M12 'INPUT' }
        if ($Value -match '(?i)(?:^|;)\s*(?:User\s*ID|UID|User|Password|PWD|Access\s*Token)\s*=') { Fail-M12 'INPUT' }
        if ($Value -notmatch '(?i)(?:^|;)\s*Encrypt\s*=') { Fail-M12 'INPUT' }
        if ($builder.Encrypt -eq [Microsoft.Data.SqlClient.SqlConnectionEncryptOption]::Optional -or $builder.TrustServerCertificate) { Fail-M12 'INPUT' }
    } catch { Fail-M12 'INPUT' }
}
function Test-M12ResolvesLocalAddress([string] $DataSource) {
    $host = $DataSource -replace '(?i)^tcp:', ''
    if ($host.StartsWith('[')) { $host = $host.Substring(1, $host.IndexOf(']') - 1) }
    $comma = $host.LastIndexOf(','); if ($comma -gt 0) { $host = $host.Substring(0,$comma) }
    $slash = $host.IndexOf('\'); if ($slash -gt 0) { $host = $host.Substring(0,$slash) }
    if ([string]::IsNullOrWhiteSpace($host)) { return $true }
    try {
        $local = @([Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()))
        $target = @([Net.Dns]::GetHostAddresses($host))
        foreach ($address in $target) { if ([Net.IPAddress]::IsLoopback($address) -or @($local | Where-Object { $_.Equals($address) }).Count -gt 0) { return $true } }
    } catch [Net.Sockets.SocketException] { return $false }
    return $false
}

function Invoke-SqlReleaseTest([string] $Root, [string] $TrxPath) {
    $dotnet = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { Fail-M12 'EXECUTABLE' }
    Assert-M12TrustedTree $dotnet
    $project = Join-Path $Root 'tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { Fail-M12 'INPUT' }
    $arguments = @('test',$project,'--configuration','Release','--no-restore','--no-build','--filter','Category=RequiresM12SqlServerRelease','--logger',"trx;LogFileName=$TrxPath")
    $previousCase = [Environment]::GetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_CASE'); $previousResult = [Environment]::GetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_RESULT_PATH'); $native = $null; $executableHandle = $null; $cleanupFailure = $false
    try {
        $executableHandle = Open-M12ExecutableHandle $dotnet
        [Environment]::SetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_CASE',$CaseId)
        [Environment]::SetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_RESULT_PATH',(Join-Path (Split-Path $TrxPath -Parent) 'sqlserver-live-result.json'))
        $native = [M12SuspendedProcess]::Start($dotnet,[string[]]$arguments,$Root)
        $run = Invoke-M12BoundedProcess $dotnet $arguments $Root $native
        if ($run.TimedOut) { Fail-M12 'TIMEOUT' }
        if ($run.TooLarge -or $run.ExitCode -ne 0) { Fail-M12 'TEST' }
        $resultPath = [Environment]::GetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_RESULT_PATH')
        if ([string]::IsNullOrWhiteSpace($resultPath) -or -not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { Fail-M12 'TEST' }
        $resultLocked=Read-M12LockedBytes $resultPath (Split-Path $TrxPath -Parent) 32768
        $result = Read-M12ClosedJsonBytes $resultLocked.Bytes @('schemaVersion','caseId','environment','observedMajorVersion','observedProductVersion','platform','transportEncrypted','authenticationScheme','isSysAdmin','engineEdition','collectorContracts','invocationCount','succeededCount','partialCount','unsupportedCount','unsupportedTargetCount','unsupportedCapabilityCount','collectorEvidence','snapshotVersion','beforeHash','afterHash','passiveMutation','nonmutation','testCount')
        Assert-M12JsonTypesBytes $resultLocked.Bytes @{ schemaVersion='integer'; caseId='string'; environment='string'; observedMajorVersion='integer'; observedProductVersion='string'; platform='string'; transportEncrypted='boolean'; authenticationScheme='string'; isSysAdmin='boolean'; engineEdition='integer'; collectorContracts='integer'; invocationCount='integer'; succeededCount='integer'; partialCount='integer'; unsupportedCount='integer'; unsupportedTargetCount='integer'; unsupportedCapabilityCount='integer'; snapshotVersion='integer'; beforeHash='string'; afterHash='string'; passiveMutation='boolean'; nonmutation='boolean'; testCount='integer' }; Assert-M12CollectorEvidence $result
        $machineNames = @($result.PSObject.Properties.Name)
        $expectedMachineNames = @('schemaVersion','caseId','environment','observedMajorVersion','observedProductVersion','platform','transportEncrypted','authenticationScheme','isSysAdmin','engineEdition','collectorContracts','invocationCount','succeededCount','partialCount','unsupportedCount','unsupportedTargetCount','unsupportedCapabilityCount','collectorEvidence','snapshotVersion','beforeHash','afterHash','passiveMutation','nonmutation','testCount')
        if (($machineNames -join '|') -cne ($expectedMachineNames -join '|') -or $result.schemaVersion -ne 1 -or $result.snapshotVersion -ne 1) { Fail-M12 'TEST' }
        [int]$edition = 0; if (-not [int]::TryParse([string]$result.engineEdition,[ref]$edition)) { Fail-M12 'TEST' }
        [int]$unsupportedTarget = 0; [int]$unsupportedCapability = 0
        if (-not [int]::TryParse([string]$result.unsupportedTargetCount,[ref]$unsupportedTarget) -or -not [int]::TryParse([string]$result.unsupportedCapabilityCount,[ref]$unsupportedCapability)) { Fail-M12 'TEST' }
        if ($result.caseId -cne $CaseId -or $result.environment -cne $case.Environment -or $result.observedMajorVersion -ne $case.Major -or [string]$result.observedProductVersion -notmatch '^\d{1,2}\.\d{1,2}\.\d{1,5}\.\d{1,5}$' -or $result.platform -cne 'Windows' -or $result.transportEncrypted -ne $true -or [string]$result.authenticationScheme -notin @('Kerberos','Ntlm') -or $result.isSysAdmin -ne $false -or $edition -lt 1 -or $result.collectorContracts -ne 14 -or $result.invocationCount -ne 14 -or $result.succeededCount -lt 0 -or $result.partialCount -lt 0 -or $result.unsupportedCount -lt 0 -or ($result.succeededCount + $result.partialCount + $result.unsupportedCount) -ne 14 -or $unsupportedTarget -lt 0 -or $unsupportedCapability -lt 0 -or ($unsupportedTarget + $unsupportedCapability) -ne $result.unsupportedCount -or $result.testCount -ne 1 -or $result.passiveMutation -ne $true -or $result.nonmutation -ne $true -or [string]$result.beforeHash -notmatch '^[0-9a-f]{64}$' -or $result.beforeHash -cne $result.afterHash) { Fail-M12 'TEST' }
        if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) { Fail-M12 'TEST' }
        $trxLocked=Read-M12LockedBytes $TrxPath (Split-Path $TrxPath -Parent) 16777216
        $bytes=$trxLocked.Bytes
        $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.LoadXml([Text.UTF8Encoding]::new($false,$true).GetString($bytes))
        $results = @($xml.SelectNodes("//*[local-name()='UnitTestResult']")); if ($results.Count -ne 1 -or $results[0].outcome -cne 'Passed') { Fail-M12 'TEST' }
        $unitTests = @($xml.SelectNodes("//*[local-name()='UnitTest']")); $testId = [string]$results[0].testId; $unitTest = $unitTests | Where-Object { [string]$_.id -ceq $testId }
        if ($null -eq $unitTest -or [string]$unitTest.name -cne 'LiveReleaseSqlServerPassiveCertificationIsNonMutating') { Fail-M12 'TEST' }
        return [pscustomobject]@{ TestCount = 1; StdoutBytes = [Text.Encoding]::UTF8.GetByteCount($run.Output); StderrBytes = [Text.Encoding]::UTF8.GetByteCount($run.Error); Result = $result }
    } finally { try { if($null -eq $native){$cleanupFailure=$true}else{if($native.ActiveProcesses -gt 0){$native.Terminate()};if(-not$native.WaitForZero(2000)-or$native.ActiveProcesses-ne 0){$cleanupFailure=$true}} } catch {$cleanupFailure=$true}; try { if($null -ne $native){$native.Dispose()} } catch {$cleanupFailure=$true}; try { Remove-M12RawTestDirectory (Split-Path $TrxPath -Parent) } catch { if(Test-Path -LiteralPath (Split-Path $TrxPath -Parent)){$cleanupFailure=$true} }; try { if($null -ne $executableHandle){$executableHandle.Stream.Dispose()} } catch {$cleanupFailure=$true}; [Environment]::SetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_CASE',$previousCase);[Environment]::SetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_RESULT_PATH',$previousResult); if($cleanupFailure){Fail-M12 'PROCESS'} }
}

function Write-SqlArtifacts([string] $BuildRoot, [string] $Root, [string] $Commit, [string] $Environment, [string] $RunId, [psobject] $Run) {
    $created = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $detailName = 'sqlserver-nonmutation-evidence.json'; $resultName = 'sqlserver-nonmutation-result.json'; $provenanceName = 'sqlserver-nonmutation-provenance.json'
    $r = $Run.Result
    $detail = [ordered]@{ schemaVersion = 1; caseId = $CaseId; producerId = $producerId; kind = $artifactKind; result = 'passed'; environment = $Environment; observedMajorVersion = $r.observedMajorVersion; observedProductVersion = $r.observedProductVersion; engineEdition = $r.engineEdition; collectorContracts = $r.collectorContracts; invocationCount = $r.invocationCount; succeededCount = $r.succeededCount; partialCount = $r.partialCount; unsupportedCount = $r.unsupportedCount; unsupportedTargetCount = $r.unsupportedTargetCount; unsupportedCapabilityCount = $r.unsupportedCapabilityCount; collectorEvidence = $r.collectorEvidence; snapshotVersion = $r.snapshotVersion; snapshotMaxRows = 1000; snapshotMaxTotalRows = 10000; snapshotMaxFieldBytes = 65536; snapshotMaxBytes = 4194304; snapshotEncoding = 'length-prefixed-utf8-v1'; beforeHash = $r.beforeHash; afterHash = $r.afterHash; passiveMutation = $r.passiveMutation; nonmutation = $r.nonmutation; testCount = $r.testCount }
    Write-M12Utf8Json (Join-Path $BuildRoot $detailName) $detail $BuildRoot
    Write-M12Utf8Json (Join-Path $BuildRoot $resultName) ([ordered]@{ caseId = $CaseId; status = 'passed'; executions = 1; skipped = 0; notRun = 0; failed = 0; runId = $RunId; commitSha = $Commit; environmentId = $Environment }) $BuildRoot
    $hash = Get-M12Sha256 (Join-Path $BuildRoot $detailName)
    Write-M12Utf8Json (Join-Path $BuildRoot $provenanceName) ([ordered]@{ artifactId = 'm12-sqlserver-passive'; kind = $artifactKind; producerId = $producerId; artifactSha256 = $hash; artifactSize = (Get-Item -LiteralPath (Join-Path $BuildRoot $detailName)).Length; commitSha = $Commit; runId = $RunId; environmentId = $Environment; createdAtUtc = $created }) $BuildRoot
    $entries = @(Get-ChildItem -LiteralPath $BuildRoot -Force); if ($entries.Count -ne 3 -or @($entries | Where-Object { $_.PSIsContainer -or $_.Extension -cne '.json' }).Count -ne 0) { Fail-M12 'OUTPUT' }
    $detailObject = Read-M12ClosedJson (Join-Path $BuildRoot $detailName)
    $detailNames = @($detailObject.PSObject.Properties.Name)
    $expectedDetailNames = @('schemaVersion','caseId','producerId','kind','result','environment','observedMajorVersion','observedProductVersion','engineEdition','collectorContracts','invocationCount','succeededCount','partialCount','unsupportedCount','unsupportedTargetCount','unsupportedCapabilityCount','collectorEvidence','snapshotVersion','snapshotMaxRows','snapshotMaxTotalRows','snapshotMaxFieldBytes','snapshotMaxBytes','snapshotEncoding','beforeHash','afterHash','passiveMutation','nonmutation','testCount')
    if (($detailNames -join '|') -cne ($expectedDetailNames -join '|')) { Fail-M12 'OUTPUT' }
    Assert-M12JsonTypes (Join-Path $BuildRoot $detailName) @{ schemaVersion='integer'; caseId='string'; producerId='string'; kind='string'; result='string'; environment='string'; observedMajorVersion='integer'; observedProductVersion='string'; engineEdition='integer'; collectorContracts='integer'; invocationCount='integer'; succeededCount='integer'; partialCount='integer'; unsupportedCount='integer'; unsupportedTargetCount='integer'; unsupportedCapabilityCount='integer'; snapshotVersion='integer'; snapshotMaxRows='integer'; snapshotMaxTotalRows='integer'; snapshotMaxFieldBytes='integer'; snapshotMaxBytes='integer'; snapshotEncoding='string'; beforeHash='string'; afterHash='string'; passiveMutation='boolean'; nonmutation='boolean'; testCount='integer' }; Assert-M12CollectorEvidence $detailObject; if($detailObject.snapshotMaxRows-ne 1000-or$detailObject.snapshotMaxTotalRows-ne 10000-or$detailObject.snapshotMaxFieldBytes-ne 65536-or$detailObject.snapshotMaxBytes-ne 4194304-or$detailObject.snapshotEncoding-cne'length-prefixed-utf8-v1'){Fail-M12 'OUTPUT'}
    $resultObject = Read-M12ClosedJson (Join-Path $BuildRoot $resultName) @('caseId','status','executions','skipped','notRun','failed','runId','commitSha','environmentId'); Assert-M12JsonTypes (Join-Path $BuildRoot $resultName) @{ caseId='string'; status='string'; executions='integer'; skipped='integer'; notRun='integer'; failed='integer'; runId='string'; commitSha='string'; environmentId='string' }
    $parsedRunId=[Guid]::Empty; if(-not[Guid]::TryParse([string]$resultObject.runId,[ref]$parsedRunId)-or$parsedRunId.ToString('D')-cne$RunId-or$resultObject.caseId-cne$CaseId-or$resultObject.status-cne'passed'-or$resultObject.executions-ne 1-or$resultObject.skipped-ne 0-or$resultObject.notRun-ne 0-or$resultObject.failed-ne 0-or$resultObject.commitSha-cne$Commit-or$resultObject.environmentId-cne$Environment){Fail-M12 'OUTPUT'}
    $provenanceObject = Read-M12ClosedJson (Join-Path $BuildRoot $provenanceName) @('artifactId','kind','producerId','artifactSha256','artifactSize','commitSha','runId','environmentId','createdAtUtc'); Assert-M12JsonTypes (Join-Path $BuildRoot $provenanceName) @{ artifactId='string'; kind='string'; producerId='string'; artifactSha256='string'; artifactSize='integer'; commitSha='string'; runId='string'; environmentId='string'; createdAtUtc='string' }
    if($provenanceObject.artifactId-cne'm12-sqlserver-passive'-or$provenanceObject.kind-cne$artifactKind-or$provenanceObject.producerId-cne$producerId-or$provenanceObject.artifactSha256-cne$hash-or[long]$provenanceObject.artifactSize-ne[long](Get-Item -LiteralPath (Join-Path $BuildRoot $detailName)).Length-or$provenanceObject.commitSha-cne$Commit-or$provenanceObject.runId-cne$RunId-or$provenanceObject.environmentId-cne$Environment){Fail-M12 'OUTPUT'};try{$at=[DateTimeOffset]::ParseExact([string]$provenanceObject.createdAtUtc,"yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)}catch{Fail-M12 'OUTPUT'};if($at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",[Globalization.CultureInfo]::InvariantCulture)-cne$provenanceObject.createdAtUtc-or$at-lt[DateTimeOffset]::UtcNow.AddMinutes(-5)-or$at-gt[DateTimeOffset]::UtcNow.AddMinutes(5)){Fail-M12 'OUTPUT'}
}

$root = $null; $outputRoot = $null; $build = $null; $cleanupTarget = $null
try {
    $authority = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $root = Resolve-M12CanonicalRoot $RepositoryRoot $authority
    Assert-M12ReleaseHost $Profile $case.Environment @('release-windows-server-2022','release-windows-server-2025')
    if ([Environment]::GetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER_ENVIRONMENT') -cne $case.Environment) { Fail-M12 'HOST' }
    $connection = Get-M12RequiredValue 'SQLOBSERVER_RELEASE_SQLSERVER'; Assert-SqlConnectionContract $connection
    $contract = Assert-SqlContract $root
    $commit = Get-M12CurrentCommit $root
    $outputRoot = [IO.Path]::GetFullPath((Join-Path $root 'TestResults/m12')); Assert-M12TrustedTree (Join-Path $root 'TestResults'); Assert-M12TrustedTree $outputRoot
    $runId = [Guid]::NewGuid().ToString('D'); $build = Join-Path $outputRoot ".pending-$runId"; $cleanupTarget = $build; [IO.Directory]::CreateDirectory($build) | Out-Null; Assert-M12TrustedTree $build
    $trxDirectory = Join-Path $build '.trx'; [IO.Directory]::CreateDirectory($trxDirectory) | Out-Null
    $run = Invoke-SqlReleaseTest $root (Join-Path $trxDirectory 'm12-sqlserver.trx')
    # The live test returns the canonical snapshot digest after exact before/after comparison.
    Remove-M12RawTestDirectory $trxDirectory
    Write-SqlArtifacts $build $root $commit $case.Environment $runId $run
    $expectedArtifactNames = @('sqlserver-nonmutation-evidence.json','sqlserver-nonmutation-result.json','sqlserver-nonmutation-provenance.json')
    $builtEntries = @(Get-ChildItem -LiteralPath $build -Force)
    if ($builtEntries.Count -ne 3 -or (@($builtEntries | ForEach-Object Name) | Sort-Object) -join '|' -cne ($expectedArtifactNames | Sort-Object) -join '|') { Fail-M12 'OUTPUT' }
    $preMove = @{}
    foreach ($entry in $builtEntries) {
        $identity=Assert-M12OutputFileIdentity $entry.FullName $build
        if (-not $script:M12OutputIdentities.ContainsKey($entry.Name) -or $identity.Volume-ne$script:M12OutputIdentities[$entry.Name].Volume -or $identity.High-ne$script:M12OutputIdentities[$entry.Name].High -or $identity.Low-ne$script:M12OutputIdentities[$entry.Name].Low -or $identity.Links-ne 1 -or (Get-M12Sha256 $entry.FullName)-cne$script:M12OutputHashes[$entry.Name] -or [Convert]::ToHexString([IO.File]::ReadAllBytes($entry.FullName))-cne[Convert]::ToHexString($script:M12OutputBytes[$entry.Name])) { Fail-M12 'OUTPUT' }
        $preMove[$entry.Name]=[pscustomobject]@{ Identity=$identity; Hash=$script:M12OutputHashes[$entry.Name]; Bytes=$script:M12OutputBytes[$entry.Name] }
    }
    $verify = Join-Path $outputRoot ".verify-$runId"; $final = Join-Path $outputRoot $runId
    [IO.Directory]::Move($build,$verify); $build = $null; $cleanupTarget = $verify
    Assert-M12TrustedTree $verify
    $verifiedEntries = @(Get-ChildItem -LiteralPath $verify -Force)
    if ($verifiedEntries.Count -ne 3 -or (@($verifiedEntries | ForEach-Object Name) | Sort-Object) -join '|' -cne ($expectedArtifactNames | Sort-Object) -join '|') { Fail-M12 'OUTPUT' }
    foreach ($entry in $verifiedEntries) { $identity=Assert-M12OutputFileIdentity $entry.FullName $verify; $before=$preMove[$entry.Name]; if($null-eq$before-or$identity.Volume-ne$before.Identity.Volume-or$identity.High-ne$before.Identity.High-or$identity.Low-ne$before.Identity.Low-or$identity.Links-ne 1-or(Get-M12Sha256 $entry.FullName)-cne$before.Hash-or[Convert]::ToHexString([IO.File]::ReadAllBytes($entry.FullName))-cne[Convert]::ToHexString($before.Bytes)){Fail-M12 'OUTPUT'}; [void](Read-M12ClosedJson $entry.FullName) }
    if (Test-Path -LiteralPath $final) { Fail-M12 'OUTPUT' }
    # Keep canonical files open while the final rename is performed. This
    # preserves identity and prevents a replacement/hardlink race.
    $held = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach ($entry in $verifiedEntries) { $stream=[IO.FileStream]::new($entry.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$held.Add($stream);$heldIdentity=[M12OutputFile]::Read($stream);$before=$preMove[$entry.Name];if($heldIdentity.Volume-ne$before.Identity.Volume-or$heldIdentity.High-ne$before.Identity.High-or$heldIdentity.Low-ne$before.Identity.Low-or$heldIdentity.Links-ne 1){Fail-M12 'OUTPUT'} }
        $cleanupTarget = $null
        [IO.Directory]::Move($verify,$final)
    } finally { foreach ($stream in $held) { $stream.Dispose() } }
    exit 0
} catch { [Console]::Error.WriteLine('M12-SQLSERVER-PRODUCER-HOST'); exit 1 }
finally { if ($null -ne $cleanupTarget -and $null -ne $outputRoot) { Remove-M12SafeTree $cleanupTarget $outputRoot } }
