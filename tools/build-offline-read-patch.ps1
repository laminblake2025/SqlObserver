#requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BaseManifestPath,
    [Parameter(Mandatory)][string] $InstalledHistoryPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $RepositoryDatabase = 'sqlobserver_test'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($RepositoryDatabase -cnotmatch '\Asqlobserver_test[a-z0-9_]{0,32}\z') {
    throw 'Use an installer-supported lowercase sqlobserver_test database name (maximum 48 characters).'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
$base = Get-Content -LiteralPath $BaseManifestPath -Raw | ConvertFrom-Json
if ($base.kind -cne 'lab-test' -or $base.sourceCommit -cne 'ad06cedad20834df71f197e445b5dd768781a53f' -or
    $base.sourceTree -cne 'fc0b353268ecfa6f2de35113941157ec50d9f226' -or $base.runtimeIdentifier -cne 'win-x64') {
    throw 'The supplied base package is not the reviewed Windows test build.'
}
$history = [regex]::Matches((Get-Content -LiteralPath $InstalledHistoryPath -Raw),
    '(?m)^\s*(\d+)\s*\|\s*([0-9]{4}_[A-Za-z0-9_]+\.sql)\s*\|\s*([0-9a-f]{64})\s*$')
$catalog = @(Get-Content -LiteralPath (Join-Path $repo 'database/migrations/checksums.sha256') | Where-Object { $_.Trim() })
if ($history.Count -ne 379 -or $catalog.Count -ne 380) { throw 'Expected the exact 379-row installed history and 380-entry patch catalog.' }
$values = @()
for ($index = 0; $index -lt 380; $index++) {
    if ($catalog[$index] -notmatch '^([0-9a-f]{64})  ([0-9]{4}_[a-z0-9_]+\.sql)$') { throw 'Malformed migration catalog.' }
    $hash = $Matches[1]; $name = $Matches[2]; $number = $index + 1
    if ($index -lt 379 -and ([int]$history[$index].Groups[1].Value -ne $number -or
        $history[$index].Groups[2].Value -cne $name -or $history[$index].Groups[3].Value -cne $hash)) {
        throw "Installed history differs at migration $number."
    }
    $values += "($number, '$name', '$hash')"
}
$migrationName = '0380_recent_completion_read_path.sql'
$migrationHash = '1ea3a64133d1841774254e34944a99baca23956c7819265430777ae8999c4cc7'
$migrationPath = Join-Path $repo ('database/migrations/' + $migrationName)
if ((Get-FileHash -LiteralPath $migrationPath).Hash -ine $migrationHash) { throw 'Migration 380 is not the tested payload.' }
New-Item -ItemType Directory -Path $output | Out-Null
function Write-Utf8([string] $Name, [string] $Text) {
    [IO.File]::WriteAllText((Join-Path $output $Name), $Text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath $migrationPath -Destination (Join-Path $output $migrationName)
$guard = @'
DO $guard$
DECLARE
    n integer;
    drift boolean;
BEGIN
    IF current_database() <> '__REPOSITORY_DATABASE__' OR
       current_setting('server_version_num')::integer NOT BETWEEN 180000 AND 189999 THEN
        RAISE EXCEPTION 'Expected __REPOSITORY_DATABASE__ on PostgreSQL 18.x';
    END IF;
    SELECT count(*) INTO n FROM system.schema_migration;
    IF n NOT IN (379,380) THEN
        RAISE EXCEPTION 'Expected exactly 379 or 380 recorded migrations';
    END IF;
    WITH expected(migration_number,migration_name,sha256) AS (VALUES
__VALUES__)
    SELECT EXISTS (
        SELECT 1 FROM system.schema_migration AS actual
        FULL JOIN expected USING (migration_number)
        WHERE coalesce(actual.migration_number, expected.migration_number) <= n
          AND (actual.migration_name IS DISTINCT FROM expected.migration_name OR
               actual.sha256 IS DISTINCT FROM expected.sha256)) INTO drift;
    IF drift THEN
        RAISE EXCEPTION 'Repository history differs from the exact reviewed 379/380 baseline';
    END IF;
    IF n=379 AND to_regprocedure('reporting.find_completed_run(uuid,text,bigint,bigint,boolean,uuid,timestamptz,uuid)') IS NOT NULL THEN
        RAISE EXCEPTION 'Unrecorded helper already exists; inspect repository drift';
    END IF;
END $guard$;
'@
$guard = $guard.Replace('__VALUES__', ($values -join ",`n")).Replace('__REPOSITORY_DATABASE__', $RepositoryDatabase)
Write-Utf8 'guard.sql' $guard
$post = @'
DO $verify$
DECLARE helper oid := to_regprocedure('reporting.find_completed_run(uuid,text,bigint,bigint,boolean,uuid,timestamptz,uuid)');
BEGIN
    IF (SELECT count(*) FROM system.schema_migration) <> 380 OR helper IS NULL THEN
        RAISE EXCEPTION 'Migration 380 is not installed';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE oid=helper AND NOT prosecdef AND provolatile='s'
                   AND proowner='sqlobserver_migrator'::regrole) OR EXISTS (
        SELECT 1 FROM pg_proc AS p CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) AS a
        WHERE p.oid=helper AND a.privilege_type='EXECUTE' AND a.grantee <> p.proowner) THEN
        RAISE EXCEPTION 'Helper owner or execution privileges differ';
    END IF;
END $verify$;
'@
Write-Utf8 'postconditions.sql' $post
Write-Utf8 'check.sql' @'
\set ON_ERROR_STOP on
BEGIN READ ONLY;
SET LOCAL statement_timeout = '30s';
\ir guard.sql
SELECT count(*)=380 AS already_applied FROM system.schema_migration \gset
\if :already_applied
\ir postconditions.sql
\echo Migration 380 is already installed and verified.
\else
\echo Exact 379-migration baseline verified; patch can proceed.
\endif
ROLLBACK;
'@
Write-Utf8 'verify.sql' @'
\set ON_ERROR_STOP on
BEGIN READ ONLY;
SET LOCAL statement_timeout = '30s';
\ir guard.sql
\ir postconditions.sql
SELECT current_database() AS database, count(*) AS verified_migrations FROM system.schema_migration;
ROLLBACK;
'@
Write-Utf8 'apply.sql' @'
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
DO $lock$
BEGIN
    IF NOT pg_try_advisory_xact_lock(6003663681560464690::bigint) THEN
        RAISE EXCEPTION 'Another migration runner holds the repository migration lock';
    END IF;
END $lock$;
LOCK TABLE system.schema_migration IN EXCLUSIVE MODE;
\ir guard.sql
SELECT count(*)=380 AS already_applied FROM system.schema_migration \gset
\if :already_applied
\ir postconditions.sql
\echo Migration 380 was already applied; no DDL was repeated.
\else
CREATE TEMP TABLE patch_route_before ON COMMIT DROP AS
    SELECT to_jsonb(route) AS value FROM system.collection_run_v2_health_read_route AS route;
\ir 0380_recent_completion_read_path.sql
RESET ROLE;
INSERT INTO system.schema_migration (migration_number,migration_name,sha256)
VALUES (380,'0380_recent_completion_read_path.sql','1ea3a64133d1841774254e34944a99baca23956c7819265430777ae8999c4cc7');
\ir postconditions.sql
DO $unchanged$
BEGIN
    IF EXISTS ((SELECT value FROM pg_temp.patch_route_before EXCEPT ALL SELECT to_jsonb(route) FROM system.collection_run_v2_health_read_route AS route)
        UNION ALL (SELECT to_jsonb(route) FROM system.collection_run_v2_health_read_route AS route EXCEPT ALL SELECT value FROM pg_temp.patch_route_before)) THEN
        RAISE EXCEPTION 'Protected read-route setting changed; rolling back';
    END IF;
END $unchanged$;
\endif
COMMIT;
\echo Migration 380 transaction completed.
'@
$readme = @'
SQL Observer Test: offline read-performance patch 380
Unsigned test-only patch for Windows Server 2025 / win-x64.
Reviewed base: ad06cedad20834df71f197e445b5dd768781a53f, database __REPOSITORY_DATABASE__, PostgreSQL 18.x.
This package is bound to this exact database name in its manifest and SQL guard.

1. Transfer the ZIP and its separate .sha256 file through your approved corporate transfer process.
2. Compare Get-FileHash .\SqlObserver-Test-ReadPatch-380.zip -Algorithm SHA256 with the separate hash.
3. Extract the ZIP to a new local folder. Open PowerShell as administrator and change into that folder.
4. Run the compatibility check (does not stop services or apply changes):
   .\Invoke-SqlObserverReadPatch.ps1
5. Apply during a maintenance window:
   .\Invoke-SqlObserverReadPatch.ps1 -Apply

If Windows blocks the downloaded script, follow your organization's script approval policy.
The script supports Windows PowerShell 5.1 and PowerShell 7. PostgreSQL 18 tools default to:
C:\Program Files\PostgreSQL\18\bin. Override -PostgresBin if required.
Install root defaults to C:\Program Files\SqlObserver Test. Override -InstallRoot if required.
PostgreSQL administrator defaults to postgres; override -Administrator if needed.
Enter the PostgreSQL password separately at each hidden prompt. Do not use your Windows password.

The script verifies the supplied build's 286 package files and the complete migration ledger.
It checks the installed service configurations to locate the repository and its existing TLS root.
TLS hostname/certificate validation remains enabled. No configuration files are rewritten.
The apply run stops SqlObserverTestCollector and SqlObserverTestServer, creates a private full
custom-format database backup, checks that pg_restore can list it, then applies migration 380.
It restores each service's original running/stopped state even if a later step fails.
It does not download software, change identities, alter target certificate trust, or update MCP.

The patch improves recent collection lookup for health, sessions, requests, blocking and waits.
Historical fallback and exact snapshot/cursor behavior are retained. No telemetry is deleted.
The migration and its history row commit together under the application's migration lock.
Failure before COMMIT rolls back database changes. If a connection is lost during COMMIT,
rerun the compatibility check to determine whether 380 committed before retrying.
An exact existing 380 is accepted and does not repeat the migration.

Backups: C:\ProgramData\SqlObserverPatchBackups\read-380-<unique id>\before-380.dump
Keep this directory private: the database backup may contain captured query text and other data.
The adjacent service-states.json records which services were running before the patch.
A readable dump is checked, but a full restore rehearsal is not performed automatically.

After installation, confirm the dashboard loads, collectors remain current, and health,
sessions, requests, blocking, waits, reports and saved investigations open normally.
No check can certify the disconnected corporate machine before you run these steps there.
The patch does not include workstation dashboard relay or browser-test changes.

Recovery after a COMMITTED patch:
Do not delete migration 380 or manually rewind the ledger. Stop both test services and have
your DBA restore before-380.dump into a SEPARATE empty recovery database using pg_restore
--exit-on-error. Validate the restored database before any cutover. Restoring a backup loses
data collected after that backup; preserve the current database first. A recovery involving
renaming databases or changing service configurations requires a separately reviewed cutover.
The original installer and old MigrationHost know only migrations through 379: do not run
their assessment/migration actions against a 380 database. A future updater must know 380.

Validation of migration 380: native Windows/PostgreSQL VDI application and rollback probes,
24 semantic assertions, 14 API requests, four report types/eight exports, snapshot pagination,
desktop/mobile browser checks. The packaged wrapper's validation is recorded separately.
Wrapper controls: see the adjacent package validation records for this database-specific revision.
The original package passed 17 fixture checks in Windows PowerShell 5.1 and PowerShell 7.
Transaction protocol: 17 checks passed on the VDI in a separate PostgreSQL database,
including failed-DDL rollback, checksum drift, migration-lock contention and repeat apply.
That protocol fixture uses stand-in DDL; the exact full migration's separate native VDI
probe and application results are the evidence for its application behavior. The complete
service-stop/backup/update sequence still requires execution on your disconnected server.
This is a test patch, not a production-qualified or signed installer.
'@
Write-Utf8 'README.txt' ($readme.Replace('__REPOSITORY_DATABASE__', $RepositoryDatabase))
$files = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ path=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
})
$baseFiles = @($base.files | ForEach-Object {
    if ($_.path -notmatch '^[A-Za-z0-9_./-]+$' -or $_.path.Split('/') -contains '..' -or $_.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Invalid base file inventory.' }
    [ordered]@{path=$_.path;sha256=$_.sha256}
})
if ($baseFiles.Count -ne 286) { throw 'Unexpected base file count.' }
$manifest = [ordered]@{
    schemaVersion=1;kind='sqlobserver-test-read-patch-380';productionSupported=$false
    baseSourceCommit=$base.sourceCommit;baseSourceTree=$base.sourceTree
    installedHistoryEvidenceSha256=(Get-FileHash -LiteralPath $InstalledHistoryPath).Hash.ToLowerInvariant()
    migration=380;migrationSha256=$migrationHash;repositoryDatabase=$RepositoryDatabase;payload=$files;baseFiles=$baseFiles
}
Write-Utf8 'patch-manifest.json' ($manifest | ConvertTo-Json -Depth 8)
$manifestHash = (Get-FileHash -LiteralPath (Join-Path $output 'patch-manifest.json')).Hash.ToLowerInvariant()
$template = Get-Content -LiteralPath (Join-Path $repo 'installer/test/Invoke-SqlObserverReadPatch.ps1') -Raw
Write-Utf8 'Invoke-SqlObserverReadPatch.ps1' ($template.Replace('__PATCH_MANIFEST_SHA256__', $manifestHash))
$zip = Join-Path ([IO.Path]::GetDirectoryName($output)) 'SqlObserver-Test-ReadPatch-380.zip'
if (Test-Path -LiteralPath $zip) { throw 'Output ZIP already exists.' }
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $output -File | ForEach-Object FullName) -DestinationPath $zip
$zipHash = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zip + '.sha256', $zipHash + '  ' + [IO.Path]::GetFileName($zip) + "`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{zip=$zip;sha256=$zipHash;baseFiles=$baseFiles.Count;payloadFiles=$files.Count;migrationSha256=$migrationHash}
