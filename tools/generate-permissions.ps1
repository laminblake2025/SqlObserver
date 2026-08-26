[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(15, 16, 17)]
    [int] $SqlServerMajorVersion,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Principal,

    [ValidateSet('Grant', 'Remove')]
    [string] $Operation = 'Grant',

    [string] $OutputPath,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-WindowsPrincipal {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    if ($Value.Length -gt 128 -or $Value -cne $Value.Trim()) {
        throw 'Principal must be trimmed and contain at most 128 characters.'
    }

    foreach ($character in $Value.ToCharArray()) {
        if ([char]::IsControl($character)) {
            throw 'Principal must not contain control characters.'
        }
    }

    $isDownLevelName = $Value -match '^[^\\@]+\\[^\\]+$'
    $isUserPrincipalName = $Value -match '^[^@\\\s]+@[^@\\\s]+$'
    if (-not $isDownLevelName -and -not $isUserPrincipalName) {
        throw 'Principal must be a Windows DOMAIN\name or user@domain identity.'
    }

    if ($Value.Equals('sysadmin', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The sysadmin role is not a valid SqlObserver monitoring principal.'
    }
}

Assert-WindowsPrincipal -Value $Principal

$principalLiteral = $Principal.Replace("'", "''", [StringComparison]::Ordinal)
$principalIdentifier = $Principal.Replace(']', ']]', [StringComparison]::Ordinal)
$permissionName = if ($SqlServerMajorVersion -eq 15) {
    'VIEW SERVER STATE'
}
else {
    '##MS_ServerPerformanceStateReader##'
}

$grantStatement = if ($SqlServerMajorVersion -eq 15) {
@"
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.server_permissions AS permission
        WHERE permission.grantee_principal_id = SUSER_ID(N'$principalLiteral')
          AND permission.class = 100
          AND permission.permission_name = N'VIEW SERVER STATE'
          AND permission.state IN (N'G', N'W')
    )
    BEGIN
        GRANT VIEW SERVER STATE TO [$principalIdentifier];
    END;
"@
}
else {
@"
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.server_role_members AS membership
        INNER JOIN sys.server_principals AS role_principal
            ON role_principal.principal_id = membership.role_principal_id
        INNER JOIN sys.server_principals AS member_principal
            ON member_principal.principal_id = membership.member_principal_id
        WHERE role_principal.name = N'##MS_ServerPerformanceStateReader##'
          AND member_principal.name = N'$principalLiteral'
    )
    BEGIN
        ALTER SERVER ROLE [##MS_ServerPerformanceStateReader##] ADD MEMBER [$principalIdentifier];
    END;
"@
}

