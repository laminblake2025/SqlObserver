[CmdletBinding()]
param([switch] $SkipBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'development-common.ps1')
$settings = Get-DevelopmentSettings
Push-Location $developmentRepository
try {
    if (-not $SkipBuild) {
        Invoke-DevelopmentCommand 'dotnet' @('build', 'src/SqlObserver.Cli/SqlObserver.Cli.csproj', '--configuration', 'Debug')
    }
    $previous = @{}
    $values = @{
        DOTNET_ENVIRONMENT = 'Development'
        SQLOBSERVER_DEV_POSTGRES = "Host=127.0.0.1;Port=55432;Database=sqlobserver_dev;Username=postgres;Password=$($settings.PostgresPassword);SSL Mode=Disable;Pooling=false;Include Error Detail=false"
        SQLOBSERVER_DEV_APP_PASSWORD = $settings.AppPassword
    }
    foreach ($name in $values.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $values[$name], 'Process')
    }
    try {
        Invoke-DevelopmentCommand 'dotnet' @('src/SqlObserver.Cli/bin/Debug/net10.0/SqlObserver.Cli.dll', 'dev-bootstrap')
    } finally {
        foreach ($name in $values.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    }
} finally { Pop-Location }
