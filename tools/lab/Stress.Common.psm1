Set-StrictMode -Version Latest
$script:LabHost = 'WIN-QNGOV5GDM24'
$script:Target = 'dfb2b72c-4ad4-4765-9523-528c798482f0'
$script:Root = 'C:\SqlObserverLab\Workloads\Stress'
$script:Api = "https://win-qngov5gdm24:5443/api/v1/observation-targets/$script:Target"

function Assert-StressHost {
    if ($env:COMPUTERNAME -ne $script:LabHost) { throw 'Stress harness is restricted to WIN-QNGOV5GDM24.' }
}
function Get-StressSettings { @{ HostName=$script:LabHost; Target=$script:Target; Root=$script:Root; Api=$script:Api } }
function Get-StressRunPath([string]$RunId) {
    if ($RunId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid stress run identity.' }
    $path = [IO.Path]::GetFullPath((Join-Path $script:Root $RunId))
    if (-not $path.StartsWith($script:Root + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Run path escapes workload directory.' }
    return $path
}
function Write-StressJson([string]$Path, $Value) {
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 24),[Text.UTF8Encoding]::new($false))
    # Same-volume atomic replacement; readers never see partially written manifests.
    if ([IO.File]::Exists($Path)) { $backup="$temporary.bak"; [IO.File]::Replace($temporary,$Path,$backup); [IO.File]::Delete($backup) }
    else { [IO.File]::Move($temporary,$Path) }
}
function Read-StressJson([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function ConvertTo-StressUtc($Value) { ([DateTimeOffset]$Value).UtcDateTime }
function Get-StressControlFailure([DateTime]$Now,[DateTime]$Deadline,[DateTime]$Heartbeat,[bool]$ControllerOwned,[bool]$StopRequested) {
    if(-not $ControllerOwned){return 'controller_exited'}
    if($Now -ge $Deadline){return 'overall_deadline'}
    if(($Now-$Heartbeat).TotalSeconds -gt 45){return 'controller_stalled'}
    if($StopRequested){return 'operator_stop'}
    return $null
}
function Get-StressGuardDecision($Sample, [int]$CpuHighSamples, [int]$BadProbes) {
    if ($Sample.AvailableMemoryBytes -lt [Math]::Max([double]1GB,[double]$Sample.TotalMemoryBytes * .15)) { return 'memory_limit' }
    foreach ($disk in $Sample.Disks) { if ($disk.FreeBytes -lt [Math]::Max([double]5GB,[double]$disk.TotalBytes * .15)) { return 'disk_limit' } }
    if ($CpuHighSamples -ge 6) { return 'sustained_cpu_limit' }
    if ($BadProbes -ge 3) { return 'observer_probe_limit' }
    return $null
}
function Test-StressProcessIdentity($Expected, $Actual) {
    return $null -ne $Actual -and [int]$Actual.Id -eq [int]$Expected.Id -and
        $Actual.StartTime.ToUniversalTime().ToString('o') -eq $Expected.StartedUtc -and
        [string]::Equals($Actual.Path,$Expected.Path,[StringComparison]::OrdinalIgnoreCase)
}
function Stop-StressOwnedProcess($Expected) {
    $actual = Get-Process -Id $Expected.Id -ErrorAction SilentlyContinue
    if (Test-StressProcessIdentity $Expected $actual) { Stop-Process -Id $actual.Id -Force -ErrorAction Stop }
}
function Invoke-StressApi([string]$Path, [string]$Method='GET', $Body=$null) {
    $uri = if ($Path.StartsWith('/api/')) { 'https://win-qngov5gdm24:5443' + $Path } else { $script:Api + $Path }
    $args = @{ Uri=$uri; Method=$Method; UseDefaultCredentials=$true; TimeoutSec=5; ErrorAction='Stop' }
    if ($null -ne $Body) {
        $args.ContentType='application/json'; $args.Body=$Body | ConvertTo-Json -Depth 12 -Compress
        $args.Headers=@{ 'Idempotency-Key'=$Body.operationId; Origin='https://win-qngov5gdm24:5443' }
    }
    Invoke-RestMethod @args
}
function Invoke-StressSql([string]$Query, [string]$Database='master', [string]$Application='SqlObserver.Stress.Preflight', [int]$Timeout=10) {
    $connection = New-Object System.Data.SqlClient.SqlConnection
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source']='tcp:WIN-QNGOV5GDM24,1433'; $builder['Initial Catalog']=$Database
    $builder['Integrated Security']=$true; $builder['Encrypt']=$true; $builder['TrustServerCertificate']=$false
    $builder['Connect Timeout']=5; $builder['Application Name']=$Application; $builder['Pooling']=$false
    $connection.ConnectionString=$builder.ConnectionString
    try {
        $connection.Open(); $command=$connection.CreateCommand(); $command.CommandText=$Query; $command.CommandTimeout=$Timeout
        $table=New-Object Data.DataTable; $reader=$command.ExecuteReader()
        try { $table.Load($reader) } finally { $reader.Dispose(); $command.Dispose() }
        return ,$table
    } finally { $connection.Dispose() }
}
function Invoke-StressRepository([string]$Query, [string]$CredentialPath) {
    # Machine protection plus the restricted file ACL works from SSH without an
    # interactive logon/master-key cache. Keep legacy operator files readable.
    if($CredentialPath.EndsWith('.dpapi.json',[StringComparison]::OrdinalIgnoreCase)){
        if((Get-Item -LiteralPath $CredentialPath).Length -gt 65536){throw 'Protected credential exceeds its bound.'}
        $record=Read-StressJson $CredentialPath
        if($record.schemaVersion -ne 1 -or $record.protection -ne 'WindowsDpapiLocalMachine' -or $record.operatorSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Protected credential identity mismatch.'}
        Add-Type -AssemblyName System.Security
        $bytes=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($record.ciphertext),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
        try{$secret=ConvertTo-SecureString ([Text.Encoding]::Unicode.GetString($bytes)) -AsPlainText -Force;$credential=[Management.Automation.PSCredential]::new($record.userName,$secret)}
        finally{[Array]::Clear($bytes,0,$bytes.Length)}
    }else{$credential = Import-Clixml -LiteralPath $CredentialPath}
    if ($credential -isnot [Management.Automation.PSCredential] -or $credential.UserName -ne 'sqlobserver_bootstrap') { throw 'Invalid repository evidence credential.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName='C:\Program Files\PostgreSQL\18\bin\psql.exe'
    $start.Arguments='-X -q -A -t -w -h 127.0.0.1 -U sqlobserver_bootstrap -d sqlobserver -v ON_ERROR_STOP=1'
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.EnvironmentVariables['PGPASSWORD']=$credential.GetNetworkCredential().Password
    $start.EnvironmentVariables['PGCONNECT_TIMEOUT']='5'
    $process=New-Object Diagnostics.Process; $process.StartInfo=$start
    try {
        [void]$process.Start(); $start.EnvironmentVariables.Remove('PGPASSWORD')
        $output=$process.StandardOutput.ReadToEndAsync(); $errors=$process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine("BEGIN READ ONLY; SET LOCAL statement_timeout='5s'; SET LOCAL ROLE sqlobserver_migrator; SET LOCAL sqlobserver.target_scope='$script:Target'; SELECT coalesce(json_agg(q),'[]'::json) FROM ($Query) q; ROLLBACK;")
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Repository evidence timed out.' }
        if ($process.ExitCode -ne 0) { throw 'Repository evidence query failed; provider text omitted.' }
        return ($output.GetAwaiter().GetResult() | ConvertFrom-Json)
    } finally { $process.Dispose(); $credential.Password.Dispose() }
}
function Get-StressSample {
    $os=Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 3 -ErrorAction Stop
    $cpu=(Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -OperationTimeoutSec 3 -ErrorAction Stop).PercentProcessorTime
    $disks=@(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -OperationTimeoutSec 3 -ErrorAction Stop | ForEach-Object { @{ Name=$_.DeviceID; FreeBytes=[long]$_.FreeSpace; TotalBytes=[long]$_.Size } })
    $watch=[Diagnostics.Stopwatch]::StartNew(); $ok=$false; $page=$null
    try { $page=Invoke-StressApi '/activity/live'; $ok=$true } catch { }
    $watch.Stop()
    @{ Utc=[DateTime]::UtcNow.ToString('o'); CpuPercent=[double]$cpu; TotalMemoryBytes=[long]$os.TotalVisibleMemorySize*1KB
       AvailableMemoryBytes=[long]$os.FreePhysicalMemory*1KB; Disks=$disks; ProbeOk=$ok; ProbeMs=$watch.Elapsed.TotalMilliseconds
       LiveAgeSeconds=if($page -and $page.observedUtc){([DateTime]::UtcNow-(ConvertTo-StressUtc $page.observedUtc)).TotalSeconds}else{$null}
       SnapshotId=if($page){$page.snapshotId}else{$null} }
}
function Get-StressPercentile([double[]]$Values, [double]$Percentile=.95) {
    if ($Values.Count -eq 0) { return $null }
    $sorted=@($Values | Sort-Object); $sorted[[Math]::Max(0,[Math]::Ceiling($sorted.Count*$Percentile)-1)]
}
function Write-StressReport([string]$RunPath) {
    $manifest=Read-StressJson (Join-Path $RunPath 'manifest.json')
    $cleanup=Read-StressJson (Join-Path $RunPath 'cleanup-result.json')
    $samples=@(); $guardPath=Join-Path $RunPath 'guardrails.jsonl'
    if(Test-Path $guardPath){$samples=@(Get-Content $guardPath|ForEach-Object{$_|ConvertFrom-Json})}
    $observations=@($samples|Where-Object SnapshotId|Group-Object SnapshotId|ForEach-Object{
        $first=$_.Group[0]; (ConvertTo-StressUtc $first.Utc).AddSeconds(-[double]$first.LiveAgeSeconds)
    }|Sort-Object)
    $gaps=@();for($i=1;$i -lt $observations.Count;$i++){$gaps+=($observations[$i]-$observations[$i-1]).TotalSeconds}
    $workers=@(Get-ChildItem $RunPath -Filter 'worker-*.json'|ForEach-Object{
        $w=Read-StressJson $_.FullName
        @{Worker=$w.Worker;Scenario=$w.Scenario;Database=$w.Database;Status=$w.Status;Batches=$w.Batches;ColdStartMs=if($w.PSObject.Properties['ColdStartMs']){$w.ColdStartMs}else{$null}}
    })
    # A force-stopped bounded worker may not execute PowerShell's finally block.
    # Keep its manifest identity visible instead of silently omitting it from the
    # report or inventing completed batch counts.
    $missingWorkers=@()
    if($manifest.PSObject.Properties['Workers']) {
        foreach($expected in $manifest.Workers) {
            if($expected.PSObject.Properties['Number'] -and @($workers|Where-Object Worker -eq $expected.Number).Count -eq 0) {
                $missingWorkers+=@{Worker=$expected.Number;Scenario=$expected.Scenario;Database=$null;Status='no_final_worker_record';Batches=$null;ColdStartMs=$null}
            }
        }
        $workers+=$missingWorkers
    }
    $summary=@{RunId=$manifest.RunId;Profile=if($manifest.PSObject.Properties['Profile']){$manifest.Profile}else{'Full'};Status=$manifest.Status;StartedUtc=$manifest.StartedUtc;CompletedUtc=$manifest.CompletedUtc;
        Samples=$samples.Count;P50LiveMs=(Get-StressPercentile @($samples|ForEach-Object ProbeMs) .50);
        P95LiveMs=(Get-StressPercentile @($samples|ForEach-Object ProbeMs));P99LiveMs=(Get-StressPercentile @($samples|ForEach-Object ProbeMs) .99);
        MaximumLiveAgeSeconds=($samples|Measure-Object LiveAgeSeconds -Maximum).Maximum;
        MaximumObservedCollectionGapSeconds=($gaps|Measure-Object -Maximum).Maximum;
        FailedProbes=@($samples|Where-Object {-not $_.ProbeOk}).Count;
        MaximumCpuPercent=($samples|Measure-Object CpuPercent -Maximum).Maximum;
        MinimumAvailableMemoryBytes=($samples|Measure-Object AvailableMemoryBytes -Minimum).Minimum;
        Workers=$workers;MissingFinalWorkerRecords=$missingWorkers.Count;Scenarios=$manifest.Results;Cleanup=$cleanup}
    Write-StressJson (Join-Path $RunPath 'results.json') $summary
    $lines=@('# SQL Observer controlled stress result','',"Run: $($manifest.RunId)","Status: $($manifest.Status)",
        "Profile: $($summary.Profile). Functional and Alerts profiles are focused retests, not full load profiles.","UTC: $($manifest.StartedUtc) to $($manifest.CompletedUtc)",'',
        "Live API latency (ms): P50 $($summary.P50LiveMs), P95 $($summary.P95LiveMs), P99 $($summary.P99LiveMs).",
        "Maximum live age: $($summary.MaximumLiveAgeSeconds) seconds; maximum observed collection gap: $($summary.MaximumObservedCollectionGapSeconds) seconds.",
        "Failed probes: $($summary.FailedProbes) of $($summary.Samples). Peak CPU: $($summary.MaximumCpuPercent)%. Minimum available memory: $($summary.MinimumAvailableMemoryBytes) bytes.",'',
        '| Scenario | Status | Evidence |','|---|---|---|')
    foreach($result in $manifest.Results){
        $evidence=($result.Evidence|ConvertTo-Json -Depth 12 -Compress).Replace('|','&#124;')
        $lines+="| $($result.Name) | $($result.Status) | $evidence |"
    }
    $lines+=@('',"Cleanup passed: $($cleanup.Passed). Remaining owned SQL sessions: $($cleanup.RemainingSessions).",'',
        'Worker outcomes, load achieved, cold viewer startup times, delivery identities and measurements are retained in results.json.',
        "Workers without a final record: $($missingWorkers.Count). Bounded forced cancellation may prevent a final record; completed batch counts for these workers remain unavailable.",
        'Collection gaps are inferred from sampled snapshot identities; raw guardrails remain available.',
        'Single shared lab VM; this is not ten-server capacity or release certification.')
    [IO.File]::WriteAllLines((Join-Path $RunPath 'report.md'),$lines)
}
Export-ModuleMember -Function *-Stress*
