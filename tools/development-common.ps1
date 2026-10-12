Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.5') {
    throw 'Development tools require PowerShell 7.5 or newer.'
}
if (-not $IsWindows) { throw 'Development startup requires Windows DPAPI for local credential storage.' }
Add-Type -AssemblyName System.Security

$developmentRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$developmentArtifacts = Join-Path $developmentRepository 'artifacts/development'
$developmentSettingsPath = Join-Path $developmentArtifacts 'settings.dpapi'
$developmentStatePath = Join-Path $developmentArtifacts 'processes.json'

function Protect-DevelopmentFile([string] $Path) {
    if ($IsWindows) {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow))
        # Update only the DACL. Set-Acl also tries to apply audit permissions on
        # an already protected file, which requires SeSecurityPrivilege.
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path), $acl)
    } else {
        [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
    }
}

function Get-DevelopmentSettings([switch] $Create) {
    if (-not (Test-Path -LiteralPath $developmentSettingsPath)) {
        if (-not $Create) { throw 'Run tools/dev-up.ps1 before seeding the local development database.' }
        [void][IO.Directory]::CreateDirectory($developmentArtifacts)
        $settings = [ordered]@{ SchemaVersion = 1 }
        foreach ($name in @('PostgresPassword', 'AppPassword', 'IdentityFingerprintKey', 'McpCursorKey')) {
            $settings[$name] = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        }
        $plainBytes = [Text.Encoding]::UTF8.GetBytes(($settings | ConvertTo-Json))
        try {
            $protectedBytes = [Security.Cryptography.ProtectedData]::Protect(
                $plainBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            [IO.File]::WriteAllBytes($developmentSettingsPath, $protectedBytes)
        } finally { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
        Protect-DevelopmentFile $developmentSettingsPath
    }
    $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($developmentSettingsPath), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { $settings = [Text.Encoding]::UTF8.GetString($plainBytes) | ConvertFrom-Json -AsHashtable }
    finally { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    if ($settings.SchemaVersion -ne 1) { throw 'Unsupported development settings version.' }
    foreach ($name in @('PostgresPassword', 'AppPassword', 'IdentityFingerprintKey', 'McpCursorKey')) {
        if ($settings[$name] -isnot [string] -or $settings[$name] -cnotmatch '^[0-9A-F]{64}$') {
            throw 'Development settings are malformed. Do not replace credentials for an existing volume.'
        }
    }
    return $settings
}

function Invoke-DevelopmentCommand([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Development command failed: $Executable (exit $LASTEXITCODE)." }
}

function Invoke-DevelopmentCompose([string[]] $Arguments) {
    $composeSettings = Get-DevelopmentSettings
    $previousPassword = [Environment]::GetEnvironmentVariable('SQLOBSERVER_DEV_POSTGRES_PASSWORD', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('SQLOBSERVER_DEV_POSTGRES_PASSWORD', $composeSettings.PostgresPassword, 'Process')
        Invoke-DevelopmentCommand 'docker' (@('compose', '--project-name', 'sqlobserver-development',
            '--file', (Join-Path $developmentRepository 'docker-compose.yml')) + $Arguments)
    } finally {
        [Environment]::SetEnvironmentVariable('SQLOBSERVER_DEV_POSTGRES_PASSWORD', $previousPassword, 'Process')
    }
}

function Invoke-DevelopmentPnpm([string[]] $Arguments) {
    if (Get-Command corepack -ErrorAction SilentlyContinue) {
        Invoke-DevelopmentCommand 'corepack' (@('pnpm@11.19.0') + $Arguments)
        return
    }
    if ((& pnpm --version) -ne '11.19.0') { throw 'Install pnpm 11.19.0 or enable Corepack for development startup.' }
    Invoke-DevelopmentCommand 'pnpm' $Arguments
}

function Get-DevelopmentProcesses {
    if (Test-Path -LiteralPath $developmentStatePath) {
        return (Get-Content -LiteralPath $developmentStatePath -Raw | ConvertFrom-Json -AsHashtable)
    }
    return @{}
}

function Save-DevelopmentProcesses([hashtable] $State) {
    [IO.File]::WriteAllText($developmentStatePath, ($State | ConvertTo-Json -Depth 4))
}

function Get-OwnedDevelopmentProcess($Record) {
    if ($null -eq $Record) { return $null }
    $process = Get-Process -Id $Record.Id -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    if ($process.StartTime.ToUniversalTime().Ticks -ne [long]$Record.StartTicks -or
        $process.Path -ne $Record.Executable) {
        throw 'A saved development PID belongs to a different process; refusing to control it.'
    }
    return $process
}

function Test-DevelopmentPort([int] $Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.Connect('127.0.0.1', $Port)
        return $true
    } catch [Net.Sockets.SocketException] {
        return $false
    } finally { $client.Dispose() }
}

function Start-DevelopmentProcess([string] $Name, [string] $Executable, [string] $EntryPoint,
    [string] $WorkingDirectory, [hashtable] $Environment, [hashtable] $State) {
    $options = @{
        FilePath = $Executable
        ArgumentList = @('"' + $EntryPoint + '"')
        WorkingDirectory = $WorkingDirectory
        Environment = $Environment
        RedirectStandardOutput = Join-Path $developmentArtifacts "$Name.stdout.log"
        RedirectStandardError = Join-Path $developmentArtifacts "$Name.stderr.log"
        PassThru = $true
    }
    if ($IsWindows) { $options.WindowStyle = 'Hidden' }
    $process = Start-Process @options
    $State[$Name] = @{ Id = $process.Id; StartTicks = $process.StartTime.ToUniversalTime().Ticks; Executable = $Executable }
    Save-DevelopmentProcesses $State
    return $process
}

function Wait-DevelopmentHttp([string] $Uri, [Diagnostics.Process] $Process) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        $Process.Refresh()
        if ($Process.HasExited) { throw 'Development process exited; inspect its logs in artifacts/development.' }
        try {
            $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 2 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { return }
        } catch [Net.Http.HttpRequestException] { }
        catch [Threading.Tasks.TaskCanceledException] { }
        Start-Sleep -Milliseconds 300
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'Development process did not become ready; inspect its logs in artifacts/development.'
}