$removeStatement = if ($SqlServerMajorVersion -eq 15) {
@"
    IF EXISTS
    (
        SELECT 1
        FROM sys.server_permissions AS permission
        WHERE permission.grantee_principal_id = SUSER_ID(N'$principalLiteral')
          AND permission.class = 100
          AND permission.permission_name = N'VIEW SERVER STATE'
          AND permission.state IN (N'G', N'W')
    )
    BEGIN
        REVOKE VIEW SERVER STATE FROM [$principalIdentifier];
    END;
"@
}
else {
@"
    IF EXISTS
    (
        SELECT 1
        FROM sys.server_role_members AS membership
        INNER JOIN sys.server_principals AS role_principal
            ON role_principal.principal_id = membership.role_principal_id
        INNER JOIN sys.server_principals AS member_principal
            ON member_principal.principal_id = membership.member_principal_id
        WHERE role_principal.name = N'##MS_ServerPerformanceStateReader##'
          AND member_principal.name = N'$principalLiteral'
    )
    BEGIN
        ALTER SERVER ROLE [##MS_ServerPerformanceStateReader##] DROP MEMBER [$principalIdentifier];
    END;
"@
}

$script = @"
-- SqlObserver capability.connection least-privilege permission plan.
-- Generated offline. Review and run independently as an authorized DBA.
-- Target SQL Server major: $SqlServerMajorVersion
-- Existing Windows principal: [$principalIdentifier]
-- Selected operation: $Operation
-- This script does not create a login, grant sysadmin, connect to a target,
-- configure the server, change Query Store, or create/change Extended Events.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @sqlobserver_expected_major int = $SqlServerMajorVersion;
DECLARE @sqlobserver_operation nvarchar(6) = N'$Operation';
DECLARE @sqlobserver_principal sysname = N'$principalLiteral';

IF CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')) <> @sqlobserver_expected_major
BEGIN
    THROW 51000, 'SqlObserver permission plan does not match this SQL Server major version.', 1;
END;

IF CONVERT(int, SERVERPROPERTY(N'EngineEdition')) NOT IN (2, 3, 4)
BEGIN
    THROW 51000, 'SqlObserver permission plan does not support this SQL Server engine edition.', 1;
END;

IF CONVERT(nvarchar(1), SERVERPROPERTY(N'PathSeparator')) <> N'\'
BEGIN
    THROW 51000, 'SqlObserver permission plan supports SQL Server on Windows only.', 1;
END;

IF SUSER_ID(@sqlobserver_principal) IS NULL
BEGIN
    THROW 51000, 'The pre-provisioned Windows principal does not exist on this SQL Server.', 1;
END;

IF COALESCE(IS_SRVROLEMEMBER(N'sysadmin', @sqlobserver_principal), 0) = 1
BEGIN
    THROW 51000, 'SqlObserver refuses to configure a sysadmin monitoring principal.', 1;
END;

IF @sqlobserver_operation = N'Grant'
BEGIN
    -- Grant section: $permissionName
$grantStatement
    -- M9 read-only history permissions; this plan never adds an Agent server role.
    -- The database principal is created only when it is absent and must map to
    -- the already-provisioned Windows login.  No login or server role is created.
    USE [msdb];
    IF DB_ID(N'msdb') IS NULL
    BEGIN
        THROW 51001, 'The msdb database is unavailable for the M9 history grant.', 1;
    END;
    IF EXISTS
    (
        SELECT 1
        FROM sys.database_principals AS database_principal
        WHERE database_principal.name = @sqlobserver_principal
          AND database_principal.authentication_type_desc NOT IN (N'INSTANCE', N'NONE')
    )
    BEGIN
        THROW 51001, 'The existing msdb principal is not an instance-mapped user.', 1;
    END;
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.database_principals AS database_principal
        WHERE database_principal.name = @sqlobserver_principal
          AND SUSER_SNAME(database_principal.sid) = @sqlobserver_principal
    )
    BEGIN
        DECLARE @sqlobserver_create_user nvarchar(776) =
            N'CREATE USER ' + QUOTENAME(@sqlobserver_principal) +
            N' FOR LOGIN ' + QUOTENAME(@sqlobserver_principal) + N';';
        EXEC sys.sp_executesql @sqlobserver_create_user;
    END;
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.database_principals AS database_principal
        WHERE database_principal.name = @sqlobserver_principal
          AND SUSER_SNAME(database_principal.sid) = @sqlobserver_principal
    )
    BEGIN
        THROW 51001, 'The msdb principal could not be verified as mapped to the intended login.', 1;
    END;
    GRANT SELECT ON OBJECT::[dbo].[backupset] TO [$principalIdentifier];
    IF CONVERT(int, SERVERPROPERTY(N'EngineEdition')) IN (2, 3)
    BEGIN
        GRANT SELECT ON OBJECT::[dbo].[sysjobhistory] TO [$principalIdentifier];
    END;
END
ELSE IF @sqlobserver_operation = N'Remove'
BEGIN
    -- Removal section: undo only the M3 capability.connection permission.
$removeStatement
    -- M9 grants are intentionally not revoked: without deployment provenance a
    -- generator must never remove a pre-existing DBA grant.
END
ELSE
BEGIN
    THROW 51000, 'SqlObserver permission plan operation is invalid.', 1;
END;
"@

$script = $script.Replace("`r`n", "`n", [StringComparison]::Ordinal).Replace("`r", "`n", [StringComparison]::Ordinal)
if (-not $script.EndsWith("`n", [StringComparison]::Ordinal)) {
    $script += "`n"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    [Console]::Out.Write($script)
    return
}

if ([IO.Path]::GetExtension($OutputPath) -cne '.sql') {
    throw 'OutputPath must use the .sql extension.'
}

$parentPath = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($parentPath)) {
    $parentPath = (Get-Location).Path
}

$resolvedParent = (Resolve-Path -LiteralPath $parentPath).Path
$resolvedOutput = Join-Path $resolvedParent (Split-Path -Leaf $OutputPath)
if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    throw 'OutputPath already exists. Pass -Force to replace this generated artifact.'
}

[IO.File]::WriteAllText($resolvedOutput, $script, [Text.UTF8Encoding]::new($false))
Write-Host "Generated offline SQL permission plan: $resolvedOutput"
