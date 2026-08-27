[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [ValidateSet('Release')]
    [string] $Profile = 'Release'
)

# This producer is deliberately an external-evidence harness.  It never
# changes the matrix or promotes the pending MCP lane; it only publishes a
# run-local artifact after the live test has passed all of its prerequisites.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProducerId = 'm12-mcp-harness'
$CaseId = 'm12-mcp-protocol'
$ArtifactKind = 'mcp-protocol-evidence'
$EndpointVariable = 'SQLOBSERVER_RELEASE_MCP_ENDPOINT'
$EnvironmentVariable = 'SQLOBSERVER_RELEASE_MCP_ENVIRONMENT'
$DeniedTargetVariable = 'SQLOBSERVER_RELEASE_MCP_DENIED_INSTANCE_ID'
$MaximumOutput = 65536
$MaximumError = 8192
$MaximumMilliseconds = 180000
$MaximumJsonBytes = 32768
$MaximumTotalJsonBytes = 65536
$ApprovedCurrentProtocol = '2026-07-28'
$ApprovedDownlevelProtocol = '2025-11-25'
$ApprovedToolCount = 25
$ApprovedCatalogDigest = '3787BD8A9511035F08781766E684083EF18F0CB7047BBF8FD8D1B50B61418D0C'
$ApprovedServerVersion = 'm11-2.2.0+catalog-3787BD8A9511035F08781766E684083EF18F0CB7047BBF8FD8D1B50B61418D0C'
$ApprovedContractSha256 = 'db19a2f428de3a348539c78ce4605ef430b37f2cfdba3efcc1654304f1c0bdba'
$ApprovedContractSchemaSha256 = '13344931e7f9660bdc69c5590a0782d24dd5f69250fbb6c83f169714fe8eb501'

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
            CloseHandle(threadHandle); threadHandle = IntPtr.Zero; stdoutWrite.Dispose(); stdoutWrite = null!; stderrWrite.Dispose(); stderrWrite = null!;
            return new M12SuspendedProcess(processHandle, jobHandle, information.dwProcessId, stdoutRead, stderrRead);
        }
        catch
        {
            try { if (processHandle != IntPtr.Zero) TerminateProcess(processHandle, 1); } catch { }
            try { if (processHandle != IntPtr.Zero) WaitForSingleObject(processHandle, 2000); } catch { }
            try { if (jobHandle != IntPtr.Zero) TerminateJobObject(jobHandle, 1); } catch { }
            try { if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle); } catch { }
            try { if (processHandle != IntPtr.Zero) CloseHandle(processHandle); } catch { }
            try { if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle); } catch { }
            try { stdoutRead?.Dispose(); stdoutWrite?.Dispose(); stderrRead?.Dispose(); stderrWrite?.Dispose(); } catch { }
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
    public void Dispose() { try { if (ActiveProcesses > 0) Terminate(); } catch { } try { _stdoutRead.Dispose(); _stderrRead.Dispose(); } catch { } try { if (_processHandle != IntPtr.Zero) CloseHandle(_processHandle); if (_jobHandle != IntPtr.Zero) CloseHandle(_jobHandle); } catch { } }
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

function Fail([string] $Code) { throw "M12-MCP-$Code" }

function Assert-TrustedTree([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $cursor = $root
    foreach ($component in ($full.Substring($root.Length) -split '[\\/]')) {
        if ([string]::IsNullOrEmpty($component)) { continue }
        $cursor = Join-Path $cursor $component
        if (-not (Test-Path -LiteralPath $cursor)) { continue }
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail 'PATH' }
    }
}

function Resolve-CanonicalRoot([string] $Requested) {
    $authority = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $requested = (Resolve-Path -LiteralPath $Requested).Path
    Assert-TrustedTree $authority; Assert-TrustedTree $requested
    if (-not [String]::Equals($authority.TrimEnd('\\', '/'), $requested.TrimEnd('\\', '/'), [StringComparison]::OrdinalIgnoreCase)) { Fail 'ROOT' }
    foreach ($relative in @('tools/run-m12-mcp-certification.ps1', 'tests/SqlObserver.McpContractTests/SqlObserver.McpContractTests.csproj', 'src/SqlObserver.McpStdio/bin/Release/net10.0/SqlObserver.McpStdio.exe', 'release/certification/m12-mcp-protocol-contract.v1.json', 'release/certification/m12-mcp-protocol-contract.v1.schema.json', 'release/certification/m12-mcp-protocol-contract.v1.sha256')) {
        $path = Join-Path $requested $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail 'INPUT' }
        Assert-TrustedTree $path
    }
    return $requested
}

