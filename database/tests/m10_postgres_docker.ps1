<#$
Docker-backed smoke checks for M10.  The harness is intentionally explicit
about unavailable Docker so CI can classify the result rather than report a
false pass.  A deployment runner may set POSTGRES_TEST_CONNECTIONSTRING and
execute its normal migration runner before adding SQL assertions here.
#>
[CmdletBinding()]
param()
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) { Write-Output 'M10 PostgreSQL Docker tests: UNAVAILABLE (docker executable not installed)'; exit 0 }
$dockerInfo = docker info --format '{{.ServerVersion}}' 2>&1
if ($LASTEXITCODE -ne 0 -or ($dockerInfo -match '(?i)error|access is denied|cannot connect|daemon unavailable')) { Write-Output 'M10 PostgreSQL Docker tests: UNAVAILABLE (Docker daemon unavailable)'; exit 0 }
if ($env:POSTGRES_TEST_CONNECTIONSTRING) {
    $psql = Get-Command psql -ErrorAction SilentlyContinue
    if (-not $psql) { Write-Output 'M10 PostgreSQL Docker tests: UNAVAILABLE (psql executable not installed)'; exit 0 }
    Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'm10_postgres_assertions.sql') | psql $env:POSTGRES_TEST_CONNECTIONSTRING
    if ($LASTEXITCODE -ne 0) { Write-Output 'M10 PostgreSQL Docker tests: FAIL'; exit $LASTEXITCODE }
    Write-Output 'M10 PostgreSQL Docker tests: PASS'
    exit 0
}
Write-Output 'M10 PostgreSQL Docker tests: AVAILABLE (runner integration required)'
Write-Output 'Assertions: fresh/upgrade, RLS/FORCE RLS, replay, partition pruning/boundaries, bounded backfill resume, retention preview/detach/drop.'
