[CmdletBinding()]
param([string] $IsccPath = "$env:ProgramFiles\Inno Setup 7\ISCC.exe")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo ('artifacts/prerequisite-tests/' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($output)
$source = Get-Content (Join-Path $repo 'installer/test/test-setup.iss') -Raw
$start = $source.IndexOf('function RuntimeOutputHasFramework(')
$end = $source.IndexOf('function HasPowerShell:')
if ($start -lt 0 -or $end -le $start) { throw 'Runtime detector functions not found.' }
$functions = $source.Substring($start, $end - $start)
$dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
$runtimeLines = if (Test-Path $dotnet) {
    $lines = & $dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw 'Local runtime inventory failed.' }
    $lines
} else { @() }
$checks = foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
    $expected = @($runtimeLines | Where-Object {
        $_ -match ('^' + [regex]::Escape($framework) + ' 10\.0\.(\d+) \[') -and [int]$Matches[1] -ge 13
    }).Count -gt 0
    "  if HasRuntime('$framework') <> $($expected.ToString()) then RaiseException('Native host result mismatch: $framework');"
}
$harness = @'
[Setup]
AppName=Prerequisite detector tests
AppVersion=1
DefaultDirName={tmp}\UnusedPrerequisiteTests
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
OutputBaseFilename=prerequisite-tests
[Code]
__FUNCTIONS__
procedure Check(Line: String; Expected: Boolean);
var Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Line;
  if RuntimeOutputHasFramework('Microsoft.NETCore.App', Lines) <> Expected then
    RaiseException('Unexpected runtime match: ' + Line);
end;
function InitializeSetup: Boolean;
var Lines: TArrayOfString;
begin
  Check('Microsoft.NETCore.App 10.0.13 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]', True);
  Check('Microsoft.NETCore.App 10.0.14 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]', True);
  Check('Microsoft.NETCore.App 10.0.12 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]', False);
  Check('Microsoft.NETCore.App 10.0.13-preview.1 [C:\dotnet]', False);
  Check('Microsoft.NETCore.App 11.0.13 [C:\dotnet]', False);
  Check('Microsoft.NETCore.App 10.1.13 [C:\dotnet]', False);
  Check('Microsoft.AspNetCore.App 10.0.13 [C:\dotnet]', False);
  Check('Microsoft.NETCore.App 10.0.invalid [C:\dotnet]', False);
  Check('Microsoft.NETCore.App 10.0.13', False);
  Check('', False);
  SetArrayLength(Lines, 3);
  Lines[0] := 'Microsoft.AspNetCore.App 10.0.13 [C:\dotnet]';
  Lines[1] := 'Microsoft.NETCore.App 10.0.13 [C:\dotnet]';
  Lines[2] := 'Microsoft.NETCore.App 10.0.7 [C:\dotnet]';
  if not RuntimeOutputHasFramework('Microsoft.AspNetCore.App', Lines) then RaiseException('ASP.NET match failed');
  if not RuntimeOutputHasFramework('Microsoft.NETCore.App', Lines) then RaiseException('Side-by-side match failed');
__CHECKS__
  if not SaveStringToFile(ExpandConstant('{src}\passed.txt'), 'passed', False) then RaiseException('Cannot save test result');
  // End during initialization: no installation or prerequisite execution occurs.
  Result := False;
end;
'@
$harness = $harness.Replace('__FUNCTIONS__', $functions).Replace('__CHECKS__', ($checks -join "`n"))
$scriptPath = Join-Path $output 'prerequisite-tests.iss'
[IO.File]::WriteAllText($scriptPath, $harness)
& $IsccPath /Qp "/O$output" $scriptPath
if ($LASTEXITCODE -ne 0) { throw 'Prerequisite harness compilation failed.' }
$process = Start-Process -FilePath (Join-Path $output 'prerequisite-tests.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', "/LOG=`"$output/test.log`"") -WindowStyle Hidden -PassThru -Wait
if (!(Test-Path (Join-Path $output 'passed.txt'))) { throw "Prerequisite regression failed (exit $($process.ExitCode)); inspect $output/test.log" }
Write-Host "Prerequisite parser and native x64 host tests passed: $output"