function Get-RequiredValue([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([String]::IsNullOrWhiteSpace($value) -or $value.Length -gt 4096) { Fail 'INPUT' }
    return $value
}

function Assert-ReleaseHost([string] $Root) {
    if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem -or
        -not [Environment]::Is64BitProcess -or
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64 -or
        [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64 -or
        $Profile -cne 'Release') { Fail 'HOST' }
    if ([Environment]::GetEnvironmentVariable('SQLOBSERVER_VALIDATION_PROFILE') -cne 'Release') { Fail 'HOST' }
    $environment = Get-RequiredValue $EnvironmentVariable
    if ($environment -cne 'release-windows-server-2022' -and $environment -cne 'release-windows-server-2025') { Fail 'HOST' }
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $expectedCaption = switch ($environment) {
            'release-windows-server-2022' { 'Windows Server 2022' }
            'release-windows-server-2025' { 'Windows Server 2025' }
            default { Fail 'HOST' }
        }
        if ($os.ProductType -notin @(2, 3) -or -not [String]::StartsWith([string]$os.Caption, $expectedCaption, [StringComparison]::Ordinal)) { Fail 'HOST' }
    } catch { Fail 'HOST' }
    $outputRoot = [IO.Path]::GetFullPath((Join-Path $Root 'TestResults/m12'))
    $rootPrefix = $Root.TrimEnd('\\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $outputRoot.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { Fail 'PATH' }
    if (Test-Path -LiteralPath $outputRoot) { Assert-TrustedTree $outputRoot }
    return $outputRoot
}

function Assert-Endpoint([string] $Text) {
    $uri = $null
    if (-not [Uri]::TryCreate($Text, [UriKind]::Absolute, [ref] $uri) -or $null -eq $uri -or
        $uri.Scheme -cne 'https' -or $uri.AbsolutePath -cne '/mcp' -or
        $uri.UserInfo.Length -ne 0 -or $uri.Query.Length -ne 0 -or $uri.Fragment.Length -ne 0) { Fail 'ENDPOINT' }
}

function Read-ValidatedLabAttestation([string] $Root, [string] $Path, [string] $ExpectedHash, [string] $Environment, [string] $ExpectedCommit, [string] $ExpectedDeniedId) {
    $full = [IO.Path]::GetFullPath($Path)
    $labRoot = [IO.Path]::GetFullPath((Join-Path $Root 'TestResults/m12-lab-inputs')).TrimEnd('\\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($labRoot, [StringComparison]::OrdinalIgnoreCase) -or $full.Substring($labRoot.Length) -cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\denied-target\.json$') { Fail 'INPUT' }
    $runIdFromPath = $full.Substring($labRoot.Length, 36)
    if ($ExpectedHash -cnotmatch '^[0-9a-f]{64}$' -or $ExpectedCommit -cnotmatch '^[0-9a-f]{40}$' -or $ExpectedDeniedId -cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$') { Fail 'INPUT' }
    Assert-TrustedTree $full
    $stream = [IO.FileStream]::new($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt 16384) { Fail 'INPUT' }
        $identity = [M12OutputFile]::Read($stream)
        if ($identity.Links -ne 1 -or -not [String]::Equals((Normalize-NativePath $identity.FinalPath), $full, [StringComparison]::OrdinalIgnoreCase)) { Fail 'INPUT' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { Fail 'INPUT' }
            $offset += $read
        }
        if (-not [String]::Equals(([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant(), $ExpectedHash, [StringComparison]::Ordinal)) { Fail 'INPUT' }
        if (($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) -or ($bytes | Where-Object { $_ -eq 0x0D }).Count -ne 0 -or ($bytes | Where-Object { $_ -eq 0x0A }).Count -ne 1 -or $bytes[-1] -ne 0x0A) { Fail 'INPUT' }
        try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes) } catch { Fail 'INPUT' }
        try { $null = ConvertFrom-Json -InputObject $text -DateKind String -ErrorAction Stop } catch { Fail 'INPUT' }
        $jsonOptions = [Text.Json.JsonDocumentOptions]::new()
        try { $document = [Text.Json.JsonDocument]::Parse($bytes, $jsonOptions) } catch { Fail 'INPUT' }
        try {
            $object = $document.RootElement
            if ($object.ValueKind -ne [Text.Json.JsonValueKind]::Object) { Fail 'INPUT' }
            $names = @('schemaVersion','attestationId','runId','environmentId','commitSha','deniedTargetId','exists','targetRevision','generatedAtUtc')
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $properties = @($object.EnumerateObject())
            if ($properties.Count -ne $names.Count) { Fail 'INPUT' }
            for ($i = 0; $i -lt $properties.Count; $i++) {
                if (-not $seen.Add($properties[$i].Name) -or $properties[$i].Name -cne $names[$i]) { Fail 'INPUT' }
            }
            $schema = $object.GetProperty('schemaVersion')
            [int]$schemaValue = 0
            if (-not $schema.TryGetInt32([ref]$schemaValue) -or $schemaValue -ne 1) { Fail 'INPUT' }
            foreach ($name in @('attestationId','runId','deniedTargetId')) {
                $value = $object.GetProperty($name)
                [Guid]$guid = [Guid]::Empty
                if ($value.ValueKind -ne [Text.Json.JsonValueKind]::String -or -not [Guid]::TryParseExact($value.GetString(), 'D', [ref]$guid)) { Fail 'INPUT' }
            }
            if ($object.GetProperty('runId').GetString() -cne $runIdFromPath) { Fail 'INPUT' }
            if ($object.GetProperty('environmentId').ValueKind -ne [Text.Json.JsonValueKind]::String -or $object.GetProperty('environmentId').GetString() -cne $Environment) { Fail 'INPUT' }
            if ($object.GetProperty('commitSha').ValueKind -ne [Text.Json.JsonValueKind]::String -or $object.GetProperty('commitSha').GetString() -cne $ExpectedCommit) { Fail 'INPUT' }
            if ($object.GetProperty('deniedTargetId').GetString() -cne $ExpectedDeniedId) { Fail 'INPUT' }
            if ($object.GetProperty('exists').ValueKind -ne [Text.Json.JsonValueKind]::True) { Fail 'INPUT' }
            $revision = $object.GetProperty('targetRevision'); [long]$revisionValue = 0
            if (-not $revision.TryGetInt64([ref]$revisionValue) -or $revisionValue -le 0 -or $revisionValue -gt 2147483647) { Fail 'INPUT' }
            $generated = $object.GetProperty('generatedAtUtc')
            if ($generated.ValueKind -ne [Text.Json.JsonValueKind]::String -or $generated.GetString() -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$') { Fail 'INPUT' }
            try { $timestamp = [DateTimeOffset]::ParseExact($generated.GetString(), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal) } catch { Fail 'INPUT' }
            if ($timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture) -cne $generated.GetString() -or (([DateTimeOffset]::UtcNow - $timestamp).TotalHours -gt 24) -or (([DateTimeOffset]::UtcNow - $timestamp).TotalMinutes -lt -5)) { Fail 'INPUT' }
            return [pscustomobject]@{ DeniedTargetId = $ExpectedDeniedId }
        } finally { $document.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-ProtocolContract([string] $Root) {
    $contractPath = Join-Path $Root 'release/certification/m12-mcp-protocol-contract.v1.json'
    $schemaPath = Join-Path $Root 'release/certification/m12-mcp-protocol-contract.v1.schema.json'
    $pinPath = Join-Path $Root 'release/certification/m12-mcp-protocol-contract.v1.sha256'
    $bytes = [IO.File]::ReadAllBytes($contractPath)
    $pinBytes = [IO.File]::ReadAllBytes($pinPath)
    if ($pinBytes.Length -lt 3 -or ($pinBytes[0] -eq 0xEF -and $pinBytes[1] -eq 0xBB -and $pinBytes[2] -eq 0xBF) -or ($pinBytes | Where-Object { $_ -eq 0x0D }).Count -ne 0 -or ($pinBytes | Where-Object { $_ -eq 0x0A }).Count -ne 2) { Fail 'CATALOG' }
    try { $pinText = [Text.UTF8Encoding]::new($false, $true).GetString($pinBytes) } catch { Fail 'CATALOG' }
    if ($pinText -cne "$ApprovedContractSha256  m12-mcp-protocol-contract.v1.json`n$ApprovedContractSchemaSha256  m12-mcp-protocol-contract.v1.schema.json`n") { Fail 'CATALOG' }
    if ((Get-FileHash -LiteralPath $contractPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ApprovedContractSha256 -or
        (Get-FileHash -LiteralPath $schemaPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ApprovedContractSchemaSha256) { Fail 'CATALOG' }
    try {
        $contract = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        if (-not (Test-Json -Json ([Text.Encoding]::UTF8.GetString($bytes)) -SchemaFile $schemaPath)) { Fail 'CATALOG' }
    } catch { Fail 'CATALOG' }
    $names = @($contract.PSObject.Properties.Name)
    if (($names -join '|') -cne '$schema|schemaVersion|contractId|currentProtocol|downlevelProtocol|toolCount|catalogDigest|serverVersion' -or
        $contract.'$schema' -cne 'm12-mcp-protocol-contract.v1.schema.json' -or $contract.schemaVersion -ne 1 -or
        $contract.contractId -cne $CaseId -or $contract.currentProtocol -cne $ApprovedCurrentProtocol -or
        $contract.downlevelProtocol -cne $ApprovedDownlevelProtocol -or $contract.toolCount -ne $ApprovedToolCount -or
        $contract.catalogDigest -cne $ApprovedCatalogDigest -or $contract.serverVersion -cne $ApprovedServerVersion) { Fail 'CATALOG' }
    $source = [IO.File]::ReadAllText((Join-Path $Root 'src/SqlObserver.Mcp/McpRuntime.cs'))
    if ($source -notmatch ('CurrentProtocolVersion\s*=\s*"' + [regex]::Escape($ApprovedCurrentProtocol) + '"') -or
        $source -notmatch ('DownlevelProtocolVersion\s*=\s*"' + [regex]::Escape($ApprovedDownlevelProtocol) + '"') -or
        $source -notmatch ('ApprovedCatalogDigest\s*=\s*"' + [regex]::Escape($ApprovedCatalogDigest) + '"') -or
        $source -notmatch ('ServerVersion\s*=\s*"' + [regex]::Escape($ApprovedServerVersion) + '"')) { Fail 'CATALOG' }
}

function Get-TrustedExecutable([string] $Name) {
    if ($Name -cne 'dotnet') { Fail 'EXECUTABLE' }
    $path = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail 'EXECUTABLE' }
    Assert-TrustedTree $path
    return $path
}

function Read-CappedProcessStreams([Diagnostics.Process] $Process, [object] $Job = $null, [IO.Stream] $Stdout = $null, [IO.Stream] $Stderr = $null) {
    if ($null -eq $Stdout) { $Stdout = $Process.StandardOutput.BaseStream }
    if ($null -eq $Stderr) { $Stderr = $Process.StandardError.BaseStream }
    $out = [IO.MemoryStream]::new(); $err = [IO.MemoryStream]::new()
    $outBuffer = New-Object byte[] 4096; $errBuffer = New-Object byte[] 4096
    $outTask = $Stdout.ReadAsync($outBuffer, 0, $outBuffer.Length)
    $errTask = $Stderr.ReadAsync($errBuffer, 0, $errBuffer.Length)
    $deadline = [Environment]::TickCount64 + $MaximumMilliseconds
    $outDone = $false; $errDone = $false; $tooLarge = $false; $timedOut = $false
    while (-not ($outDone -and $errDone -and $Process.HasExited)) {
        [Threading.Tasks.Task]::Delay(25).GetAwaiter().GetResult()
        if (-not $outDone -and $outTask.IsCompleted) { $count = $outTask.GetAwaiter().GetResult(); if ($count -eq 0) { $outDone = $true } elseif ($out.Length + $count -gt $MaximumOutput) { $tooLarge = $true } else { $out.Write($outBuffer, 0, $count); $outTask = $Stdout.ReadAsync($outBuffer, 0, $outBuffer.Length) } }
        if (-not $errDone -and $errTask.IsCompleted) { $count = $errTask.GetAwaiter().GetResult(); if ($count -eq 0) { $errDone = $true } elseif ($err.Length + $count -gt $MaximumError) { $tooLarge = $true } else { $err.Write($errBuffer, 0, $count); $errTask = $Stderr.ReadAsync($errBuffer, 0, $errBuffer.Length) } }
        if ($tooLarge -or [Environment]::TickCount64 -ge $deadline) {
            $timedOut = -not $tooLarge
            try { if ($null -ne $Job) { $Job.Terminate() } else { $Process.Kill($true) } } catch { try { $Process.Kill($true) } catch { } }
            break
        }
    }
    try { if (-not $Process.HasExited) { $Process.Kill($true); [void]$Process.WaitForExit(1000) } } catch { }
    return [pscustomobject]@{ Output = [Text.Encoding]::UTF8.GetString($out.ToArray()); Error = [Text.Encoding]::UTF8.GetString($err.ToArray()); TooLarge = $tooLarge; TimedOut = $timedOut }
}

function Assert-NoDescendants([int] $ParentPid) {
    try {
        $pending = [System.Collections.Generic.Queue[int]]::new(); $pending.Enqueue($ParentPid); $seen = [System.Collections.Generic.HashSet[int]]::new()
        while ($pending.Count -gt 0 -and $seen.Count -le 256) {
            $parent = $pending.Dequeue(); if (-not $seen.Add($parent)) { continue }
            foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$parent" -ErrorAction Stop)) { $pending.Enqueue([int]$child.ProcessId) }
        }
        if ($seen.Count -gt 256 -or $seen.Count -ne 1) { Fail 'PROCESS' }
    } catch { Fail 'PROCESS' }
}

function Assert-ObservedPidsExited([int[]] $ProcessIds) {
    $deadline = [Environment]::TickCount64 + 2000
    do {
        $live = @($ProcessIds | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($live.Count -eq 0) { return }
        Start-Sleep -Milliseconds 25
    } while ([Environment]::TickCount64 -le $deadline)
    Fail 'PROCESS'
}

function Invoke-LiveTest([string] $Root, [string] $BuildRoot, [string] $StdioPath, [psobject] $ValidatedAttestation) {
    if ($null -eq $ValidatedAttestation -or $ValidatedAttestation.DeniedTargetId -cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$') { Fail 'INPUT' }
    $dotnet = Get-TrustedExecutable 'dotnet'
    $project = Join-Path $Root 'tests/SqlObserver.McpContractTests/SqlObserver.McpContractTests.csproj'
    $trxDirectory = Join-Path $BuildRoot '.trx'
    [IO.Directory]::CreateDirectory($trxDirectory) | Out-Null
    Assert-TrustedTree $trxDirectory
    $trx = Join-Path $trxDirectory 'm12-mcp.trx'
    $arguments = @('test', $project, '--configuration', 'Release', '--no-restore', '--no-build', '--filter', 'Category=RequiresM12McpRelease', '--logger', "trx;LogFileName=$trx")
    $native = $null
    $stdioHandle = $null
    try {
        # Open and validate the canonical image before creating the runner;
        # the deny-write/delete handle remains held through suspended launch,
        # job assignment, and resume.
        $stdioHandle = [IO.FileStream]::new($StdioPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $stdioIdentity = [M12OutputFile]::Read($stdioHandle)
        if ($stdioIdentity.Links -ne 1 -or -not [String]::Equals((Normalize-NativePath $stdioIdentity.FinalPath), [IO.Path]::GetFullPath($StdioPath), [StringComparison]::OrdinalIgnoreCase)) { Fail 'EXECUTABLE' }
        $native = [M12SuspendedProcess]::Start($dotnet, [string[]]$arguments, $Root)
    } catch {
        try { if ($null -ne $stdioHandle) { $stdioHandle.Dispose() } } catch { }
        Fail 'PROCESS'
    }
    $process = $native.ManagedProcess
    $processId = $process.Id
    $job = $native
    $observedProcessIds = @($job.ProcessIds)
    $stdout = $native.StandardOutput
    $stderr = $native.StandardError
    $cleanupFailed = $false
    try {
        $streams = Read-CappedProcessStreams $process $job $stdout $stderr
        if ($streams.TooLarge -or $streams.TimedOut -or $process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $trx -PathType Leaf)) { Fail 'TEST' }
        $trxInfo = Get-Item -LiteralPath $trx -Force
        if ($trxInfo.PSIsContainer -or $trxInfo.Length -lt 1 -or $trxInfo.Length -gt 16777216) { Fail 'TEST' }
        $trxIdentity = Assert-OutputFileIdentity $trx $trxDirectory
        if ($trxIdentity.Links -ne 1) { Fail 'TEST' }
        $exclusive = [IO.FileStream]::new($trx, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            $trxBytes = [byte[]]::new([int]$trxInfo.Length)
            $offset = 0
            while ($offset -lt $trxBytes.Length) {
                $read = $exclusive.Read($trxBytes, $offset, $trxBytes.Length - $offset)
                if ($read -le 0) { Fail 'TEST' }
                $offset += $read
            }
        } finally { $exclusive.Dispose() }
        $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null
        $xmlStream = [IO.MemoryStream]::new($trxBytes)
        try { $xml.Load($xmlStream) } finally { $xmlStream.Dispose() }
        $results = @($xml.SelectNodes("//*[local-name()='UnitTestResult']"))
        $testId = [string]$results[0].testId
        $unitTests = @($xml.SelectNodes("//*[local-name()='UnitTest']"))
        $unitTest = $unitTests | Where-Object { [string]$_.id -ceq $testId }
        if ($results.Count -ne 1 -or $results[0].outcome -cne 'Passed' -or $null -eq $unitTest -or [string]$unitTest.name -cne 'LiveReleaseMcpProtocolAndProcessBoundaryAreCertified') { Fail 'TEST' }
        return [pscustomobject]@{ TestCount = $results.Count; StdoutBytes = [Text.Encoding]::UTF8.GetByteCount($streams.Output); StderrBytes = [Text.Encoding]::UTF8.GetByteCount($streams.Error) }
    } finally {
        try {
            if ($null -ne $job) {
                if ($job.ActiveProcesses -gt 0) { $job.Terminate() }
                if (-not $job.WaitForZero(2000)) { $cleanupFailed = $true }
            } else { $cleanupFailed = $true }
        } catch { $cleanupFailed = $true }
        try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(1000) } } catch { $cleanupFailed = $true }
        try { Assert-NoDescendants $processId } catch { $cleanupFailed = $true }
        try { Assert-ObservedPidsExited $observedProcessIds } catch { $cleanupFailed = $true }
        try { $stdout.Dispose(); $stderr.Dispose() } catch { $cleanupFailed = $true }
        if ($null -ne $job) { $job.Dispose() }
        $process.Dispose()
        if ($null -ne $stdioHandle) { $stdioHandle.Dispose() }
        try { Remove-SafeTree $trxDirectory $BuildRoot; if (Test-Path -LiteralPath $trxDirectory) { $cleanupFailed = $true } } catch { $cleanupFailed = $true }
        if ($cleanupFailed) { Fail 'PROCESS' }
    }
}

function Normalize-NativePath([string] $Path) {
    if ($Path.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { return '\\' + $Path.Substring(8) }
    if ($Path.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)) { return $Path.Substring(4) }
    return $Path
}

function Assert-OutputFileIdentity([string] $Path, [string] $ExpectedDirectory) {
    $full = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetFullPath($ExpectedDirectory).TrimEnd('\\', '/')
    if (-not $full.StartsWith($directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notmatch '^[a-z0-9-]+\.(json|trx)$') { Fail 'OUTPUT' }
    Assert-TrustedTree $full
    $stream = [IO.FileStream]::new($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $identity = [M12OutputFile]::Read($stream)
        if ($identity.Links -ne 1) { Fail 'OUTPUT' }
        $final = [IO.Path]::GetFullPath((Normalize-NativePath $identity.FinalPath))
        if (-not [String]::Equals($final, $full, [StringComparison]::OrdinalIgnoreCase)) { Fail 'OUTPUT' }
        return $identity
    } finally { $stream.Dispose() }
}

function Write-Utf8Json([string] $Path, [object] $Value, [string] $ExpectedDirectory) {
    $json = $Value | ConvertTo-Json -Compress -Depth 12
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
    if ($bytes.Length -lt 1 -or $bytes.Length -gt $MaximumJsonBytes -or ($bytes | Where-Object { $_ -eq 0x0D }).Count -ne 0 -or ($bytes | Where-Object { $_ -eq 0x0A }).Count -ne 1) { Fail 'OUTPUT' }
    $full = [IO.Path]::GetFullPath($Path)
    Assert-TrustedTree $ExpectedDirectory
    $stream = [IO.FileStream]::new($full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $before = $null
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $before = [M12OutputFile]::Read($stream)
        if ($before.Links -ne 1 -or -not [String]::Equals((Normalize-NativePath $before.FinalPath), $full, [StringComparison]::OrdinalIgnoreCase)) { Fail 'OUTPUT' }
    } finally { $stream.Dispose() }
    $after = Assert-OutputFileIdentity $full $ExpectedDirectory
    if ($null -eq $before -or $after.Volume -ne $before.Volume -or $after.High -ne $before.High -or $after.Low -ne $before.Low -or $after.Links -ne 1) { Fail 'OUTPUT' }
    $fileName = [IO.Path]::GetFileName($full)
    if ($script:OutputIdentities.ContainsKey($fileName)) { Fail 'OUTPUT' }
    $script:OutputIdentities[$fileName] = $after
    $script:OutputHashes[$fileName] = Get-Sha256 $full
    $script:OutputBytes[$fileName] = $bytes
}

function Read-ClosedJson([string] $Path, [string[]] $ExpectedNames) {
    $info = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($info.PSIsContainer -or $info.Length -lt 1 -or $info.Length -gt $MaximumJsonBytes) { Fail 'OUTPUT' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ne $info.Length -or $bytes.Length -lt 1 -or $bytes[-1] -ne 0x0A -or ($bytes | Where-Object { $_ -eq 0x0D }).Count -ne 0 -or ($bytes | Where-Object { $_ -eq 0x0A }).Count -ne 1 -or ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) { Fail 'OUTPUT' }
    $document = $null
    try {
        $document = [Text.Json.JsonDocument]::Parse($bytes)
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { Fail 'OUTPUT' }
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) { if (-not $seen.Add($property.Name)) { Fail 'OUTPUT' } }
        $value = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -Depth 16
    } catch { Fail 'OUTPUT' } finally { if ($null -ne $document) { $document.Dispose() } }
    $names = @($value.PSObject.Properties.Name)
    if (($names -join '|') -cne ($ExpectedNames -join '|')) { Fail 'OUTPUT' }
    return $value
}

function Assert-JsonTypes([string] $Path, [hashtable] $ExpectedTypes) {
    $document = $null
    try {
        $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($file.PSIsContainer -or $file.Length -lt 1 -or $file.Length -gt $MaximumJsonBytes) { Fail 'OUTPUT' }
        $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllBytes($Path))
        $kinds = @{ string = [Text.Json.JsonValueKind]::String; number = [Text.Json.JsonValueKind]::Number; integer = [Text.Json.JsonValueKind]::Number }
        foreach ($entry in $ExpectedTypes.GetEnumerator()) {
            $element = $document.RootElement.GetProperty($entry.Key)
            if ($element.ValueKind -ne $kinds[$entry.Value]) { Fail 'OUTPUT' }
            if ($entry.Value -eq 'integer') { $integer = 0; if (-not $element.TryGetInt32([ref]$integer)) { Fail 'OUTPUT' } }
        }
    } catch { Fail 'OUTPUT' } finally { if ($null -ne $document) { $document.Dispose() } }
}

function Assert-PublishedJson([string] $Directory, [string] $RunId, [string] $Commit, [string] $Environment, [DateTimeOffset] $Created) {
    $artifactName = 'mcp-protocol-evidence.json'; $evidenceName = 'mcp-protocol-result.json'; $sidecarName = 'mcp-protocol-provenance.json'
    $artifact = Read-ClosedJson (Join-Path $Directory $artifactName) @('schemaVersion','caseId','producerId','kind','result','protocolCurrent','protocolDownlevel','toolCount','catalogDigest','serverVersion','testCount')
    Assert-JsonTypes (Join-Path $Directory $artifactName) @{ schemaVersion='integer'; caseId='string'; producerId='string'; kind='string'; result='string'; protocolCurrent='string'; protocolDownlevel='string'; toolCount='integer'; catalogDigest='string'; serverVersion='string'; testCount='integer' }
    if ($artifact.schemaVersion -ne 1 -or $artifact.caseId -cne $CaseId -or $artifact.producerId -cne $ProducerId -or $artifact.kind -cne $ArtifactKind -or $artifact.result -cne 'passed' -or $artifact.protocolCurrent -cne $ApprovedCurrentProtocol -or $artifact.protocolDownlevel -cne $ApprovedDownlevelProtocol -or $artifact.toolCount -ne $ApprovedToolCount -or $artifact.catalogDigest -cne $ApprovedCatalogDigest -or $artifact.serverVersion -cne $ApprovedServerVersion -or $artifact.testCount -ne 1) { Fail 'OUTPUT' }
    $evidence = Read-ClosedJson (Join-Path $Directory $evidenceName) @('caseId','status','executions','skipped','notRun','failed','runId','commitSha','environmentId')
    Assert-JsonTypes (Join-Path $Directory $evidenceName) @{ caseId='string'; status='string'; executions='integer'; skipped='integer'; notRun='integer'; failed='integer'; runId='string'; commitSha='string'; environmentId='string' }
    if ($evidence.caseId -cne $CaseId -or $evidence.status -cne 'passed' -or $evidence.executions -ne 1 -or $evidence.skipped -ne 0 -or $evidence.notRun -ne 0 -or $evidence.failed -ne 0 -or $evidence.runId -cne $RunId -or $evidence.commitSha -cne $Commit -or $evidence.environmentId -cne $Environment) { Fail 'OUTPUT' }
    $sidecar = Read-ClosedJson (Join-Path $Directory $sidecarName) @('artifactId','kind','producerId','artifactSha256','artifactSize','commitSha','runId','environmentId','createdAtUtc')
    Assert-JsonTypes (Join-Path $Directory $sidecarName) @{ artifactId='string'; kind='string'; producerId='string'; artifactSha256='string'; artifactSize='integer'; commitSha='string'; runId='string'; environmentId='string'; createdAtUtc='string' }
    $artifactHash = Get-Sha256 (Join-Path $Directory $artifactName); $artifactSize = (Get-Item -LiteralPath (Join-Path $Directory $artifactName)).Length
    $totalSize = 0L
    foreach ($name in @($artifactName, $evidenceName, $sidecarName)) {
        $path = Join-Path $Directory $name; $info = Get-Item -LiteralPath $path -Force
        if ($info.PSIsContainer -or $info.Length -lt 1 -or $info.Length -gt $MaximumJsonBytes) { Fail 'OUTPUT' }
        $totalSize += [long]$info.Length
        $raw = [IO.File]::ReadAllBytes($path)
        if ($raw.Length -ne $info.Length -or $raw.Length -gt $MaximumJsonBytes -or $raw.Length -gt 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF -or
            $raw[-1] -ne 0x0A -or ($raw | Where-Object { $_ -eq 0x0D }).Count -ne 0 -or ($raw | Where-Object { $_ -eq 0x0A }).Count -ne 1) { Fail 'OUTPUT' }
        try { [Text.UTF8Encoding]::new($false, $true).GetString($raw) | Out-Null } catch { Fail 'OUTPUT' }
        if ($script:OutputHashes.ContainsKey($name) -and (Get-Sha256 $path) -cne $script:OutputHashes[$name]) { Fail 'OUTPUT' }
        if ($script:OutputBytes.ContainsKey($name) -and [Convert]::ToHexString($raw) -cne [Convert]::ToHexString($script:OutputBytes[$name])) { Fail 'OUTPUT' }
    }
    if ($totalSize -gt $MaximumTotalJsonBytes) { Fail 'OUTPUT' }
    if ($sidecar.artifactId -cne 'm12-mcp-protocol-evidence' -or $sidecar.kind -cne $ArtifactKind -or $sidecar.producerId -cne $ProducerId -or $sidecar.artifactSha256 -cne $artifactHash -or [long]$sidecar.artifactSize -ne [long]$artifactSize -or $sidecar.commitSha -cne $Commit -or $sidecar.runId -cne $RunId -or $sidecar.environmentId -cne $Environment) { Fail 'OUTPUT' }
    try { $sidecarAt = [DateTimeOffset]::ParseExact([string]$sidecar.createdAtUtc, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal) } catch { Fail 'OUTPUT' }
    if ($sidecarAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture) -cne $sidecar.createdAtUtc -or $sidecarAt -lt $Created.AddMinutes(-5) -or $sidecarAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { Fail 'OUTPUT' }
}

function Get-Sha256([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-RepoText([string] $Path, [int] $MaximumBytes = 65536) {
    Assert-TrustedTree $Path
    $info = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($info.PSIsContainer -or $info.Length -lt 1 -or $info.Length -gt $MaximumBytes) { Fail 'COMMIT' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    try { return [Text.UTF8Encoding]::new($false, $true).GetString($bytes) } catch { Fail 'COMMIT' }
}

function Resolve-RepoPath([string] $Base, [string] $Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains([char]0) -or $Relative.Contains('..')) { Fail 'COMMIT' }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Base $Relative))
    Assert-TrustedTree $resolved
    return $resolved
}

function Get-Commit([string] $Root) {
    $gitEntry = Join-Path $Root '.git'; Assert-TrustedTree $gitEntry
    $gitItem = Get-Item -LiteralPath $gitEntry -Force -ErrorAction Stop
    if ($gitItem.PSIsContainer) { $gitDirectory = [IO.Path]::GetFullPath($gitEntry) }
    else {
        $gitText = (Read-RepoText $gitEntry 4096).TrimEnd("`r", "`n")
        if ($gitText -notmatch '^gitdir: ([^\r\n]+)$') { Fail 'COMMIT' }
        $gitDirectory = if ([IO.Path]::IsPathRooted($Matches[1])) { [IO.Path]::GetFullPath($Matches[1]) } else { [IO.Path]::GetFullPath((Join-Path $Root $Matches[1])) }
        Assert-TrustedTree $gitDirectory
    }
    $commonDirectory = $gitDirectory
    $commondirPath = Join-Path $gitDirectory 'commondir'
    if (Test-Path -LiteralPath $commondirPath -PathType Leaf) {
        Assert-TrustedTree $commondirPath
        $commonBytes = [IO.File]::ReadAllBytes($commondirPath)
        if ($commonBytes.Length -ne 6 -or $commonBytes[0] -ne 0x2e -or $commonBytes[1] -ne 0x2e -or $commonBytes[2] -ne 0x2f -or $commonBytes[3] -ne 0x2e -or $commonBytes[4] -ne 0x2e -or $commonBytes[5] -ne 0x0a) { Fail 'COMMIT' }
        $commonDirectory = [IO.Path]::GetFullPath((Join-Path $gitDirectory '../..'))
        Assert-TrustedTree $commonDirectory
    }
    $head = (Read-RepoText (Join-Path $gitDirectory 'HEAD') 4096).TrimEnd("`r", "`n")
    if ($head -match '^[0-9a-f]{40}$') { return $head }
    if ($head -notmatch '^ref: (refs/[A-Za-z0-9._/-]+)$' -or $Matches[1].Contains('..')) { Fail 'COMMIT' }
    $reference = $Matches[1]
    $loosePath = Resolve-RepoPath $commonDirectory $reference
    if (Test-Path -LiteralPath $loosePath -PathType Leaf) {
        $value = (Read-RepoText $loosePath 4096).TrimEnd("`r", "`n")
        if ($value -notmatch '^[0-9a-f]{40}$') { Fail 'COMMIT' }
        return $value
    }
    $packed = Join-Path $commonDirectory 'packed-refs'
    if (-not (Test-Path -LiteralPath $packed -PathType Leaf)) { Fail 'COMMIT' }
    foreach ($line in (Read-RepoText $packed 1048576) -split "`n") {
        $entry = $line.TrimEnd("`r")
        if ($entry -match '^([0-9a-f]{40}) ' + [regex]::Escape($reference) + '$') { return $Matches[1] }
    }
    Fail 'COMMIT'
}

function Remove-SafeTree([string] $Path, [string] $AllowedParent) {
    $rootItem = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $rootItem) { return }
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
    $canonical = [IO.Path]::GetFullPath($Path)
    $parentCanonical = [IO.Path]::GetFullPath($AllowedParent).TrimEnd('\\', '/')
    if ($canonical.Contains(':', [StringComparison]::Ordinal) -and $canonical -notmatch '^[A-Za-z]:[\\/]') { return }
    if (-not $canonical.StartsWith($parentCanonical + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return }
    $parentLeaf = [IO.Path]::GetFileName($parentCanonical)
    $targetLeaf = [IO.Path]::GetFileName($canonical)
    $quarantinePattern = '^\.(pending|verify)-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$'
    if ([String]::Equals($parentCanonical, [IO.Path]::GetFullPath($outputRoot).TrimEnd('\\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        if ($targetLeaf -notmatch $quarantinePattern) { return }
    } elseif ($parentLeaf -eq '.trx') {
        $grandParentLeaf = [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($parentCanonical))
        if ($grandParentLeaf -notmatch $quarantinePattern) { return }
    } elseif ($parentLeaf -notmatch $quarantinePattern) { return }
    Assert-TrustedTree $AllowedParent
    Assert-TrustedTree $canonical
    # Walk one level at a time.  A recursive provider enumeration can follow
    # a junction inserted between enumeration and deletion; never hand a
    # reparse-bearing tree to Remove-Item -Recurse.
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)) {
        $itemPath = [IO.Path]::GetFullPath($item.FullName)
        if (-not $itemPath.StartsWith($canonical + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            try { Remove-Item -LiteralPath $itemPath -Force -ErrorAction Stop } catch { return }
            continue
        }
        if ($item.PSIsContainer) { Remove-SafeTree $itemPath $canonical }
        else { try { Remove-Item -LiteralPath $itemPath -Force -ErrorAction Stop } catch { return } }
    }
    try { Remove-Item -LiteralPath $canonical -Force -ErrorAction Stop } catch { }
}

$root = $null; $outputRoot = $null; $build = $null; $cleanupTarget = $null
$script:OutputIdentities = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$script:OutputHashes = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$script:OutputBytes = [System.Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
try {
    $root = Resolve-CanonicalRoot $RepositoryRoot
    $outputRoot = Assert-ReleaseHost $root
    $endpoint = Get-RequiredValue $EndpointVariable; Assert-Endpoint $endpoint
    [void](Get-RequiredValue 'SQLOBSERVER_RELEASE_MCP_AUTHORIZED_INSTANCE_ID')
    [void](Get-RequiredValue 'SQLOBSERVER_RELEASE_MCP_CANCELLATION_INSTANCE_ID')
    $commit = Get-Commit $root
    $environment = Get-RequiredValue $EnvironmentVariable
    $deniedTargetId = Get-RequiredValue $DeniedTargetVariable
    $attestationPath = Get-RequiredValue 'SQLOBSERVER_RELEASE_MCP_DENIED_TARGET_ATTESTATION'
    $attestationHash = Get-RequiredValue 'SQLOBSERVER_RELEASE_MCP_DENIED_TARGET_ATTESTATION_SHA256'
    $attestation = Read-ValidatedLabAttestation $root $attestationPath $attestationHash $environment $commit $deniedTargetId
    if ($attestation.DeniedTargetId -cne $deniedTargetId) { Fail 'INPUT' }
    Assert-ProtocolContract $root
    $stdio = [IO.Path]::GetFullPath((Join-Path $root 'src/SqlObserver.McpStdio/bin/Release/net10.0/SqlObserver.McpStdio.exe'))
    if (-not (Test-Path -LiteralPath $stdio -PathType Leaf)) { Fail 'EXECUTABLE' }
    Assert-TrustedTree $stdio
    $runId = [Guid]::NewGuid().ToString()
    $build = Join-Path $outputRoot ('.pending-' + $runId)
    if (Test-Path -LiteralPath $build) { Fail 'PATH' }
    # Validate every existing component before creation; the newly-created
    # TestResults/m12/build chain is checked again immediately afterward.
    Assert-TrustedTree (Join-Path $root 'TestResults')
    Assert-TrustedTree $outputRoot
    $cleanupTarget = $build
    [IO.Directory]::CreateDirectory($build) | Out-Null
    Assert-TrustedTree (Join-Path $root 'TestResults')
    Assert-TrustedTree $outputRoot
    Assert-TrustedTree $build
    $run = Invoke-LiveTest $root $build $stdio $attestation
    $created = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $artifactName = 'mcp-protocol-evidence.json'; $evidenceName = 'mcp-protocol-result.json'; $sidecarName = 'mcp-protocol-provenance.json'
    Write-Utf8Json (Join-Path $build $artifactName) ([ordered]@{ schemaVersion = 1; caseId = $CaseId; producerId = $ProducerId; kind = $ArtifactKind; result = 'passed'; protocolCurrent = $ApprovedCurrentProtocol; protocolDownlevel = $ApprovedDownlevelProtocol; toolCount = $ApprovedToolCount; catalogDigest = $ApprovedCatalogDigest; serverVersion = $ApprovedServerVersion; testCount = $run.TestCount }) $build
    Write-Utf8Json (Join-Path $build $evidenceName) ([ordered]@{ caseId = $CaseId; status = 'passed'; executions = 1; skipped = 0; notRun = 0; failed = 0; runId = $runId; commitSha = $commit; environmentId = (Get-RequiredValue $EnvironmentVariable) }) $build
    $artifactPath = Join-Path $build $artifactName; $artifactHash = Get-Sha256 $artifactPath; $artifactSize = (Get-Item -LiteralPath $artifactPath).Length
    Write-Utf8Json (Join-Path $build $sidecarName) ([ordered]@{ artifactId = 'm12-mcp-protocol-evidence'; kind = $ArtifactKind; producerId = $ProducerId; artifactSha256 = $artifactHash; artifactSize = $artifactSize; commitSha = $commit; runId = $runId; environmentId = (Get-RequiredValue $EnvironmentVariable); createdAtUtc = $created }) $build
    # TRX is an input to the producer only; never publish raw runner output.
    $candidateEntries = @(Get-ChildItem -LiteralPath $build -Force)
    if ($candidateEntries.Count -ne 3 -or ($candidateEntries | Where-Object { $_.PSIsContainer -or $_.Extension -cne '.json' }).Count -ne 0 -or @($candidateEntries.Name | Sort-Object) -join '|' -cne 'mcp-protocol-evidence.json|mcp-protocol-provenance.json|mcp-protocol-result.json') { Fail 'OUTPUT' }
    $preMoveIdentities = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $candidateEntries) {
        $identity = Assert-OutputFileIdentity $entry.FullName $build
        if (-not $script:OutputIdentities.ContainsKey($entry.Name)) { Fail 'OUTPUT' }
        $expectedIdentity = $script:OutputIdentities[$entry.Name]
        if ($identity.Volume -ne $expectedIdentity.Volume -or $identity.High -ne $expectedIdentity.High -or $identity.Low -ne $expectedIdentity.Low -or $identity.Links -ne 1) { Fail 'OUTPUT' }
        $preMoveIdentities[$entry.Name] = $identity
    }
    Assert-PublishedJson $build $runId $commit (Get-RequiredValue $EnvironmentVariable) $created
    $verify = Join-Path $outputRoot ('.verify-' + $runId)
    $final = Join-Path $outputRoot $runId
    if (Test-Path -LiteralPath $verify) { Fail 'PATH' }
    if (Test-Path -LiteralPath $final) { Assert-TrustedTree $final; Fail 'PATH' }
    $finalCanonical = [IO.Path]::GetFullPath($final)
    $outputPrefix = $outputRoot.TrimEnd('\\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $finalCanonical.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) { Fail 'PATH' }
    Assert-TrustedTree $build
    [IO.Directory]::Move($build, $verify); $build = $null; $cleanupTarget = $verify
    Assert-TrustedTree $verify
    $verifiedEntries = @(Get-ChildItem -LiteralPath $verify -Force)
    if ($verifiedEntries.Count -ne 3 -or ($verifiedEntries | Where-Object { $_.PSIsContainer }).Count -ne 0 -or @($verifiedEntries.Name | Sort-Object) -join '|' -cne 'mcp-protocol-evidence.json|mcp-protocol-provenance.json|mcp-protocol-result.json') { Fail 'OUTPUT' }
    foreach ($file in $verifiedEntries) {
        Assert-TrustedTree $file.FullName
        if ($file.Length -lt 1) { Fail 'OUTPUT' }
        $identity = Assert-OutputFileIdentity $file.FullName $verify
        $before = $preMoveIdentities[$file.Name]
        if ($identity.Volume -ne $before.Volume -or $identity.High -ne $before.High -or $identity.Low -ne $before.Low -or $identity.Links -ne 1) { Fail 'OUTPUT' }
    }
    Assert-PublishedJson $verify $runId $commit (Get-RequiredValue $EnvironmentVariable) $created
    if (Test-Path -LiteralPath $final) { Fail 'PATH' }
    # This is the final fallible operation. No validation, cleanup, or state
    # mutation follows a successful verify-to-candidate rename.
    $heldJson = [System.Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach ($name in @('mcp-protocol-evidence.json','mcp-protocol-provenance.json','mcp-protocol-result.json')) {
            $held = [IO.FileStream]::new((Join-Path $verify $name), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $heldIdentity = [M12OutputFile]::Read($held)
            if ($heldIdentity.Links -ne 1 -or -not [String]::Equals((Normalize-NativePath $heldIdentity.FinalPath), [IO.Path]::GetFullPath((Join-Path $verify $name)), [StringComparison]::OrdinalIgnoreCase)) { $held.Dispose(); Fail 'OUTPUT' }
            $heldJson.Add($held)
        }
        $cleanupTarget = $null
        [IO.Directory]::Move($verify, $final)
    }
    finally { foreach ($held in $heldJson) { try { $held.Dispose() } catch { } } }
    exit 0
    $cleanupTarget = $null
    [ordered]@{ caseId = $CaseId; producerId = $ProducerId; kind = $ArtifactKind; runId = $runId; commitSha = $commit; environmentId = (Get-RequiredValue $EnvironmentVariable); artifact = $artifactName; evidence = $evidenceName; provenance = $sidecarName; status = 'passed' } | ConvertTo-Json -Compress
    exit 0
}
catch {
    # Never expose endpoint, host, identity, command line, or exception text.
    [Console]::Error.WriteLine('M12-MCP-PRODUCER-HOST')
    exit 1
}
finally {
    if ($null -ne $cleanupTarget -and $null -ne $outputRoot) { Remove-SafeTree $cleanupTarget $outputRoot }
}
