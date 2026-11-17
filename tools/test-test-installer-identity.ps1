[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Requires Windows with the NSI service running.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this native qualification harness from an elevated Windows administrator console.'
    }
} finally { $identity.Dispose() }
$previous = $env:SQLOBSERVER_INSTALLER_IDENTITY_TEST
try {
    $env:SQLOBSERVER_INSTALLER_IDENTITY_TEST = '1'
    $project = Join-Path $PSScriptRoot '../installer/test/SqlObserver.TestSetup.Tests/SqlObserver.TestSetup.Tests.csproj'
    & dotnet test $project --configuration Release --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~WindowsIdentityTests' --logger trx --results-directory (Join-Path $PSScriptRoot '../artifacts/test-installer/native-identity')
    if ($LASTEXITCODE -ne 0) { throw 'Native Windows service identity qualification failed.' }
} finally { $env:SQLOBSERVER_INSTALLER_IDENTITY_TEST = $previous }
