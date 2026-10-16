#ifndef PayloadRoot
  #error PayloadRoot is required
#endif
#include BuildPins

[Setup]
AppId={{49159A72-DDE6-47C6-9639-C5E01F77BC21}
AppName=SQL Observer Unsigned Test
AppVersion=0.1.0
AppVerName=SQL Observer Unsigned Test ({#SourceCommitShort})
DefaultDirName={autopf}\SqlObserver Test
DefaultGroupName=SQL Observer Test
OutputDir={#InstallerOutput}
OutputBaseFilename=SqlObserver-TestSetup-win-x64-{#SourceCommitShort}
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.20348
PrivilegesRequired=admin
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=yes
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes
SetupLogging=yes
UninstallDisplayName=SQL Observer Unsigned Test
UninstallDisplayIcon={app}\Setup\SqlObserver.TestSetup.exe
CloseApplications=no
RestartApplications=no
DirExistsWarning=yes
UsePreviousAppDir=no
AllowNetworkDrive=no
AllowUNCPath=no
InfoBeforeFile={#SetupGuide}

[Tasks]
Name: postgres; Description: "Install PostgreSQL 18 if it is missing (interactive vendor wizard)"; Flags: checkedonce

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{app}\Application"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#HelperRoot}\*"; DestDir: "{app}\Setup"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SetupGuide}"; DestDir: "{app}"; DestName: "TEST-INSTALL.txt"; Flags: ignoreversion
Source: "{#PrerequisiteRoot}\{#DotnetFile}"; Flags: dontcopy
Source: "{#PrerequisiteRoot}\{#AspnetFile}"; Flags: dontcopy
Source: "{#PrerequisiteRoot}\{#PowerShellFile}"; Flags: dontcopy
Source: "{#PrerequisiteRoot}\{#PostgresFile}"; Flags: dontcopy

[Icons]
Name: "{group}\Configure test services"; Filename: "{app}\Setup\SqlObserver.TestSetup.exe"
Name: "{group}\Deployment instructions"; Filename: "{app}\TEST-INSTALL.txt"
Name: "{group}\Uninstall SQL Observer Test"; Filename: "{uninstallexe}"

[Code]
var ConfigurationExitCode: Integer;

function GetBinaryType(FileName: String; var BinaryType: LongWord): Boolean;
  external 'GetBinaryTypeW@kernel32.dll stdcall';

function Is64BitExecutable(FileName: String): Boolean;
var BinaryType: LongWord;
begin
  Result := GetBinaryType(FileName, BinaryType) and (BinaryType = 6);
  if not Result then Log('Executable is not a usable 64-bit binary: ' + FileName);
end;

function ParseStableVersion(Version: String; var Major, Minor, Patch: Integer): Boolean;
var I, Dot: Integer; Remainder: String;
begin
  Result := False;
  Major := -1; Minor := -1; Patch := -1;
  for I := 1 to Length(Version) do
    if ((Version[I] < '0') or (Version[I] > '9')) and (Version[I] <> '.') then exit;
  Dot := Pos('.', Version);
  if Dot = 0 then exit;
  Major := StrToIntDef(Copy(Version, 1, Dot - 1), -1);
  Remainder := Copy(Version, Dot + 1, Length(Version));
  Dot := Pos('.', Remainder);
  if Dot = 0 then exit;
  Minor := StrToIntDef(Copy(Remainder, 1, Dot - 1), -1);
  Patch := StrToIntDef(Copy(Remainder, Dot + 1, Length(Remainder)), -1);
  Result := (Major >= 0) and (Minor >= 0) and (Patch >= 0);
end;

function RuntimeOutputHasFramework(Framework: String; Lines: TArrayOfString): Boolean;
var I, Separator, Major, Minor, Patch: Integer; Line, Version, Prefix: String;
    HasBaseMinor, BaseMinorReady, HigherMinorReady: Boolean;
begin
  Result := False;
  HasBaseMinor := False; BaseMinorReady := False; HigherMinorReady := False;
  Prefix := Framework + ' ';
  for I := 0 to GetArrayLength(Lines) - 1 do begin
    Line := Trim(Lines[I]);
    if Copy(Line, 1, Length(Prefix)) = Prefix then begin
      Version := Copy(Line, Length(Prefix) + 1, Length(Line));
      Separator := Pos(' ', Version);
      if Separator > 0 then begin
        Version := Copy(Version, 1, Separator - 1);
        Log('Detected x64 ' + Framework + ' ' + Version);
        if ParseStableVersion(Version, Major, Minor, Patch) and (Major = 10) then begin
          if Minor = 0 then begin
            HasBaseMinor := True;
            if Patch >= 13 then BaseMinorReady := True;
          end else HigherMinorReady := True;
        end;
      end;
    end;
  end;
  // Default .NET Minor roll-forward prefers 10.0 when it exists.
  Result := BaseMinorReady or (not HasBaseMinor and HigherMinorReady);
end;

function DotnetHost: String;
var Root: String;
begin
  // The .NET apphost uses the x64 InstallLocation in the 32-bit registry view.
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) and (Root <> '') then
    Result := AddBackslash(Root) + 'dotnet.exe'
  else Result := ExpandConstant('{pf64}\dotnet\dotnet.exe');
end;

function ReadRuntimeInventory(var Lines: TArrayOfString): Boolean;
var Output: TExecOutput; ExitCode: Integer; Host: String;
begin
  Result := False;
  // Use the same machine-wide x64 host as the installed services, never PATH.
  // Valid runtime installations need not expose the HKLM64 sharedfx keys.
  Host := DotnetHost;
  Log('Checking x64 runtime inventory using ' + Host);
  if not FileExists(Host) then begin
    Log('Runtime check failed: x64 dotnet host is missing.'); exit;
  end;
  if not Is64BitExecutable(Host) then exit;
  try
    if not ExecAndCaptureOutput(Host, '--list-runtimes', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode, Output) then begin
      Log('Runtime check could not launch the x64 dotnet host.'); exit;
    end;
  except
    Log('Runtime check could not capture dotnet output.'); exit;
  end;
  Log('Runtime probe exit code: ' + IntToStr(ExitCode));
  if (ExitCode <> 0) or Output.Error then exit;
  Lines := Output.StdOut;
  Result := True;
end;

function HasRuntime(Framework: String): Boolean;
var Lines: TArrayOfString;
begin
  Result := False;
  if not ReadRuntimeInventory(Lines) then exit;
  Result := RuntimeOutputHasFramework(Framework, Lines);
  if Result then Log('Runtime check passed: ' + Framework)
  else Log('Runtime check failed: ' + Framework + ' requires stable 10.0.13+ or a newer 10.x minor. Other majors can coexist but do not replace .NET 10.');
end;

function RuntimeOutputHasCompatibleFrameworks(Lines: TArrayOfString): Boolean;
var I, Major, Minor, Patch, AspnetMinor, Separator: Integer; Version, Prefix: String;
begin
  Result := False;
  AspnetMinor := 2147483647;
  Prefix := 'Microsoft.AspNetCore.App ';
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Copy(Lines[I], 1, Length(Prefix)) = Prefix then begin
      Version := Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I]));
      Separator := Pos(' ', Version);
      if Separator > 0 then
        if ParseStableVersion(Copy(Version, 1, Separator - 1), Major, Minor, Patch) then
          if (Major = 10) and (Minor < AspnetMinor) then AspnetMinor := Minor;
    end;
  Prefix := 'Microsoft.NETCore.App ';
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Copy(Lines[I], 1, Length(Prefix)) = Prefix then begin
      Version := Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I]));
      Separator := Pos(' ', Version);
      if Separator > 0 then
        if ParseStableVersion(Copy(Version, 1, Separator - 1), Major, Minor, Patch) then
          if (Major = 10) and (Minor >= AspnetMinor) then Result := True;
    end;
