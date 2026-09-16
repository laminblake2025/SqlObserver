Set-StrictMode -Version Latest

function Start-LoggedWorkloadProcess([string]$FilePath, [string]$Arguments, [string]$OutputPath, [string]$ErrorPath) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $FilePath
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $output = $null
    $errors = $null
    $started = $false
    try {
        $output = [IO.File]::Create($OutputPath)
        $errors = [IO.File]::Create($ErrorPath)
        # Process.Start retains ownership of the process handle on Windows
        # PowerShell, including after exit. Start-Process -PassThru can lose
        # the exit code when the caller polls HasExited before WaitForExit.
        [void]$process.Start()
        $started = $true
        return [pscustomobject]@{
            Process = $process
            Output = $output
            Errors = $errors
            OutputCopy = $process.StandardOutput.BaseStream.CopyToAsync($output)
            ErrorCopy = $process.StandardError.BaseStream.CopyToAsync($errors)
        }
    } catch {
        if ($started -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($output) { $output.Dispose() }
        if ($errors) { $errors.Dispose() }
        $process.Dispose()
        throw
    }
}

function Complete-LoggedWorkloadProcess($Child) {
    if (-not $Child.Process.HasExited) { throw 'Workload process is still running.' }
    $Child.Process.WaitForExit()
    [void]$Child.OutputCopy.GetAwaiter().GetResult()
    [void]$Child.ErrorCopy.GetAwaiter().GetResult()
    $Child.Output.Flush()
    $Child.Errors.Flush()
    return $Child.Process.ExitCode
}

function Close-LoggedWorkloadProcess($Child) {
    try {
        if (-not $Child.Process.HasExited) { $Child.Process.Kill() }
        $Child.Process.WaitForExit()
        [void]$Child.OutputCopy.GetAwaiter().GetResult()
        [void]$Child.ErrorCopy.GetAwaiter().GetResult()
    } finally {
        $Child.Output.Dispose()
        $Child.Errors.Dispose()
        $Child.Process.Dispose()
    }
}

Export-ModuleMember -Function Start-LoggedWorkloadProcess, Complete-LoggedWorkloadProcess, Close-LoggedWorkloadProcess
