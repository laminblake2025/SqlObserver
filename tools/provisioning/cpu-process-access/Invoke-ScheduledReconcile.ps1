#requires -Version 5.1
# Fixed task entry point. No arguments, IPC, caller-selected paths, or command text.
# A script-level call is required: Windows PowerShell 5.1 -File cannot pass an
# explicit false value to a switch parameter such as -Confirm.
$ErrorActionPreference = 'Stop'
if ($args.Count -ne 0) { throw 'This scheduled entry point accepts no arguments.' }
& (Join-Path $PSScriptRoot 'CpuProcessAccess.ps1') -Action Reconcile -Apply -Confirm:$false
if (-not $?) { exit 1 }