end;

function HasCompatibleRuntimePair: Boolean;
var Lines: TArrayOfString;
begin
  Result := False;
  if ReadRuntimeInventory(Lines) then Result := RuntimeOutputHasCompatibleFrameworks(Lines);
  if Result then Log('.NET and ASP.NET Core minor versions are compatible.')
  else Log('A compatible .NET/ASP.NET Core 10.x pair is missing. ASP.NET Core needs a .NET runtime at least as new as its selected minor.');
end;

function PowerShellVersionSupported(Version: String): Boolean;
var Major, Minor, Patch: Integer;
begin
  Result := False;
  if ParseStableVersion(Version, Major, Minor, Patch) then
    Result := (Major > 7) or ((Major = 7) and (Minor >= 5));
end;

function HasPowerShellAt(Host: String): Boolean;
var Output: TExecOutput; ExitCode: Integer; Version: String;
begin
  Result := False;
  Log('Checking PowerShell using ' + Host);
  if not FileExists(Host) then begin
    Log('PowerShell prerequisite not detected: executable is missing.'); exit;
  end;
  if not Is64BitExecutable(Host) then exit;
  try
    if not ExecAndCaptureOutput(Host, '-NoLogo -NoProfile -NonInteractive -Command "if (-not [Environment]::Is64BitProcess) { exit 1 }; $PSVersionTable.PSVersion.ToString()"', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode, Output) then begin
      Log('PowerShell prerequisite probe could not launch.'); exit;
    end;
  except
    Log('PowerShell prerequisite probe could not capture output.'); exit;
  end;
  Log('PowerShell probe exit code: ' + IntToStr(ExitCode));
  if (ExitCode <> 0) or Output.Error or (GetArrayLength(Output.StdOut) <> 1) then exit;
  Version := Trim(Output.StdOut[0]);
  Log('Detected x64 PowerShell ' + Version);
  Result := PowerShellVersionSupported(Version);
  if Result then Log('PowerShell prerequisite detected.')
  else Log('PowerShell prerequisite not detected: stable 7.5+ is required.');
