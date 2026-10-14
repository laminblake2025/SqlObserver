[CmdletBinding()]
param([ValidateRange(1024,65535)][int] $Port = 4322, [switch] $NoBuild, [switch] $Stop)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.5') {
    throw 'Use PowerShell 7.5 or newer.'
}
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskArtifacts = Join-Path $taskRepository '.artifacts/lab-dashboard-20261009'
$taskStatePath = Join-Path $taskArtifacts "process-$Port.json"
$taskAssembly = Join-Path $PSScriptRoot 'LabDashboardHost/bin/Release/net10.0/SqlObserver.LabDashboardHost.dll'
if (Test-Path -LiteralPath $taskStatePath) {
    $taskState = Get-Content -LiteralPath $taskStatePath -Raw | ConvertFrom-Json
    $taskOwnedProcess = Get-Process -Id $taskState.Id -ErrorAction SilentlyContinue
    if ($null -ne $taskOwnedProcess) {
        if ($taskOwnedProcess.Path -ne $taskState.Executable -or
            $taskOwnedProcess.StartTime.ToUniversalTime().Ticks -ne [long]$taskState.StartTicks -or
            $taskState.EntryPoint -ne $taskAssembly) { throw 'Recorded PID belongs to another process; refusing to control it.' }
        if ($Stop) { Stop-Process -Id $taskOwnedProcess.Id; Write-Host 'Lab dashboard stopped; lab services are untouched.'; return }
        Write-Host "Lab dashboard already running at http://localhost:$Port/#/overview?range=1h"
        return
    }
}
if ($Stop) { Write-Host 'Lab dashboard is already stopped.'; return }
$taskProbe = [Net.Sockets.TcpClient]::new()
try {
    try { $taskProbe.Connect('127.0.0.1', $Port) } catch [Net.Sockets.SocketException] { }
    if ($taskProbe.Connected) { throw 'The requested port is occupied. No unrelated process was stopped.' }
} finally { $taskProbe.Dispose() }
Push-Location $taskRepository
try {
    if (-not $NoBuild) {
        & dotnet build tools/LabDashboardHost/LabDashboardHost.csproj --configuration Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Lab dashboard host build failed.' }
        & pnpm --dir web run build
        if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed.' }
    } else {
        & pnpm --dir web run assets:verify
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $taskAssembly)) { throw 'Build and verify the dashboard first.' }
    }
    # Preflight keeps validated TLS and the caller's existing Windows privileges.
    $taskInventory = Invoke-RestMethod -Uri 'https://WIN-QNGOV5GDM24:5443/api/v1/observation-targets/' -UseDefaultCredentials -TimeoutSec 15
    if (@($taskInventory.items | Where-Object instanceKey -ceq 'lab-sql-01').Count -ne 1) { throw 'Expected lab target is unavailable.' }
    [void][IO.Directory]::CreateDirectory($taskArtifacts)
    $taskRuntime = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $taskWeb = Join-Path $taskRepository 'web/dist'
    $taskProcess = Start-Process -FilePath $taskRuntime -ArgumentList @(
        '"'+$taskAssembly+'"', "--LabDashboard:Port=$Port", '"--LabDashboard:WebRoot='+$taskWeb+'"'
    ) -WorkingDirectory $taskRepository -RedirectStandardOutput (Join-Path $taskArtifacts "host-$Port.stdout.log") `
        -RedirectStandardError (Join-Path $taskArtifacts "host-$Port.stderr.log") -WindowStyle Hidden -PassThru
    [pscustomobject]@{ Id=$taskProcess.Id; StartTicks=$taskProcess.StartTime.ToUniversalTime().Ticks; Executable=$taskRuntime; Port=$Port; EntryPoint=$taskAssembly } |
        ConvertTo-Json | Set-Content -LiteralPath $taskStatePath
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $taskProcess.Refresh()
        if ($taskProcess.HasExited) { throw 'Lab dashboard exited; inspect its logs.' }
        try {
            $taskReady = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/__lab/status" -UseDefaultCredentials -AllowUnencryptedAuthentication -NoProxy -TimeoutSec 2
            if ($taskReady.syntheticData -eq $false) {
                Write-Host "Lab dashboard ready: http://localhost:$Port/#/overview?range=1h"
                Write-Host 'Connected to WIN-QNGOV5GDM24 through its authenticated HTTPS API.'
                return
            }
        } catch [Net.Http.HttpRequestException] { }
        catch [Threading.Tasks.TaskCanceledException] { }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $taskDeadline)
    throw 'Lab dashboard did not become ready; inspect its logs.'
} catch {
    if ($null -ne (Get-Variable taskProcess -ValueOnly -ErrorAction SilentlyContinue)) {
        $taskProcess.Refresh()
        if (-not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue }
    }
    throw
} finally { Pop-Location }
