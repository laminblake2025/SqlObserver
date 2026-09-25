param(
    [Parameter(Mandatory = $true)]
    [string] $SqlInstance
)

$ErrorActionPreference = 'Stop'
$probeName = 'sqlobserver_volume_probe_' + [guid]::NewGuid().ToString('N')
$quotedName = '[' + $probeName + ']'

# The login is a disposable local test principal. Its random password is never
# returned, logged, or used by the probe: EXECUTE AS checks the actual grant.
$statement = @"
SET NOCOUNT ON;
DECLARE @major int = CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));
IF @major NOT BETWEEN 15 AND 17 THROW 51000, 'Unsupported SQL Server major version.', 1;
CREATE TABLE #expected (file_count int NOT NULL);
INSERT #expected SELECT COUNT(*) FROM sys.master_files;
DECLARE @password nvarchar(64) = N'Az9!' + REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'');
DECLARE @create_login nvarchar(max) =
    N'CREATE LOGIN $quotedName WITH PASSWORD = ' + QUOTENAME(@password, '''') + N', CHECK_POLICY = ON;';
EXEC (@create_login);
IF @major = 15
    EXEC (N'GRANT VIEW SERVER STATE TO $quotedName;');
ELSE
    EXEC (N'GRANT VIEW SERVER PERFORMANCE STATE TO $quotedName;');
EXEC (N'
EXECUTE AS LOGIN = N''$probeName'';
BEGIN TRY
    SELECT COUNT(*) AS FilesVisibleWithDmvPermissionOnly FROM sys.master_files;
    REVERT;
END TRY
BEGIN CATCH
    REVERT;
    THROW;
END CATCH;');
-- sys.master_files requires separate metadata visibility for fleet-wide rows.
EXEC (N'GRANT VIEW ANY DEFINITION TO $quotedName;');
EXEC (N'
EXECUTE AS LOGIN = N''$probeName'';
BEGIN TRY
    IF IS_SRVROLEMEMBER(N''sysadmin'') <> 0
        THROW 51001, ''The probe login unexpectedly has sysadmin.'', 1;
    DECLARE @rows int, @known int, @identified int, @minimum_free bigint;
    DECLARE @physical_node nvarchar(128) =
        CONVERT(nvarchar(128), SERVERPROPERTY(''ComputerNamePhysicalNetBIOS''));
    WITH bounded_files AS
    (
        SELECT TOP (1001) database_id, file_id
        FROM sys.master_files ORDER BY database_id, file_id
    )
    SELECT @rows = COUNT(*),
           @known = SUM(CASE WHEN volume.total_bytes IS NOT NULL
                                  AND volume.available_bytes IS NOT NULL THEN 1 ELSE 0 END),
           @identified = SUM(CASE WHEN NULLIF(volume.volume_id, N'''') IS NOT NULL
                                       OR NULLIF(volume.volume_mount_point, N'''') IS NOT NULL
                                  THEN 1 ELSE 0 END),
           @minimum_free = MIN(volume.available_bytes)
    FROM bounded_files AS files
    OUTER APPLY sys.dm_os_volume_stats(files.database_id, files.file_id) AS volume;
    IF @rows IS NULL OR @rows = 0 OR @rows > 1000
        THROW 51002, ''The bounded file set is empty or incomplete.'', 1;
    IF @rows <> (SELECT file_count FROM #expected)
        THROW 51003, ''The non-sysadmin login cannot see all database files.'', 1;
    IF @known = 0
        THROW 51004, ''The non-sysadmin login received no known volume capacity.'', 1;
    IF @physical_node IS NULL OR @physical_node = N''''
        THROW 51005, ''The non-sysadmin login received no physical node identity.'', 1;
    SELECT CONVERT(int, SERVERPROPERTY(''ProductMajorVersion'')) AS SqlMajorVersion,
           @rows AS DatabaseFiles, @known AS CapacityAvailable,
           @rows - @known AS CapacityUnknown, @identified AS IdentifiedFiles,
           @minimum_free AS MinimumFreeBytesAcrossFiles,
           IS_SRVROLEMEMBER(N''sysadmin'') AS IsSysadmin;
    REVERT;
END TRY
BEGIN CATCH
    REVERT;
    THROW;
END CATCH;');
"@

try {
    $result = & sqlcmd -S $SqlInstance -d master -E -b -W -l 5 -t 30 -Q $statement 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The least-privilege volume probe failed: $($result -join [Environment]::NewLine)"
    }
    $result
}
finally {
    $cleanup = & sqlcmd -S $SqlInstance -d master -E -b -W -h -1 -l 5 -t 30 -Q `
        "IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$probeName') DROP LOGIN $quotedName;" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The disposable volume probe login could not be removed: $($cleanup -join [Environment]::NewLine)"
    }
}
