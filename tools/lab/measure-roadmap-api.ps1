[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri] $BaseUri,
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateRange(3, 20)][int] $Iterations = 10,
    [string] $RepositorySettingsPath,
    [string] $PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($BaseUri.Scheme -ne 'https' -or $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment) { throw 'Provide an HTTPS origin without credentials, query, or fragment.' }
$origin = $BaseUri.GetLeftPart([UriPartial]::Authority)
$end = [DateTime]::UtcNow
$start = $end.AddHours(-1)
$query = 'fromUtc=' + [Uri]::EscapeDataString($start.ToString('o')) + '&toUtc=' + [Uri]::EscapeDataString($end.ToString('o'))

function Read-RepositoryCounters {
    if (-not $RepositorySettingsPath) { return $null }
    $priorPassword = $env:PGPASSWORD
    try {
        $settings = Get-Content -LiteralPath $RepositorySettingsPath -Raw | ConvertFrom-Json
        $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connection.set_ConnectionString([string]$settings.ConnectionStrings.SqlObserverRepository)
        $env:PGPASSWORD = [string]$connection['Password']
        $counterSql = 'SELECT xact_commit,xact_rollback,blks_read,blks_hit,temp_bytes,tup_returned,tup_fetched,numbackends FROM pg_stat_database WHERE datname=current_database();'
        $csv = & $PsqlPath -X -w --csv -h $connection['Host'] -p $connection['Port'] -U $connection['Username'] -d $connection['Database'] -c $counterSql 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return ($csv | ConvertFrom-Csv | Select-Object -First 1)
    } finally { $env:PGPASSWORD = $priorPassword }
}

$inventory = Invoke-RestMethod -Uri "$origin/api/v1/observation-targets?limit=10" -UseDefaultCredentials -TimeoutSec 20
$registered = @($inventory.items).Count
$before = Read-RepositoryCounters
$samples = @()
$total = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Iterations; $i++) {
    if ($total.Elapsed.TotalMinutes -ge 5) { break }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-WebRequest -Uri "$origin/api/v1/overview?$query" -UseDefaultCredentials -TimeoutSec 35 -SkipHttpErrorCheck
    $watch.Stop()
    $lag = $null
    if ([int]$response.StatusCode -eq 200) {
        $value = $response.Content | ConvertFrom-Json
        $ages = @($value.evidence | Where-Object lastObservedUtc | ForEach-Object { [Math]::Max(0, ([DateTime]::UtcNow - [DateTime]::Parse($_.lastObservedUtc).ToUniversalTime()).TotalSeconds) })
        if ($ages.Count) { $lag = ($ages | Measure-Object -Maximum).Maximum }
    }
    $samples += [ordered]@{ status = [int]$response.StatusCode; durationMs = $watch.Elapsed.TotalMilliseconds; maximumCoreObservationAgeSeconds = $lag }
}
$after = Read-RepositoryCounters
$delta = $null
if ($null -ne $before -and $null -ne $after) {
    $delta = [ordered]@{}
    foreach ($name in @('xact_commit','xact_rollback','blks_read','blks_hit','temp_bytes','tup_returned','tup_fetched')) { $delta[$name] = [long]$after.$name - [long]$before.$name }
    $delta['connectionsAtEnd'] = [long]$after.numbackends
}
$durations = @($samples.durationMs | Sort-Object)
$result = [ordered]@{
    kind = 'existing-deployment-read-only-benchmark'; measuredAtUtc = [DateTime]::UtcNow.ToString('o')
    registeredTargetsInFirstPage = $registered; inventoryTruncated = [bool]$inventory.nextCursor
    iterations = $samples.Count; fromUtc = $start.ToString('o'); toUtc = $end.ToString('o')
    p50Ms = $durations[[int][Math]::Ceiling($durations.Count * 0.5) - 1]
    p95Ms = $durations[[int][Math]::Ceiling($durations.Count * 0.95) - 1]
    repositoryCounterDelta = $delta; samples = $samples
    notes = @('Existing deployed build; not evidence of this working-tree build.', 'Repository counters include concurrent collector activity.', 'Observation age is not end-to-end ingestion lag.', 'Only the actually registered fleet size was exercised.')
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Measured $($samples.Count) requests for $registered registered targets. p50=$([Math]::Round($result.p50Ms,1))ms; p95=$([Math]::Round($result.p95Ms,1))ms."
