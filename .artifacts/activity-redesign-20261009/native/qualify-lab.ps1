$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$session=$null;$credential=$null
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'inputs.json') -Raw | ConvertFrom-Json
$report=[ordered]@{runId=$manifest.runId;startedUtc=[DateTime]::UtcNow.ToString('o');status='awaiting_secure_credential';errorCategory=$null}
function Save-Status {$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'status.json') -Encoding UTF8}
try {
    if($manifest.runId -notmatch '^[0-9a-f]{32}$'){throw 'Invalid run identity.'}
    foreach($file in $manifest.files){if($file.name -notin @('guest-workload.ps1','blocker.sql','blocked.sql') -or (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file.name)).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Qualification input changed.'}}
    Save-Status;Write-Host 'Verify the Activity redesign using the existing isolated SQL Server lab.';Write-Host 'The two bounded workers update one lab row inside transactions and roll back. Services and schemas are unchanged.'
    Write-Host 'Enter the existing WIN-QNGOV5GDM24 Administrator password here. It is never saved or sent to the chat.'
    $password=Read-Host 'VM Administrator password' -AsSecureString;$credential=New-Object Management.Automation.PSCredential('Administrator',$password);$password=$null
    $session=New-PSSession -VMId '48a614e9-3f6c-4aca-b208-2f95075097e0' -Credential $credential;$credential.Password.Dispose();$credential=$null
    $report.status='running';Save-Status
    $result=Invoke-Command -Session $session -FilePath (Join-Path $PSScriptRoot 'guest-workload.ps1') -ArgumentList $manifest.runId,(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'blocker.sql') -Raw),(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'blocked.sql') -Raw)
    $result | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'workload-result.json') -Encoding UTF8
    $parsed=$result | ConvertFrom-Json
    if(!$parsed.rollbackVerified -or @($parsed.workers | Where-Object state -cne 'completed').Count -gt 0 -or @($parsed.workers).Count -ne 2){throw 'Workload or rollback verification failed.'}
    $report.status='completed';Write-Host 'Bounded workload completed; rollback and session cleanup verified.'
} catch {$report.status='failed';$report.errorCategory=$_.Exception.GetType().FullName;Write-Host 'Qualification stopped. See the sanitized status in Codex.'}
finally {if($session){Remove-PSSession $session};if($credential){$credential.Password.Dispose()};$report.finishedUtc=[DateTime]::UtcNow.ToString('o');Save-Status}
