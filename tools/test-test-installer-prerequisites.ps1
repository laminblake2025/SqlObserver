[CmdletBinding()]
param([string] $IsccPath = "$env:ProgramFiles\Inno Setup 7\ISCC.exe")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo ('artifacts/prerequisite-tests/' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($output)
$source = Get-Content (Join-Path $repo 'installer/test/test-setup.iss') -Raw
$start = $source.IndexOf('function GetBinaryType(')
$end = $source.IndexOf('function InitializeSetup:')
if ($start -lt 0 -or $end -le $start) { throw 'Runtime detector functions not found.' }
$functions = $source.Substring($start, $end - $start)
$dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
$dotnetRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry32)
try {
    $installation = $dotnetRegistry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
    if ($null -ne $installation) {
        try {
            $registeredRoot = $installation.GetValue('InstallLocation')
            if ($registeredRoot) { $dotnet = Join-Path $registeredRoot 'dotnet.exe' }
        } finally { $installation.Dispose() }
    }
} finally { $dotnetRegistry.Dispose() }
$runtimeLines = if (Test-Path $dotnet) {
    $lines = & $dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw 'Local runtime inventory failed.' }
    $lines
} else { @() }
$checks = foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
    $hasBaseMinor = @($runtimeLines | Where-Object { $_ -match ('^' + [regex]::Escape($framework) + ' 10\.0\.\d+ \[') }).Count -gt 0
    $hasBasePatch = @($runtimeLines | Where-Object { $_ -match ('^' + [regex]::Escape($framework) + ' 10\.0\.(\d+) \[') -and [int]$Matches[1] -ge 13 }).Count -gt 0
    $hasHigherMinor = @($runtimeLines | Where-Object {
        $_ -match ('^' + [regex]::Escape($framework) + ' 10\.(\d+)\.(\d+) \[') -and ([int]$Matches[1] -gt 0 -or [int]$Matches[2] -ge 13)
    }).Count -gt 0
    $expected = $hasBasePatch -or (!$hasBaseMinor -and $hasHigherMinor)
    "  if HasRuntime('$framework') <> $($expected.ToString()) then RaiseException('Native host result mismatch: $framework');"
}
$testPowerShell = (Join-Path $PSHOME 'pwsh.exe').Replace("'", "''")
$checks += "  if not HasPowerShellAt('$testPowerShell') then RaiseException('Native PowerShell probe failed');"
$checks += "  if HasPowerShellAt(ExpandConstant('{src}\missing-pwsh.exe')) then RaiseException('Missing PowerShell accepted');"
# Simulated version probes exercise directory discovery and subprocess failures
# without installing products or writing machine registry keys.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
function New-ProbeFixture([string] $RelativePath, [string] $Version, [int] $ExitCode = 0, [string] $Platform = 'x64') {
    $target = Join-Path $output $RelativePath
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    $fixtureSource = Join-Path $output 'probe.cs'
    # Keep fixture text literal: all versions below are fixed test inputs.
    [IO.File]::WriteAllText($fixtureSource, ('class Probe { static int Main() { System.Console.WriteLine("' + $Version + '"); return ' + $ExitCode + '; } }'))
    & $compiler /nologo /target:exe "/platform:$Platform" "/out:$target" $fixtureSource
    if ($LASTEXITCODE -ne 0) { throw 'Version probe fixture compilation failed.' }
}
New-ProbeFixture 'PowerShell/7/pwsh.exe' '7.4.9'
New-ProbeFixture 'PowerShell/8/pwsh.exe' '8.2.0'
New-ProbeFixture 'PowerShellPreview/8-preview/pwsh.exe' '8.0.0-preview.1'
New-ProbeFixture 'PostgreSQL/new-update/bin/postgres.exe' 'postgres (PostgreSQL) 18.99'
New-ProbeFixture 'PostgreSQLWrong/19/bin/postgres.exe' 'postgres (PostgreSQL) 19.1'
New-ProbeFixture 'FailedPostgres/bin/postgres.exe' 'postgres (PostgreSQL) 18.99' 1
New-ProbeFixture 'PostgreSQLx86/bin/postgres.exe' 'postgres (PostgreSQL) 18.99' 0 'x86'
New-ProbeFixture 'PowerShellx86/pwsh.exe' '8.0.0' 0 'x86'
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
  if not ServerProductSupported('ServerNT') then RaiseException('Windows Server rejected');
  if not ServerProductSupported('LanmanNT') then RaiseException('Server product type rejected');
  if ServerProductSupported('WinNT') then RaiseException('Windows client accepted');
  if ServerProductSupported('') then RaiseException('Unknown Windows product accepted');
  if not PowerShellVersionSupported('7.6.7') then RaiseException('PowerShell 7.6.7 rejected');
  if not PowerShellVersionSupported('7.5.0') then RaiseException('PowerShell minimum rejected');
  if not PowerShellVersionSupported('8.0.0') then RaiseException('Newer PowerShell rejected');
  if PowerShellVersionSupported('7.4.9') then RaiseException('Old PowerShell accepted');
  if PowerShellVersionSupported('7.6.7-preview.1') then RaiseException('PowerShell preview accepted');
  if PowerShellVersionSupported('7.6') then RaiseException('Incomplete PowerShell version accepted');
  if PowerShellVersionSupported('7.6.bad') then RaiseException('Invalid PowerShell version accepted');
  if not HasPowerShellIn(ExpandConstant('{src}\PowerShell')) then RaiseException('Future PowerShell major folder missed');
  if HasPowerShellIn(ExpandConstant('{src}\PowerShellPreview')) then RaiseException('Preview folder accepted');
  if not HasPostgresIn(ExpandConstant('{src}\PostgreSQL')) then RaiseException('PostgreSQL without registry missed');
  if HasPostgresIn(ExpandConstant('{src}\PostgreSQLWrong')) then RaiseException('Unsupported PostgreSQL major accepted');
  if HasPostgresAt(ExpandConstant('{src}\FailedPostgres')) then RaiseException('Failed PostgreSQL probe accepted');
  if HasPostgresAt(ExpandConstant('{src}\MissingPostgres')) then RaiseException('Missing PostgreSQL accepted');
  if HasPostgresAt(ExpandConstant('{src}\PostgreSQLx86')) then RaiseException('x86 PostgreSQL accepted');
  if HasPowerShellAt(ExpandConstant('{src}\PowerShellx86\pwsh.exe')) then RaiseException('x86 PowerShell accepted');
  if not PostgresVersionSupported('postgres (PostgreSQL) 18.6') then RaiseException('Bundled PostgreSQL rejected');
  if not PostgresVersionSupported('postgres (PostgreSQL) 18.12') then RaiseException('Newer PostgreSQL update rejected');
  if PostgresVersionSupported('postgres (PostgreSQL) 18beta1') then RaiseException('Preview PostgreSQL accepted');
  if PostgresVersionSupported('psql (PostgreSQL) 18.6') then RaiseException('Client-only PostgreSQL accepted');
  Check('Microsoft.NETCore.App 10.0.13 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]', True);
  Check('Microsoft.NETCore.App 10.0.14 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]', True);
  Check('Microsoft.NETCore.App 10.0.12 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]', False);
  Check('Microsoft.NETCore.App 10.0.13-preview.1 [C:\dotnet]', False);
  Check('Microsoft.NETCore.App 11.0.13 [C:\dotnet]', False);
  Check('Microsoft.NETCore.App 10.1.0 [C:\dotnet]', True);
  Check('Microsoft.NETCore.App 10.0.99 [C:\dotnet]', True);
  Check('Microsoft.NETCore.App 10.0.0x13 [C:\dotnet]', False);
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
  Lines[1] := 'Microsoft.NETCore.App 10.1.0 [C:\dotnet]';
  if RuntimeOutputHasFramework('Microsoft.NETCore.App', Lines) then RaiseException('Old preferred runtime masked by newer minor');
  if not RuntimeOutputHasCompatibleFrameworks(Lines) then RaiseException('Compatible minor pair rejected');
  SetArrayLength(Lines, 2);
  Lines[0] := 'Microsoft.AspNetCore.App 10.1.0 [C:\dotnet]';
  Lines[1] := 'Microsoft.NETCore.App 10.0.13 [C:\dotnet]';
  if RuntimeOutputHasCompatibleFrameworks(Lines) then RaiseException('ASP.NET missing its matching newer runtime accepted');
  Lines[1] := 'Microsoft.NETCore.App 10.1.0 [C:\dotnet]';
  if not RuntimeOutputHasCompatibleFrameworks(Lines) then RaiseException('Future compatible runtime pair rejected');
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