end;

function HasPowerShellIn(Root: String): Boolean;
var Entry: TFindRec;
begin
  // Include future stable major-version folders, never user-local PATH entries.
  Root := AddBackslash(Root);
  Result := HasPowerShellAt(Root + '7\pwsh.exe');
  if Result then exit;
  if FindFirst(Root + '*', Entry) then begin
    try
      repeat
        if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
           (Entry.Name <> '.') and (Entry.Name <> '..') and (Entry.Name <> '7') then begin
          Result := HasPowerShellAt(Root + Entry.Name + '\pwsh.exe');
          if Result then exit;
        end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
  end;
end;

function HasRegisteredPowerShell(View: Integer): Boolean;
var Names: TArrayOfString; I: Integer; Root: String;
begin
  Result := False;
  // Enumerate registered installations instead of assuming an MSI GUID.
  if RegGetSubkeyNames(View, 'SOFTWARE\Microsoft\PowerShellCore\InstalledVersions', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(View, 'SOFTWARE\Microsoft\PowerShellCore\InstalledVersions\' + Names[I], 'InstallLocation', Root) and (Root <> '') then
        if HasPowerShellAt(AddBackslash(Root) + 'pwsh.exe') then begin Result := True; exit; end;
end;

function HasPowerShell: Boolean;
begin
  Result := HasPowerShellIn(ExpandConstant('{pf64}\PowerShell'));
  if not Result then Result := HasRegisteredPowerShell(HKLM64);
  if not Result then Result := HasRegisteredPowerShell(HKLM32);
end;

function PostgresVersionSupported(Line: String): Boolean;
var Version: String; Separator, Major, Minor, Patch: Integer;
begin
  Result := False;
  if Copy(Line, 1, 22) <> 'postgres (PostgreSQL) ' then exit;
  Version := Trim(Copy(Line, 23, Length(Line)));
  Separator := Pos(' ', Version);
  if Separator > 0 then Version := Copy(Version, 1, Separator - 1);
  // PostgreSQL 10+ uses major.update, not major.minor.patch.
  if ParseStableVersion(Version + '.0', Major, Minor, Patch) then Result := Major = 18;
end;

function HasPostgresAt(Root: String): Boolean;
var Output: TExecOutput; ExitCode: Integer; Host: String;
begin
  Result := False;
  Host := AddBackslash(Root) + 'bin\postgres.exe';
  Log('Checking PostgreSQL server using ' + Host);
  if not FileExists(Host) then begin Log('PostgreSQL server executable is missing.'); exit; end;
  if not Is64BitExecutable(Host) then exit;
  try
    if not ExecAndCaptureOutput(Host, '--version', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode, Output) then begin
      Log('PostgreSQL server version probe could not launch.'); exit;
    end;
  except Log('PostgreSQL server version probe could not capture output.'); exit; end;
  Log('PostgreSQL server probe exit code: ' + IntToStr(ExitCode));
  if (ExitCode <> 0) or Output.Error or (GetArrayLength(Output.StdOut) <> 1) then exit;
  Log('PostgreSQL server reports: ' + Trim(Output.StdOut[0]));
  Result := PostgresVersionSupported(Trim(Output.StdOut[0]));
  if not Result then Log('This application requires PostgreSQL 18.x; other majors are not modified.');
end;

function HasRegisteredPostgres(View: Integer): Boolean;
var Names: TArrayOfString; I: Integer; Root: String;
begin
  Result := False;
  if RegGetSubkeyNames(View, 'SOFTWARE\PostgreSQL\Installations', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(View, 'SOFTWARE\PostgreSQL\Installations\' + Names[I], 'Base Directory', Root) then
        if HasPostgresAt(Root) then begin Result := True; exit; end;
end;

function HasPostgresIn(Root: String): Boolean;
var Entry: TFindRec;
begin
  Result := False;
  Root := AddBackslash(Root);
  if FindFirst(Root + '*', Entry) then begin
    try
      repeat
        if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (Entry.Name <> '.') and (Entry.Name <> '..') then
          Result := HasPostgresAt(Root + Entry.Name);
      until Result or not FindNext(Entry);
    finally FindClose(Entry); end;
  end;
end;

function HasPostgres18: Boolean;
begin
  Result := HasRegisteredPostgres(HKLM64);
  if not Result then Result := HasRegisteredPostgres(HKLM32);
  if not Result then Result := HasPostgresIn(ExpandConstant('{pf64}\PostgreSQL'));
  if Result then Log('PostgreSQL 18 prerequisite detected.')
  else Log('PostgreSQL 18 prerequisite not detected.');
end;

function ServerProductSupported(ProductType: String): Boolean;
begin
  Result := (ProductType = 'ServerNT') or (ProductType = 'LanmanNT');
end;

function InitializeSetup: Boolean;
var ProductType: String;
begin
  ConfigurationExitCode := 1;
  Result := not (RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\SqlObserverTestServer') or
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\SqlObserverTestCollector'));
  if not Result then MsgBox('Test services already exist. This installer supports fresh installs. Uninstall the existing test package first; its repository, configuration and keys will be preserved.', mbError, MB_OK);
  if Result then begin
    Result := RegQueryStringValue(HKLM64, 'SYSTEM\CurrentControlSet\Control\ProductOptions', 'ProductType', ProductType) and ServerProductSupported(ProductType);
    Log('Windows product type: ' + ProductType);
    if not Result then MsgBox('Use an isolated Windows Server 2022 or later x64 test host.', mbError, MB_OK);
  end;
  if Result and WizardSilent then begin
    Result := False;
    MsgBox('This unsigned test installer requires interactive configuration and hidden password prompts. Silent installation is not supported.', mbError, MB_OK);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then begin
    Result := (Pos(Lowercase(ExpandConstant('{autopf}') + '\'), Lowercase(AddBackslash(WizardDirValue))) = 1) and not DirExists(WizardDirValue);
    if not Result then MsgBox('Choose a fresh directory beneath Program Files. Existing installations are never overwritten.', mbError, MB_OK);
  end;
end;

function RunPrerequisite(FileName, Hash, Arguments: String; Msi: Boolean; var NeedsRestart: Boolean): String;
var ExitCode: Integer; Executable, Parameters: String;
begin
  Result := '';
  ExtractTemporaryFile(FileName);
  if CompareText(GetSHA256OfFile(ExpandConstant('{tmp}\') + FileName), Hash) <> 0 then begin
    Result := 'Bundled prerequisite checksum mismatch.'; exit;
  end;
  Executable := ExpandConstant('{tmp}\') + FileName;
  Parameters := Arguments;
  if Msi then begin
    Executable := ExpandConstant('{sys}\msiexec.exe');
    Parameters := '/i "' + ExpandConstant('{tmp}\') + FileName + '" ' + Arguments;
  end;
  if not Exec(Executable, Parameters, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
    Result := 'Could not launch prerequisite: ' + FileName
  else if (ExitCode <> 0) and (ExitCode <> 3010) then Result := 'Prerequisite failed or was cancelled: ' + FileName
  else if ExitCode = 3010 then begin
    NeedsRestart := True;
    Result := 'Restart Windows to finish prerequisite installation, then run this setup again.';
  end;
  Log('Prerequisite ' + FileName + ' returned code ' + IntToStr(ExitCode));
  if Result <> '' then Log(Result);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var RuntimeReady, AspnetReady, PowerShellReady, PostgresReady: Boolean;
begin
  Result := '';
  if (CompareText(RemoveBackslashUnlessRoot(WizardDirValue), ExpandConstant('{autopf}\SqlObserver Test')) <> 0) or DirExists(WizardDirValue) then begin
    Result := 'This fresh-install test package uses a new Program Files\SqlObserver Test directory. Run the installed configuration tool or uninstall the existing test package first.';
    exit;
  end;
  // Always record every result before starting prerequisite installers.
  RuntimeReady := HasRuntime('Microsoft.NETCore.App');
  AspnetReady := HasRuntime('Microsoft.AspNetCore.App');
  // If only a newer ASP.NET minor is present without its matching .NET minor,
  // installing the bundled ASP.NET 10.0 restores a compatible runtime pair.
  if AspnetReady then
    if not HasCompatibleRuntimePair then AspnetReady := False;
  PowerShellReady := HasPowerShell;
  PostgresReady := HasPostgres18;
  WizardForm.StatusLabel.Caption := 'Installing verified prerequisites...';
  if not RuntimeReady then Result := RunPrerequisite('{#DotnetFile}', '{#DotnetSha256}', '/install /quiet /norestart', False, NeedsRestart);
  if Result <> '' then exit;
  if not AspnetReady then Result := RunPrerequisite('{#AspnetFile}', '{#AspnetSha256}', '/install /quiet /norestart', False, NeedsRestart);
  if Result <> '' then exit;
  if not PowerShellReady then Result := RunPrerequisite('{#PowerShellFile}', '{#PowerShellSha256}', '/qn /norestart ADD_PATH=1 ENABLE_PSREMOTING=0 REGISTER_MANIFEST=1', True, NeedsRestart);
  if Result <> '' then exit;
  if not PostgresReady then begin
    if not WizardIsTaskSelected('postgres') then Result := 'PostgreSQL 18 is required. Select its installation task or install it separately before continuing.'
    else Result := RunPrerequisite('{#PostgresFile}', '{#PostgresSha256}', '', False, NeedsRestart);
  end;
  if Result <> '' then exit;
  if not HasRuntime('Microsoft.NETCore.App') then
    Result := Result + ' .NET x64 runtime 10.0.13+ or newer 10.x minor.';
  if not HasRuntime('Microsoft.AspNetCore.App') then
    Result := Result + ' ASP.NET Core x64 runtime 10.0.13+ or newer 10.x minor.';
  if not HasCompatibleRuntimePair then Result := Result + ' Compatible .NET/ASP.NET Core 10.x runtime pair.';
  if not HasPowerShell then Result := Result + ' PowerShell 7.5+.';
  if not HasPostgres18 then Result := Result + ' PostgreSQL 18.';
  if Result <> '' then begin
    Result := 'Prerequisite detection did not pass. Missing or undetectable:' + Result + ' See the setup log for detection results.';
    Log(Result);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    WizardForm.StatusLabel.Caption := 'Complete the secure configuration window to create the test services.';
    if not Exec(ExpandConstant('{app}\Setup\SqlObserver.TestSetup.exe'), '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ConfigurationExitCode) then ConfigurationExitCode := 1;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (ConfigurationExitCode <> 0) then
    WizardForm.FinishedLabel.Caption := 'Application files were installed, but service configuration did not complete. The application is NOT ready. Read TEST-INSTALL.txt and run Configure test services from the Start menu after resolving the failure. Data from a failed attempt is preserved; use a fresh database/configuration directory on retry.';
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := ConfigurationExitCode;
end;

function InitializeUninstall: Boolean;
var ExitCode: Integer;
begin
  Result := Exec(ExpandConstant('{app}\Setup\SqlObserver.TestSetup.exe'), '--remove-services', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := Result and (ExitCode = 0);
  if not Result then MsgBox('Test service removal failed. Application files and all data have been preserved. Inspect service ownership/status before retrying.', mbError, MB_OK);
end;
