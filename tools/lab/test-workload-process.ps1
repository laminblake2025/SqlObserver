[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Workload.Process.psm1') -Force
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($code in @(0, 7)) {
    $outPath = Join-Path $OutputDirectory "exit-$code.out.txt"
    $errPath = Join-Path $OutputDirectory "exit-$code.err.txt"
    $command = "[Console]::Out.WriteLine('stdout marker'); [Console]::Error.WriteLine('stderr marker'); exit $code"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $child = Start-LoggedWorkloadProcess $shell "-NoProfile -EncodedCommand $encoded" $outPath $errPath
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not $child.Process.HasExited) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Process test timed out.' }
            Start-Sleep -Milliseconds 20
        }
        $actual = Complete-LoggedWorkloadProcess $child
        if ($null -eq $actual -or $actual -ne $code) { throw "Exit code mismatch: expected $code, received $actual" }
    } finally { Close-LoggedWorkloadProcess $child }
    if ([IO.File]::ReadAllText($outPath).Trim() -ne 'stdout marker') { throw 'Standard output was not drained.' }
    if ([IO.File]::ReadAllText($errPath).Trim() -ne 'stderr marker') { throw 'Standard error was not drained.' }
}
'PASS: polled child processes preserve exit codes 0 and 7 and drain both output streams.'
