[CmdletBinding()]
param([switch] $NoStart, [switch] $Stop)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'development-common.ps1')
if ($Stop -and $NoStart) { throw 'Use either -Stop or -NoStart.' }
$settings = Get-DevelopmentSettings -Create:(-not $Stop)
$state = Get-DevelopmentProcesses

if ($Stop) {
    foreach ($name in @('web', 'api')) {
        $process = Get-OwnedDevelopmentProcess $state[$name]
        if ($null -ne $process) { Stop-Process -Id $process.Id -ErrorAction Stop }
        $state.Remove($name)
        Save-DevelopmentProcesses $state
    }
    Invoke-DevelopmentCompose @('stop', 'postgres')
    Write-Host 'Development processes stopped. PostgreSQL data and development credentials are preserved.'
    return
}

foreach ($name in @('api', 'web')) {
    $process = Get-OwnedDevelopmentProcess $state[$name]
    if ($null -ne $process) { throw 'Development processes are already running. Use -Stop before rebuilding, or seed-lab.ps1 to inspect/reuse the sample.' }
}
if (-not $NoStart -and ((Test-DevelopmentPort 5080) -or (Test-DevelopmentPort 5173))) {
    throw 'Development API/web ports are in use by another process. No process was stopped.'
}

Push-Location $developmentRepository
try {
    Invoke-DevelopmentCommand 'dotnet' @('restore', 'SqlObserver.slnx', '--configfile', 'NuGet.Config', '--locked-mode')
    Invoke-DevelopmentCommand 'dotnet' @('build', 'src/SqlObserver.Cli/SqlObserver.Cli.csproj', '--configuration', 'Debug', '--no-restore')
    Invoke-DevelopmentCommand 'dotnet' @('build', 'src/SqlObserver.Server/SqlObserver.Server.csproj', '--configuration', 'Debug', '--no-restore')
    Invoke-DevelopmentPnpm @('--dir', 'web', 'install', '--frozen-lockfile')
    Invoke-DevelopmentCompose @('up', '-d', '--wait', '--wait-timeout', '60', 'postgres')
    & (Join-Path $PSScriptRoot 'seed-lab.ps1') -SkipBuild
    if ($NoStart) { Write-Host 'Development database migrated and seeded. Run dev-up.ps1 to start API and Vite.'; return }

    $apiEnvironment = @{
        DOTNET_ENVIRONMENT = 'Development'
        ASPNETCORE_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = 'http://127.0.0.1:5080'
        ConnectionStrings__SqlObserverRepository = "Host=127.0.0.1;Port=55432;Database=sqlobserver_dev;Username=sqlobserver_dev_app;Password=$($settings.AppPassword);SSL Mode=Disable;Include Error Detail=false"
        SqlObserver__DevelopmentAuthentication__Enabled = 'true'
        SqlObserver__IdentityFingerprintKey = $settings.IdentityFingerprintKey
        SQLOBSERVER_MCP_CURSOR_KEY = [Convert]::ToBase64String([Convert]::FromHexString($settings.McpCursorKey))
        SqlObserver__Authorization__Bindings__0__GroupSid = 'S-1-5-21-104001-104002-104003-2001'
        SqlObserver__Authorization__Bindings__0__AllTargets = 'false'
        SqlObserver__Authorization__Bindings__0__Roles__0 = 'Viewer'
        SqlObserver__Authorization__Bindings__0__Roles__1 = 'Operator'
        SqlObserver__Authorization__Bindings__0__Roles__2 = 'TargetAdministrator'
        SqlObserver__Authorization__Bindings__0__TargetIds__0 = '00000000-0000-4000-8000-000000000001'
        SqlObserver__Authorization__Bindings__0__TargetIds__1 = '00000000-0000-4000-8000-000000000002'
    }
    $api = Start-DevelopmentProcess 'api' (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source `
        (Join-Path $developmentRepository 'src/SqlObserver.Server/bin/Debug/net10.0/SqlObserver.Server.dll') `
        $developmentRepository $apiEnvironment $state
    Wait-DevelopmentHttp 'http://127.0.0.1:5080/api/v1/service' $api
    $web = Start-DevelopmentProcess 'web' (Get-Command node -CommandType Application | Select-Object -First 1).Source `
        (Join-Path $developmentRepository 'web/node_modules/vite/bin/vite.js') `
        (Join-Path $developmentRepository 'web') @{} $state
    Wait-DevelopmentHttp 'http://127.0.0.1:5173/' $web
    Write-Host 'SqlObserver development is ready at http://127.0.0.1:5173 (synthetic data).'
    Write-Host 'Logs and credentials: artifacts/development. Stop with pwsh ./tools/dev-up.ps1 -Stop.'
} catch {
    # Only these recorded, identity-checked child processes belong to this run.
    foreach ($name in @('web', 'api')) {
        $process = Get-OwnedDevelopmentProcess $state[$name]
        if ($null -ne $process) { Stop-Process -Id $process.Id -ErrorAction Stop }
        $state.Remove($name)
    }
    Save-DevelopmentProcesses $state
    throw
} finally { Pop-Location }
