[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri] $BaseUri,
    [Parameter(Mandatory)][string] $TargetId,
    [Parameter(Mandatory)][string] $OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($BaseUri.Scheme -ne 'https' -or $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment) {
    throw 'Provide an HTTPS origin without credentials, query, or fragment.'
}
$origin = $BaseUri.GetLeftPart([UriPartial]::Authority)
$target = [guid]::Parse($TargetId).ToString('D')
$to = [DateTimeOffset]::UtcNow
$from = $to.AddHours(-1)
$window = 'fromUtc=' + [uri]::EscapeDataString($from.ToString('o')) + '&toUtc=' + [uri]::EscapeDataString($to.ToString('o'))

function Read-Api([string] $Path) {
    $response = Invoke-WebRequest -Uri "$origin$Path" -UseDefaultCredentials -TimeoutSec 35 -SkipHttpErrorCheck
    $body = if ($response.Content) { $response.Content | ConvertFrom-Json } else { $null }
    return [pscustomobject]@{ status = [int]$response.StatusCode; body = $body }
}

$overview = Read-Api "/api/v1/overview?$window"
$evidence = @($overview.body.evidence)
$ages = @($evidence | Where-Object { $null -ne $_.lastObservedUtc } | ForEach-Object {
    ([DateTimeOffset]::UtcNow - [DateTimeOffset]$_.lastObservedUtc).TotalSeconds
})
$status = Read-Api "/api/v1/observation-targets/$target/query-performance/status?$window"
$top = Read-Api "/api/v1/observation-targets/$target/query-performance/top?metric=cpu&limit=1&$window"
$selectedSource = if (@($top.body.items).Count -and $top.body.items[0].source -in @('query_store', 'plan_cache')) {
    [string]$top.body.items[0].source
} else { 'query_store' }
$source = Read-Api "/api/v1/observation-targets/$target/query-performance/top?metric=cpu&limit=1&source=$selectedSource&$window"
$database = if (@($top.body.items).Count) { [int]$top.body.items[0].databaseId } elseif (@($status.body.databaseCatalog).Count) {
    [int]$status.body.databaseCatalog[0].databaseId
} else { $null }
$databaseFilter = if ($null -ne $database) {
    Read-Api "/api/v1/observation-targets/$target/query-performance/top?metric=cpu&limit=1&databaseId=$database&$window"
} else { $null }
$queryContinuation = if ($top.body.nextCursor) {
    Read-Api "/api/v1/observation-targets/$target/query-performance/top?metric=cpu&limit=1&$window&cursor=$([uri]::EscapeDataString($top.body.nextCursor))"
} else { $null }
$queryCursorFilterMismatch = if ($top.body.nextCursor) {
    Read-Api "/api/v1/observation-targets/$target/query-performance/top?metric=cpu&limit=1&source=$selectedSource&$window&cursor=$([uri]::EscapeDataString($top.body.nextCursor))"
} else { $null }
$deadlocks = Read-Api "/api/v1/observation-targets/$target/deadlocks?limit=1&$window"
$deadlockContinuation = if ($deadlocks.body.nextCursor) {
    Read-Api "/api/v1/observation-targets/$target/deadlocks?limit=1&cursor=$([uri]::EscapeDataString($deadlocks.body.nextCursor))"
} else { $null }
$result = [ordered]@{
    measuredAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    overviewStatus = $overview.status
    overviewEvidenceCount = $evidence.Count
    overviewCollectionStates = @($evidence | ForEach-Object collectionState)
    overviewLastObservedValues = @($evidence | ForEach-Object lastObservedUtc)
    observationAgesSeconds = @($ages | ForEach-Object { [Math]::Round($_, 1) })
    queryStatus = $status.status
    queryTopStatus = $top.status
    queryTopItems = @($top.body.items).Count
    queryTopContinuationAvailable = [bool]$top.body.nextCursor
    queryTopContinuationStatus = if ($queryContinuation) { $queryContinuation.status } else { $null }
    queryTopContinuationDistinct = if ($queryContinuation -and @($queryContinuation.body.items).Count) {
        $top.body.items[0].observationKey -ne $queryContinuation.body.items[0].observationKey -or
        $top.body.items[0].collectionRunId -ne $queryContinuation.body.items[0].collectionRunId
    } else { $null }
    queryCursorFilterMismatchStatus = if ($queryCursorFilterMismatch) { $queryCursorFilterMismatch.status } else { $null }
    querySourceFilterStatus = $source.status
    querySourceFilter = $selectedSource
    querySourceFilterItems = @($source.body.items).Count
    querySourceFilterMatches = @($source.body.items | Where-Object { $_.source -ne $selectedSource }).Count -eq 0
    queryDatabaseFilterStatus = if ($databaseFilter) { $databaseFilter.status } else { $null }
    queryDatabaseFilterItems = if ($databaseFilter) { @($databaseFilter.body.items).Count } else { $null }
    queryDatabaseFilterMatches = if ($databaseFilter) { @($databaseFilter.body.items | Where-Object { $_.databaseId -ne $database }).Count -eq 0 } else { $null }
    deadlockStatus = $deadlocks.status
    deadlockItems = @($deadlocks.body.items).Count
    deadlockContinuationAvailable = [bool]$deadlocks.body.nextCursor
    deadlockContinuationWithoutDatesStatus = if ($deadlockContinuation) { $deadlockContinuation.status } else { $null }
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$result | ConvertTo-Json -Depth 5
if (@($overview.status, $status.status, $top.status, $source.status, $deadlocks.status) | Where-Object { $_ -ne 200 }) { exit 1 }
if ($databaseFilter -and $databaseFilter.status -ne 200) { exit 1 }
if ($queryContinuation -and $queryContinuation.status -ne 200) { exit 1 }
if ($queryCursorFilterMismatch -and $queryCursorFilterMismatch.status -ne 400) { exit 1 }
if ($deadlockContinuation -and $deadlockContinuation.status -ne 200) { exit 1 }
if (-not $result.querySourceFilterMatches -or $result.queryDatabaseFilterMatches -eq $false) { exit 1 }
if ($result.queryTopItems -gt 0 -and ($result.querySourceFilterItems -eq 0 -or $result.queryDatabaseFilterItems -eq 0)) { exit 1 }
if ($result.queryTopContinuationDistinct -eq $false) { exit 1 }
