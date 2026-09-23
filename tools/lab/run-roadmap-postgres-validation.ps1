[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TestAssembly,
    [string] $PostgreSqlBin = 'C:\Program Files\PostgreSQL\18\bin',
    [Parameter(Mandatory)][string] $OutputRoot,
    [string] $AdditionalTestFilter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# An isolated loopback cluster; never reconfigure, stop, migrate or delete an
# existing repository. No credentials are accepted on the command line.
$runRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('postgres-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$passwordFile = Join-Path $runRoot 'password.tmp'
$started = $false
$testExit = 1
$previousProfile = $env:SQLOBSERVER_VALIDATION_PROFILE
$previousConnection = $env:SQLOBSERVER_RELEASE_POSTGRES
New-Item -ItemType Directory -Path $runRoot | Out-Null
$acl = Get-Acl -LiteralPath $runRoot
$acl.SetAccessRuleProtection($true, $false)
$acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $runRoot -AclObject $acl

try {
    $version = & (Join-Path $PostgreSqlBin 'initdb.exe') --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch 'PostgreSQL\) 18\.') { throw 'PostgreSQL 18 binaries are required.' }
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::WriteAllText($passwordFile, $password, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PostgreSqlBin 'initdb.exe') -D $dataRoot -U roadmap_test_admin -A scram-sha-256 --encoding=UTF8 --no-locale "--pwfile=$passwordFile" *> (Join-Path $runRoot 'initdb.log')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated cluster initialization failed; inspect initdb.log.' }
    Remove-Item -LiteralPath $passwordFile
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    & (Join-Path $PostgreSqlBin 'pg_ctl.exe') -D $dataRoot -l (Join-Path $runRoot 'postgres.log') -o "-h 127.0.0.1 -p $port" -w -t 30 start
    if ($LASTEXITCODE -ne 0) { throw 'Isolated cluster start failed.' }
    $started = $true
    $env:SQLOBSERVER_VALIDATION_PROFILE = 'Release' # selects external fixture, not certification tests
    $env:SQLOBSERVER_RELEASE_POSTGRES = "Host=127.0.0.1;Port=$port;Database=postgres;Username=roadmap_test_admin;Password=$password;Pooling=false;Include Error Detail=false"
    # This one case owns a Docker container regardless of the fixture setting.
    $filter = 'Category=RequiresPostgreSql&FullyQualifiedName!~LeaseFenceAndMigrationHistoryPersistAcrossDatabaseRestart'
    if (-not [string]::IsNullOrWhiteSpace($AdditionalTestFilter)) { $filter += "&($AdditionalTestFilter)" }
    & dotnet vstest ([IO.Path]::GetFullPath($TestAssembly)) "/TestCaseFilter:$filter" "/Logger:trx;LogFileName=postgres.trx" "/ResultsDirectory:$runRoot"
    $testExit = $LASTEXITCODE
    [ordered]@{
        kind = 'isolated-postgres-functional-validation'; postgresVersion = $version
        testExitCode = $testExit; measuredAtUtc = [DateTime]::UtcNow.ToString('o')
        testFilter = $filter
        omitted = @('Docker restart test', 'M12 external certification')
        productionRepositoryModified = $false; outputDirectory = $runRoot
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8
} finally {
    if ($started) {
        & (Join-Path $PostgreSqlBin 'pg_ctl.exe') -D $dataRoot -w -t 30 -m fast stop
        if ($LASTEXITCODE -ne 0) { Write-Warning "Temporary cluster requires cleanup: $dataRoot"; $testExit = 1 }
    }
    Remove-Item -LiteralPath $passwordFile -ErrorAction SilentlyContinue
    $env:SQLOBSERVER_VALIDATION_PROFILE = $previousProfile
    $env:SQLOBSERVER_RELEASE_POSTGRES = $previousConnection
    $password = $null
    Write-Output "Functional validation artifacts: $runRoot"
}
exit $testExit
