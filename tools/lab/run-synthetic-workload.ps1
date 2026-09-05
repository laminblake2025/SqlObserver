[CmdletBinding()]
param([ValidateSet('All','Traffic','Blocking','Deadlock','Backups','Agent')][string]$Scenario='All')
$ErrorActionPreference='Stop'
if($env:COMPUTERNAME -ne 'WIN-QNGOV5GDM24'){throw 'This workload is restricted to the disposable lab VM.'}
$sqlcmd='C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE'
$runPath=Join-Path 'C:\SqlObserverLab\Workloads' ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $runPath -Force | Out-Null
$names=@()
if($Scenario -in @('All','Traffic')){$names+='traffic'}
if($Scenario -in @('All','Blocking')){$names+=@('blocker','blocked')}
if($Scenario -in @('All','Deadlock')){$names+=@('deadlock-a','deadlock-b')}
if($Scenario -in @('All','Backups')){$names+='backup'}
if($Scenario -in @('All','Agent')){
 $agentService=Get-Service SQLSERVERAGENT
 if($agentService.Status -ne 'Running'){
  if($agentService.StartType -eq 'Disabled'){Set-Service SQLSERVERAGENT -StartupType Manual}
  Start-Service SQLSERVERAGENT
 }
 $names+='agent-failure'
}
$children=@()
try {
foreach($name in $names){
 $script=Join-Path $PSScriptRoot ($name+'.sql')
 if(-not(Test-Path -LiteralPath $script)){throw "Missing scenario $name"}
 $process=Start-Process -FilePath $sqlcmd -ArgumentList @('-S','tcp:WIN-QNGOV5GDM24,1433','-E','-N','-b','-l','15','-t','300','-i',('"'+$script+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runPath ($name+'.out.txt')) -RedirectStandardError (Join-Path $runPath ($name+'.err.txt'))
 $children+=@{Name=$name;Process=$process}
}
[pscustomobject]@{RunPath=$runPath;Scenario=$Scenario;StartedUtc=[DateTime]::UtcNow.ToString('o');Processes=@($children|ForEach-Object{@{Name=$_.Name;Id=$_.Process.Id}})} | ConvertTo-Json -Depth 4
 $deadline=[DateTime]::UtcNow.AddSeconds(330)
 while(@($children|Where-Object{-not $_.Process.HasExited}).Count -gt 0){
  if([DateTime]::UtcNow -ge $deadline){throw 'Synthetic workload exceeded its 330-second bound.'}
  Start-Sleep -Milliseconds 500
 }
 foreach($child in $children){$child.Process.WaitForExit()}
 $result=@($children|ForEach-Object{[pscustomobject]@{Scenario=$_.Name;ExitCode=$_.Process.ExitCode;CompletedUtc=[DateTime]::UtcNow.ToString('o')}})
 $result|ConvertTo-Json|Set-Content (Join-Path $runPath 'result.json')
 $result|ConvertTo-Json
 if(@($result|Where-Object{$_.ExitCode -ne 0}).Count -gt 0){throw 'At least one workload process failed; inspect bounded run logs.'}
} finally {
 foreach($child in $children){if(-not $child.Process.HasExited){$child.Process.Kill()};$child.Process.Dispose()}
}
