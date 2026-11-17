#requires -Version 5.1
param([Parameter(Mandatory)][string]$PackageDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$package=[IO.Path]::GetFullPath($PackageDirectory)
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $package 'Invoke-SqlObserverReadPatch.ps1'),[ref]$null,[ref]$errors)
if($errors.Count){throw ($errors.Message -join '; ')}
foreach($definition in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){
    Invoke-Expression $definition.Extent.Text
}
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name){if(!$Condition){throw "Failed: $Name"};$checks.Add($Name)}
function Reject([scriptblock]$Action,[string]$Name,[string]$ExpectedMessage){
    $rejected=$false;$message=''
    try{&$Action}catch{$rejected=$true;$message=$_.Exception.Message}
    Check $rejected $Name
    if($ExpectedMessage){
        Check ($message.Contains($ExpectedMessage)) ($Name+'-specific-message')
        Check (!$message.Contains('fixture-secret-password') -and !$message.Contains('fixture-secret-user')) ($Name+'-no-credentials')
    }
}
$fixture=Join-Path ([IO.Path]::GetDirectoryName($package)) ('fixtures-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
$manifest=Get-Content -LiteralPath (Join-Path $package 'patch-manifest.json') -Raw|ConvertFrom-Json
$repositoryDatabase=[string]$manifest.repositoryDatabase
Assert-TestRepositoryDatabase $repositoryDatabase
foreach($invalid in @('postgres','sqlobserver_lab','SqlObserver_test',('sqlobserver_test'+('a'*33)),"sqlobserver_test`n","sqlobserver_test';SELECT 1;--")){
    Reject {Assert-TestRepositoryDatabase $invalid} ('reject-invalid-target-'+$checks.Count)
}
Check ((Get-Content -LiteralPath (Join-Path $package 'guard.sql') -Raw).Contains("current_database() <> '$repositoryDatabase'")) 'sql-guard-pins-exact-manifest-database'
Check ($manifest.baseFiles.Count -eq 286) 'exact-base-inventory'
Check ($manifest.payload.Count -eq 7) 'complete-sql-payload'
foreach($file in $manifest.payload){Assert-Hash (Get-ChildPath $package $file.path) $file.sha256}
$checks.Add('all-payload-hashes')
Check ($ast.Extent.Text.Contains((Get-FileHash -LiteralPath (Join-Path $package 'patch-manifest.json')).Hash.ToLowerInvariant())) 'runner-pins-manifest'
Reject {Get-ChildPath $package '../escape.sql'} 'reject-parent-traversal'
Reject {Get-ChildPath $package 'C:/escape.sql'} 'reject-absolute-payload-path'
Reject {Assert-Hash (Join-Path $package 'guard.sql') ('0'*64)} 'reject-changed-payload'
$InstallRoot=Join-Path $fixture 'Installation'
$content=Join-Path $fixture 'Configuration'
New-Item -ItemType Directory -Path $content|Out-Null
$certificate=Join-Path $fixture 'root.crt'
Set-Content -LiteralPath $certificate 'fixture-public-certificate'
$config=Join-Path $content 'appsettings.json'
function Write-FixtureConnection([string]$Database,[string]$SslMode,[string]$HostName='localhost',[string]$Port='5432'){
    @{ConnectionStrings=@{SqlObserverRepository="Host=$HostName;Port=$Port;Database=$Database;SSL Mode=$SslMode;Root Certificate=$certificate;Username=fixture-secret-user;Password=fixture-secret-password"}}|
        ConvertTo-Json -Depth 4|Set-Content -LiteralPath $config
}
$exe=Join-Path $InstallRoot 'Application/Server/SqlObserver.Server.exe'
$service=[pscustomobject]@{Name='SqlObserverTestServer';PathName='"'+$exe+'" --contentRoot "'+$content+'"'}
Write-FixtureConnection $repositoryDatabase 'VerifyFull'
$safe=Get-ServiceRepository $service 'Server' $repositoryDatabase
Check ($safe.Host -eq 'localhost' -and $safe.Database -ceq $repositoryDatabase -and $safe.RootCertificate -ceq $certificate) 'derive-repository-and-trust-root'
Check ($safe.PSObject.Properties.Name -notcontains 'Password') 'safe-connection-metadata'
Write-FixtureConnection 'another_database' 'VerifyFull'
Reject {Get-ServiceRepository $service 'Server' $repositoryDatabase} 'reject-wrong-database' 'Repository Database mismatch:'
$otherDatabase=if($repositoryDatabase -ceq 'sqlobserver_test'){'sqlobserver_test_identityfix'}else{'sqlobserver_test'}
Write-FixtureConnection $otherDatabase 'VerifyFull'
Reject {Get-ServiceRepository $service 'Server' $repositoryDatabase} 'reject-other-valid-test-database' 'Repository Database mismatch:'
Write-FixtureConnection $repositoryDatabase 'VerifyFull' 'remote.example'
Reject {Get-ServiceRepository $service 'Server' $repositoryDatabase} 'reject-remote-host' 'Repository Host mismatch:'
Write-FixtureConnection $repositoryDatabase 'VerifyFull' 'localhost' '5433'
Reject {Get-ServiceRepository $service 'Server' $repositoryDatabase} 'reject-wrong-port' 'Repository Port mismatch:'
Write-FixtureConnection $repositoryDatabase 'VerifyFull' 'localhost' 'invalid'
Reject {Get-ServiceRepository $service 'Server' $repositoryDatabase} 'identify-nonnumeric-port' 'Repository Port mismatch:'
Write-FixtureConnection $repositoryDatabase 'Require'
Reject {Get-ServiceRepository $service 'Server' $repositoryDatabase} 'reject-reduced-certificate-validation' 'Repository SSL Mode mismatch:'
Write-FixtureConnection $repositoryDatabase 'verify-full' '127.0.0.1'
$safe=Get-ServiceRepository $service 'Server' $repositoryDatabase
Check ($safe.Host -eq '127.0.0.1') 'accept-existing-loopback-and-tls-spelling'
Write-FixtureConnection $repositoryDatabase 'VerifyFull'
$badService=[pscustomobject]@{Name='SqlObserverTestServer';PathName=$service.PathName+' --extra'}
Reject {Get-ServiceRepository $badService 'Server' $repositoryDatabase} 'reject-unexpected-service-command'
# Exercise the exact argument assignment used by psql and pg_dump, with each supported target.
$argumentAssignment=$ast.Find({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$script:connectionArguments'},$true)
foreach($target in @('sqlobserver_test','sqlobserver_test_identityfix')){
    Write-FixtureConnection $target 'VerifyFull'
    $repository=Get-ServiceRepository $service 'Server' $target
    $Administrator='fixture_admin'
    Invoke-Expression $argumentAssignment.Extent.Text
    Check ($script:connectionArguments -contains ('--dbname='+$target)) ('psql-and-backup-target-'+$target)
    Check (@($script:connectionArguments|Where-Object {$_ -like '--dbname=*'}).Count -eq 1) ('single-database-argument-'+$target)
}
Write-FixtureConnection 'sqlobserver_test_identityfix' 'VerifyFull'
$collectorExe=Join-Path $InstallRoot 'Application/Collector/SqlObserver.Collector.exe'
$collectorService=[pscustomobject]@{Name='SqlObserverTestCollector';PathName='"'+$collectorExe+'" --contentRoot "'+$content+'"'}
$collector=Get-ServiceRepository $collectorService 'Collector' 'sqlobserver_test_identityfix'
Check ($collector.Database -ceq 'sqlobserver_test_identityfix') 'collector-accepts-exact-identityfix-target'
Write-FixtureConnection 'sqlobserver_test' 'VerifyFull'
Reject {Get-ServiceRepository $collectorService 'Collector' 'sqlobserver_test_identityfix'} 'collector-rejects-different-database' 'Repository Database mismatch:'
$script:starts=[Collections.Generic.List[string]]::new()
function Start-Service([string]$Name){$script:starts.Add($Name)}
function Get-Service([string]$Name){$value=[pscustomobject]@{Name=$Name};$value|Add-Member ScriptMethod WaitForStatus {param($Status,$Timeout)};return $value}
Restore-ServiceStates @{SqlObserverTestServer='Running';SqlObserverTestCollector='Stopped'}
Check ($script:starts.Count -eq 1 -and $script:starts[0] -eq 'SqlObserverTestServer') 'restore-only-originally-running-service'
$script:starts.Clear()
function Start-Service([string]$Name){$script:starts.Add($Name);if($Name -eq 'SqlObserverTestServer'){throw 'fixture service failure'}}
Reject {Restore-ServiceStates @{SqlObserverTestServer='Running';SqlObserverTestCollector='Running'}} 'report-service-restart-failure'
Check ($script:starts.Count -eq 2) 'attempt-second-restart-after-first-fails'
$oldProgramData=$env:ProgramData
try{
    $env:ProgramData=$fixture
    $private=New-PrivateBackupDirectory
    $acl=Get-Acl -LiteralPath $private
    Check $acl.AreAccessRulesProtected 'private-backup-no-inherited-access'
    $allowed=@([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')
    Check (@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])|Where-Object {$_.IdentityReference.Value -notin $allowed}).Count -eq 0) 'private-backup-only-operator-admin-system'
}finally{$env:ProgramData=$oldProgramData}
$result=[ordered]@{status='passed';checks=$checks;count=$checks.Count;nativeSqlValidation='recorded separately';checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}
$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($package)) 'package-tests.json')
$result|ConvertTo-Json -Depth 5
