#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Install','Inspect','Reconcile','Remove')][string] $Action = 'Inspect',
    [switch] $Apply,
    [string] $SqlService,
    [string] $SqlImagePath,
    [string] $SqlImageSha256,
    [string] $SqlAccountSid,
    [string] $CollectorService = 'SqlObserverCollector',
    [string] $MonitorSid
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskName = 'SqlObserverCpuProcessAccess'
$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SqlObserverCpuProcessAccess'
$installedScript = Join-Path $root 'CpuProcessAccess.ps1'
$scheduledScript = Join-Path $root 'Invoke-ScheduledReconcile.ps1'
$helperPath = Join-Path $root 'ProcessAccessProvisioner.cs'
$configPath = Join-Path $root 'policy.json'
$manifestPath = Join-Path $root 'manifest.json'
$statePath = Join-Path $root 'ownership.bin'
$lockPath = Join-Path $root 'operation.lock'
$powershell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
$taskArguments = '-NoLogo -NoProfile -NonInteractive -File "' + $scheduledScript + '"'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and $identity.User.Value -ne 'S-1-5-18') { throw 'An elevated administrator or SYSTEM is required; the Collector must not call this provisioner.' }
}
function Assert-NoReparse([string] $Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) { if ((Get-Item -Force -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse paths are refused.' } }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Assert-Private([string] $Path) {
    Assert-NoReparse $Path
    if (-not (Test-Path -LiteralPath $Path)) { throw "Missing protected installation entry: $([IO.Path]::GetFileName($Path))" }
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Installation owner must be Administrators or SYSTEM.' }
    $seen = @{}
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -or $rule.AccessControlType -ne 'Allow' -or $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) { throw 'Installation ACL must grant only Administrators and SYSTEM full control.' }
        $seen[$rule.IdentityReference.Value] = $true
    }
    if ($seen.Count -ne 2) { throw 'Both Administrators and SYSTEM must own installation access.' }
}
function New-PrivateDirectory([string] $Path) {
    Assert-NoReparse $Path
    if (Test-Path -LiteralPath $Path) { throw 'Existing installation directory is not adopted or overwritten.' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow')) }
    [IO.Directory]::CreateDirectory($Path,$acl) | Out-Null
    Assert-Private $Path
}
function Get-Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Set-NewFileOwner([string] $Path) {
    # Only called on files just created by this invocation inside the protected root.
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Write-Json([string] $Path, $Value) {
    if (Test-Path -LiteralPath $Path) { Assert-Private $Path }
    $temporary = Join-Path $root ('pending-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 8))
        $stream = [IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Set-NewFileOwner $temporary
        Assert-Private $temporary
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary,$Path,$null) } else { [IO.File]::Move($temporary,$Path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
function Read-Json([string] $Path) { Assert-Private $Path; if ((Get-Item -LiteralPath $Path).Length -gt 16384) { throw 'Configuration exceeds its byte bound.' }; Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Assert-Fields($Value, [string[]] $Names) {
    if (@($Value.PSObject.Properties.Name).Count -ne $Names.Count) { throw 'Unexpected configuration fields.' }
    foreach ($name in $Names) { if ($name -notin $Value.PSObject.Properties.Name) { throw 'Missing configuration field.' } }
}
function Get-OwnedTask([string] $InstallationId, [switch] $AllowMissing) {
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
    if ($null -eq $task) { if ($AllowMissing) { return $null }; throw 'Owned scheduled task is missing.' }
    if ($task.Description -ne ('SqlObserver CPU process access ' + $InstallationId) -or @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ne $powershell -or $task.Actions[0].Arguments -ne $taskArguments -or $task.Actions[0].WorkingDirectory -ne $root -or $task.Principal.UserId -notin @('SYSTEM','S-1-5-18') -or $task.Principal.LogonType -ne 'ServiceAccount' -or $task.Principal.RunLevel -ne 'Highest' -or $task.Settings.MultipleInstances -ne 'IgnoreNew' -or $task.Settings.ExecutionTimeLimit -ne 'PT1M') { throw 'Scheduled task ownership/configuration drift; refusing to act on it.' }
    if (@($task.Triggers).Count -ne 2 -or @($task.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' -and $_.Enabled }).Count -ne 1 -or @($task.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskTimeTrigger' -and $_.Enabled -and $_.Repetition.Interval -eq 'PT1M' -and [string]::IsNullOrEmpty($_.Repetition.Duration) }).Count -ne 1) { throw 'Scheduled task trigger drift.' }
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($scheduler.GetFolder('\').GetTask($taskName).GetSecurityDescriptor(4))
    if ($null -eq $descriptor.DiscretionaryAcl -or -not ($descriptor.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -or $descriptor.DiscretionaryAcl.Count -ne 2) { throw 'Scheduled task DACL drift.' }
    $seen = @{}
    foreach ($ace in $descriptor.DiscretionaryAcl) {
        if ($ace -isnot [Security.AccessControl.CommonAce] -or $ace.IsCallback -or $ace.AceQualifier -ne 'AccessAllowed' -or $ace.AceFlags -ne 'None' -or $ace.SecurityIdentifier.Value -notin @('S-1-5-18','S-1-5-32-544') -or $ace.AccessMask -ne 0x1f01ff) { throw 'Scheduled task DACL drift.' }
        $seen[$ace.SecurityIdentifier.Value] = $true
    }
    if ($seen.Count -ne 2) { throw 'Scheduled task DACL principal mismatch.' }
    return $task
}

if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSVersion.Major -ne 5) { throw '64-bit Windows PowerShell 5.1 is required.' }
Assert-Administrator
Assert-NoReparse $root
if ($Action -ne 'Install' -and ($PSBoundParameters.ContainsKey('SqlService') -or $PSBoundParameters.ContainsKey('SqlImagePath') -or $PSBoundParameters.ContainsKey('SqlImageSha256') -or $PSBoundParameters.ContainsKey('SqlAccountSid') -or $PSBoundParameters.ContainsKey('CollectorService') -or $PSBoundParameters.ContainsKey('MonitorSid'))) { throw 'Policy overrides are accepted only during explicit installation.' }
if ($Action -eq 'Install') {
    if (Test-Path -LiteralPath $root) { throw 'Installation already exists; inspect it. In-place policy replacement is unsupported.' }
    if (Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue) { throw 'Scheduled task name already exists; it will not be adopted.' }
    $sourceHelper = Join-Path $PSScriptRoot 'ProcessAccessProvisioner.cs'
    $sourceScheduled = Join-Path $PSScriptRoot 'Invoke-ScheduledReconcile.ps1'
    Assert-NoReparse $sourceHelper
    Assert-NoReparse $sourceScheduled
    Add-Type -Path $sourceHelper
    $policy = [SqlObserver.CpuProcessAccess.Policy]::new()
    $policy.SqlService = $SqlService; $policy.SqlImagePath = $SqlImagePath; $policy.SqlImageSha256 = $SqlImageSha256
    $policy.SqlAccountSid = $SqlAccountSid; $policy.CollectorService = $CollectorService; $policy.MonitorSid = $MonitorSid
    [SqlObserver.CpuProcessAccess.Provisioner]::ValidateServices($policy)
    Assert-NoReparse $SqlImagePath
    if ((Get-Hash $SqlImagePath) -ne $SqlImageSha256) { throw 'Pinned SQL executable hash does not match.' }
    $id = [guid]::NewGuid().ToString('D')
    $config = [ordered]@{ SchemaVersion = 1; InstallationId = $id; MachineName = [Environment]::MachineName; SqlService = $SqlService; SqlImagePath = $SqlImagePath; SqlImageSha256 = $SqlImageSha256; SqlAccountSid = $SqlAccountSid; CollectorService = $CollectorService; MonitorSid = $MonitorSid }
    $preview = [ordered]@{ State = 'install-preview'; Root = $root; Task = $taskName; Principal = 'SYSTEM'; SqlService = $SqlService; MonitorSid = $MonitorSid; AccessMask = '0x101000'; IntervalSeconds = 60; DeadlineSeconds = 60; PolicySha256 = $policy.Binding }
    $preview | ConvertTo-Json
    if (-not $Apply -or -not $PSCmdlet.ShouldProcess($root, 'Install persistent SYSTEM task that grants 0x101000 to the approved monitor account on each approved SQL process generation')) { return }
    New-PrivateDirectory $root
    Copy-Item -LiteralPath $PSCommandPath -Destination $installedScript
    Copy-Item -LiteralPath $sourceHelper -Destination $helperPath
    Copy-Item -LiteralPath $sourceScheduled -Destination $scheduledScript
    foreach ($newFile in @($installedScript,$helperPath,$scheduledScript)) { Set-NewFileOwner $newFile }
    Assert-Private $installedScript; Assert-Private $helperPath; Assert-Private $scheduledScript
    Write-Json $configPath $config
    $manifest = [ordered]@{ SchemaVersion = 1; InstallationId = $id; ScriptSha256 = Get-Hash $installedScript; ScheduledScriptSha256 = Get-Hash $scheduledScript; HelperSha256 = Get-Hash $helperPath; ConfigSha256 = Get-Hash $configPath }
    Write-Json $manifestPath $manifest
    # Register disabled, establish the task ACL, verify configuration, then enable.
    # No SQL restart, Collector restart, or immediate task invocation occurs here.
    $startup = New-ScheduledTaskTrigger -AtStartup
    $periodic = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $settings.Enabled = $false
    $actionDefinition = New-ScheduledTaskAction -Execute $powershell -Argument $taskArguments -WorkingDirectory $root
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Description ('SqlObserver CPU process access ' + $id) -Action $actionDefinition -Trigger @($startup,$periodic) -Settings $settings -Principal $principal | Out-Null
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $scheduler.GetFolder('\').GetTask($taskName).SetSecurityDescriptor('D:P(A;;FA;;;SY)(A;;FA;;;BA)',0)
    Get-OwnedTask $id | Out-Null
    Enable-ScheduledTask -TaskName $taskName -TaskPath '\' | Out-Null
    Write-Json (Join-Path $root 'last-receipt.json') ([ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); State = 'installed-awaiting-reconciliation'; InstallationId = $id })
    Write-Output 'Installed. First reconciliation is scheduled within one minute; inspect its receipt. Installation is not native qualification.'
    return
}

Assert-Private $root
foreach ($file in @($installedScript,$scheduledScript,$helperPath,$configPath,$manifestPath)) { Assert-Private $file }
if ([IO.Path]::GetFullPath($PSCommandPath) -ne $installedScript) { throw 'Inspect/Reconcile/Remove must run from the protected installed script.' }
$manifest = Read-Json $manifestPath
Assert-Fields $manifest @('SchemaVersion','InstallationId','ScriptSha256','ScheduledScriptSha256','HelperSha256','ConfigSha256')
if ($manifest.SchemaVersion -ne 1 -or (Get-Hash $installedScript) -ne $manifest.ScriptSha256 -or (Get-Hash $scheduledScript) -ne $manifest.ScheduledScriptSha256 -or (Get-Hash $helperPath) -ne $manifest.HelperSha256 -or (Get-Hash $configPath) -ne $manifest.ConfigSha256) { throw 'Installed artifact integrity mismatch.' }
$config = Read-Json $configPath
Assert-Fields $config @('SchemaVersion','InstallationId','MachineName','SqlService','SqlImagePath','SqlImageSha256','SqlAccountSid','CollectorService','MonitorSid')
if ($config.SchemaVersion -ne 1 -or $config.InstallationId -ne $manifest.InstallationId -or $config.MachineName -ne [Environment]::MachineName) { throw 'Installation binding mismatch.' }
Add-Type -Path $helperPath
$policy = [SqlObserver.CpuProcessAccess.Policy]::new()
foreach ($field in @('SqlService','SqlImagePath','SqlImageSha256','SqlAccountSid','CollectorService','MonitorSid')) { $policy.$field = $config.$field }
$policy.Validate()
$task = Get-OwnedTask $config.InstallationId -AllowMissing:($Action -eq 'Remove')
if (Test-Path -LiteralPath $lockPath) { Assert-Private $lockPath }
if (Test-Path -LiteralPath $statePath) { Assert-Private $statePath }
$doApply = $Apply -and $Action -ne 'Inspect' -and $PSCmdlet.ShouldProcess($config.SqlService, ($Action + ' approved process access policy; no SQL or Collector service control'))
# An exclusive file lock serializes scheduled/manual operations. Read-only Inspect does
# create/open only this protected coordination file; it never writes the process DACL.
$lock = $null
try {
    if ($Action -eq 'Remove' -and $doApply -and $null -ne $task) {
        Disable-ScheduledTask -TaskName $taskName -TaskPath '\' | Out-Null
        Stop-ScheduledTask -TaskName $taskName -TaskPath '\'
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do { $running = (Get-ScheduledTask -TaskName $taskName -TaskPath '\').State -eq 'Running'; if ($running) { Start-Sleep -Milliseconds 100 } } while ($running -and [DateTime]::UtcNow -lt $deadline)
        if ($running) { throw 'Scheduled action has not stopped; removal refused. Task remains disabled.' }
    }
    $newLock = -not (Test-Path -LiteralPath $lockPath)
    $lock = [IO.FileStream]::new($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if ($newLock) { Set-NewFileOwner $lockPath }
    Assert-Private $lockPath
    $receipt = [SqlObserver.CpuProcessAccess.Provisioner]::Execute($policy,$statePath,$Action,$doApply)
    $output = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); InstallationId = $config.InstallationId; Operation = $Action; Apply = [bool]$doApply; Result = $receipt }
    if ($doApply) { Write-Json (Join-Path $root 'last-receipt.json') $output }
    $output | ConvertTo-Json -Depth 8
    if ($Action -eq 'Remove' -and $doApply) {
        if ($null -ne $task) { Get-OwnedTask $config.InstallationId | Out-Null; Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false }
        # Keep private evidence and ownership state. Never recursively delete during removal.
        Write-Output 'Task removed; owned current-generation ACE removed or verified absent. Protected installation and receipt retained for review.'
    }
} catch {
    if ($doApply) { try { Write-Json (Join-Path $root 'last-receipt.json') ([ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); InstallationId = $config.InstallationId; Operation = $Action; State = 'refused-or-unverified'; Error = $_.Exception.Message }) } catch { } }
    throw
} finally { if ($null -ne $lock) { $lock.Dispose() } }
