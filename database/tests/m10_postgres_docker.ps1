<#$
Docker-backed smoke checks for M10.  The harness is intentionally explicit
about unavailable Docker so CI can classify the result rather than report a
false pass.  A deployment runner may set POSTGRES_TEST_CONNECTIONSTRING and
execute its normal migration runner before adding SQL assertions here.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) { Write-Output 'M10 PostgreSQL Docker tests: UNAVAILABLE (docker executable not installed)'; exit 2 }
$dockerInfo = docker info --format '{{.ServerVersion}}' 2>&1
if ($LASTEXITCODE -ne 0 -or ($dockerInfo -match '(?i)error|access is denied|cannot connect|daemon unavailable')) { Write-Output 'M10 PostgreSQL Docker tests: UNAVAILABLE (Docker daemon unavailable)'; exit 2 }
if ($env:POSTGRES_TEST_CONNECTIONSTRING) {
    $psql = Get-Command psql -ErrorAction SilentlyContinue
    if (-not $psql) { Write-Output 'M10 PostgreSQL Docker tests: UNAVAILABLE (psql executable not installed)'; exit 2 }
    try {
        # ON_ERROR_STOP is mandatory: without it psql can print an assertion
        # error and still return success after processing later statements.
        Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'm10_postgres_assertions.sql') |
            psql --set=ON_ERROR_STOP=1 $env:POSTGRES_TEST_CONNECTIONSTRING
        $psqlExitCode = $LASTEXITCODE
    }
    catch {
        Write-Output 'M10 PostgreSQL Docker tests: FAIL (psql invocation failed)'
        exit 1
    }
    if ($psqlExitCode -ne 0) { Write-Output 'M10 PostgreSQL Docker tests: FAIL'; exit $psqlExitCode }
    Write-Output 'M10 PostgreSQL Docker tests: PASS'
    exit 0
}
Write-Output 'M10 PostgreSQL Docker tests: UNAVAILABLE (POSTGRES_TEST_CONNECTIONSTRING is not configured)'
Write-Output 'No certification result was produced. Assertions require a connected PostgreSQL 18 runner.'
exit 2
