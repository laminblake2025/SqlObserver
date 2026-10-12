[CmdletBinding()]
param([string] $TrustedPowerShellPath = $env:SQLOBSERVER_M12_TRUSTED_PWSH_PATH)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.5') {
    throw 'Web supply-chain tests require an explicitly invoked PowerShell 7.5+ runtime.'
}
if (-not $TrustedPowerShellPath) {
    # Use the runtime explicitly executing this script, never an executable found on PATH.
    $TrustedPowerShellPath = Join-Path $PSHOME 'pwsh.exe'
}
$previousTrustedPath = $env:SQLOBSERVER_M12_TRUSTED_PWSH_PATH
$previousPermissionParity = $env:SQLOBSERVER_PERMISSION_PARITY
try {
    $env:SQLOBSERVER_M12_TRUSTED_PWSH_PATH = [IO.Path]::GetFullPath($TrustedPowerShellPath)
    $env:SQLOBSERVER_PERMISSION_PARITY = "1"
    Push-Location (Join-Path $PSScriptRoot '../web')
    try {
        & pnpm run test
        if ($LASTEXITCODE -ne 0) { throw "Web contract tests failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }
} finally {
    $env:SQLOBSERVER_M12_TRUSTED_PWSH_PATH = $previousTrustedPath
    $env:SQLOBSERVER_PERMISSION_PARITY = $previousPermissionParity
}
