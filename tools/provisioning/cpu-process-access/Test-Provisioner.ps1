#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -ne 5) { throw 'Run this pure managed suite in Windows PowerShell 5.1.' }
$errors = $null; $tokens = $null
$null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'CpuProcessAccess.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
# Compile as separate source files: C# using directives remain at each file's start.
Add-Type -Path @((Join-Path $PSScriptRoot 'ProcessAccessProvisioner.cs'),(Join-Path $PSScriptRoot 'ProvisionerTests.cs'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('SqlObserverCpuAclTests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $count = [SqlObserver.CpuProcessAccess.ProvisionerTests]::Run($temporary)
    [ordered]@{ State = 'passed'; ManagedChecks = $count; PowerShellParse = 'passed'; NativeApiCalls = 0; ScheduledTasksInstalled = 0; ProcessDaclWrites = 0 } | ConvertTo-Json
} finally {
    # Only the one known test-owned file and empty generated directory are removed.
    $file = Join-Path $temporary 'ownership-test.bin'
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    [IO.Directory]::Delete($temporary,$false)
}
