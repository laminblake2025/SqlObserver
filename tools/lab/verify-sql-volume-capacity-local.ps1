param(
    [Parameter(Mandatory = $true)]
    [string] $SqlInstance,

    [ValidateRange(1, 1000)]
    [int] $MaximumFiles = 1000
)

$ErrorActionPreference = 'Stop'

function Invoke-ReadOnlySql([string] $statement) {
    $output = & sqlcmd -S $SqlInstance -d master -E -b -W -h -1 -s '|' -l 5 -t 20 -Q $statement 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The SQL Server read-only volume probe failed: $($output -join [Environment]::NewLine)"
    }

    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

$majorRows = @(Invoke-ReadOnlySql "SET NOCOUNT ON; SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));" |
    Where-Object { $_ -match '^\s*\d+\s*$' })
if ($majorRows.Count -ne 1) { throw 'SQL Server did not return one major version.' }
$major = [int]::Parse($majorRows[0].Trim(), [Globalization.CultureInfo]::InvariantCulture)
if ($major -lt 15 -or $major -gt 17) {
    throw "The volume probe supports SQL Server major versions 15-17; found $major."
}

$take = $MaximumFiles + 1
$query = @"
SET NOCOUNT ON;
WITH bounded_files AS
(
    SELECT TOP ($take) database_id, file_id
    FROM sys.master_files
    ORDER BY database_id, file_id
)
SELECT files.database_id, files.file_id, volume.total_bytes, volume.available_bytes
FROM bounded_files AS files
OUTER APPLY sys.dm_os_volume_stats(files.database_id, files.file_id) AS volume
ORDER BY files.database_id, files.file_id;
"@
$rows = @(Invoke-ReadOnlySql $query | Where-Object { $_ -match '^\s*\d+\|' })
if ($rows.Count -gt $MaximumFiles) {
    throw "More than $MaximumFiles SQL database files exist; the bounded probe cannot claim full coverage."
}

$available = 0
$unknown = 0
$minimumFreeBytes = $null
foreach ($row in $rows) {
    $fields = $row.Split('|')
    if ($fields.Count -ne 4) { throw 'The SQL Server volume row changed its expected shape.' }
    foreach ($index in 0, 1) {
        if ($fields[$index].Trim() -notmatch '^\d+$') { throw 'The SQL Server volume row has an invalid file identity.' }
    }
    if ($fields[2].Trim() -eq 'NULL' -and $fields[3].Trim() -eq 'NULL') {
        $unknown++
        continue
    }
    if ($fields[2].Trim() -notmatch '^\d+$' -or $fields[3].Trim() -notmatch '^\d+$') {
        throw 'SQL Server returned incomplete volume capacity.'
    }
    $totalBytes = [long]::Parse($fields[2].Trim(), [Globalization.CultureInfo]::InvariantCulture)
    $freeBytes = [long]::Parse($fields[3].Trim(), [Globalization.CultureInfo]::InvariantCulture)
    if ($totalBytes -le 0 -or $freeBytes -lt 0 -or $freeBytes -gt $totalBytes) {
        throw 'SQL Server returned invalid volume capacity.'
    }
    $available++
    if ($null -eq $minimumFreeBytes -or $freeBytes -lt $minimumFreeBytes) {
        $minimumFreeBytes = $freeBytes
    }
}

[pscustomobject]@{
    SqlMajorVersion = $major
    DatabaseFiles = $rows.Count
    CapacityAvailable = $available
    CapacityUnknown = $unknown
    MinimumFreeBytesAcrossFiles = $minimumFreeBytes
    Scope = 'SQL Server host volumes containing visible database files; repeated volumes are not summed.'
}
