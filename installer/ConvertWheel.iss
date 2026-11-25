; Convert Wheel installer (Inno Setup). Built by tools\build-installer.ps1, which passes:
;   AppDir (published app), RuntimeDir (private .NET runtime), RuntimeVersion, RuntimeMinimum, WizardDir, OutputDir,
;   and Sign when the installer is to be signed. Version, publisher and copyright come from the exe, which has
;   them from ConvertWheel.csproj.
;
; Installs per user (no administrator prompt) and downloads nothing. The .NET runtime travels inside the installer
; but is only copied when this PC doesn't already have .NET 10 for desktop apps; the app looks in its "runtime"
; folder first, then for the system-wide runtime.

#ifndef RuntimeMinimum
  #define RuntimeMinimum "10.0.0"
#endif
#define AppExe AddBackslash(AppDir) + "ConvertWheel.exe"
#define AppVersion GetStringFileInfo(AppExe, "ProductVersion")
#define AppFileVersion GetVersionNumbersString(AppExe)
#define AppCompany GetStringFileInfo(AppExe, "CompanyName")
#define AppCopyright GetStringFileInfo(AppExe, "LegalCopyright")
#if AppVersion == "" || AppFileVersion == ""
  #error ConvertWheel.exe has no version
#endif
#define UninstallId "{6C0B6F6E-3E8B-4C55-9F5C-8E2C1D0A7B41}"
#if FileExists(AddBackslash(SourcePath) + "..\LICENSE.txt")
  #define LicensePage "..\LICENSE.txt"
#elif FileExists(AddBackslash(SourcePath) + "..\LICENSE")
  #define LicensePage "..\LICENSE"
#endif

[Setup]
AppId={{#UninstallId}
AppName=Convert Wheel
AppVersion={#AppVersion}
AppVerName=Convert Wheel {#AppVersion}
AppPublisher={#AppCompany}
AppCopyright={#AppCopyright}
VersionInfoVersion={#AppFileVersion}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoProductName=Convert Wheel
VersionInfoCompany={#AppCompany}
VersionInfoCopyright={#AppCopyright}
VersionInfoDescription=Convert Wheel Setup
DefaultDirName={localappdata}\Programs\Convert Wheel
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; 64-bit Windows, including Windows 11 on Arm (which runs x64 programs); Windows 10 from version 1809.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=ConvertWheel-Setup-{#AppVersion}
SetupIconFile=..\Assets\ConvertWheel.ico
WizardImageFile={#WizardDir}\large.bmp,{#WizardDir}\large-2x.bmp
WizardSmallImageFile={#WizardDir}\small.bmp,{#WizardDir}\small-2x.bmp
UninstallDisplayIcon={app}\ConvertWheel.exe
UninstallDisplayName=Convert Wheel
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The running app is closed by our own code (it lives in the tray, so there's no window to ask).
CloseApplications=no
#ifdef LicensePage
; Only when the project has a licence of its own to show.
LicenseFile={#LicensePage}
#endif
#ifdef Sign
SignTool=ConvertWheel
SignedUninstaller=yes
#endif

[Messages]
FinishedLabel=Convert Wheel is installed. Hold Ctrl over any file to convert it; find it in the taskbar's hidden icons.

[Tasks]
Name: "startup"; Description: "Start Convert Wheel when Windows starts"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[InstallDelete]
; Parts of an earlier install that this one replaces (or no longer needs, when the PC now has its own .NET).
Type: filesandordirs; Name: "{app}\runtime"; Check: IsUpdate
Type: filesandordirs; Name: "{app}\licenses"; Check: IsUpdate

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef LicensePage
Source: "{#LicensePage}"; DestDir: "{app}"; Flags: ignoreversion
#endif
Source: "{#RuntimeDir}\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: NeedsRuntime

[Icons]
Name: "{autoprograms}\Convert Wheel"; Filename: "{app}\ConvertWheel.exe"
Name: "{autodesktop}\Convert Wheel"; Filename: "{app}\ConvertWheel.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ConvertWheel.exe"; Description: "Open Convert Wheel now"; Flags: postinstall nowait skipifsilent
; Deletes the installer once it has closed (it is still running at this point, so keep trying for a minute).
Filename: "{cmd}"; Parameters: "/c ""for /l %i in (1,1,60) do (ping -n 2 127.0.0.1 >nul & del /f /q ""{srcexe}"" >nul 2>&1 & if not exist ""{srcexe}"" exit)"""; \
  Description: "Delete the installer file ({code:InstallerName})"; Flags: postinstall runhidden nowait skipifsilent unchecked; Check: CanDeleteInstaller

[UninstallDelete]
; What the app made for itself: the unpacked video tools, the log, its notes about unfinished conversions and its
; extracted libraries. Anything else in these folders is left alone. Settings are asked about (see below).
Type: filesandordirs; Name: "{localappdata}\ConvertWheel\tools\ffmpeg-*"
Type: dirifempty; Name: "{localappdata}\ConvertWheel\tools"
Type: filesandordirs; Name: "{localappdata}\ConvertWheel\working"
Type: files; Name: "{localappdata}\ConvertWheel\log.txt"
Type: dirifempty; Name: "{localappdata}\ConvertWheel"
Type: filesandordirs; Name: "{%TEMP|{localappdata}\Temp}\.net\ConvertWheel"
Type: dirifempty; Name: "{app}"

[Code]
const
  AppMutex = 'ConvertWheel.SingleInstance';
  QuitEvent = 'Local\ConvertWheel.Quit';
  OldQuitEvent = 'ConvertWheel.Exit'; { what version 1.0.0 listened to }
  EVENT_MODIFY_STATE = 2;
  CloseTimeout = 20000;
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#UninstallId}_is1';

var
  RuntimeNeeded, StartupTaskSet, KeepStartup, RemoveSettings: Boolean;

function OpenEvent(Access: DWORD; Inherit: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ ------------------------------------------------------------------ .NET }

{ "10.0.5" into its numbers; False for anything else, so previews ("10.0.0-rc.1.25451.107") don't count. }
function ParseVersion(const Text: String; var Major, Minor, Patch: Integer): Boolean;
var
  Rest: String;
  First, Second, I: Integer;
begin
  Result := False;
  if Length(Text) > 20 then Exit;
  for I := 1 to Length(Text) do
    if (Text[I] <> '.') and ((Text[I] < '0') or (Text[I] > '9')) then Exit;
  First := Pos('.', Text);
  if First = 0 then Exit;
  Rest := Copy(Text, First + 1, Length(Text));
  Second := Pos('.', Rest);
  if Second = 0 then Exit;
  Major := StrToIntDef(Copy(Text, 1, First - 1), -1);
  Minor := StrToIntDef(Copy(Rest, 1, Second - 1), -1);
  Patch := StrToIntDef(Copy(Rest, Second + 1, Length(Rest)), -1);
  Result := (Major >= 0) and (Minor >= 0) and (Patch >= 0);
end;

{ Whether the app can use this version: the same major version, and not older than the one it was built for. The
  host that finds the frameworks (NewerToo) may also have a newer major version. }
function IsUsable(const Text: String; NewerToo: Boolean): Boolean;
var
  Major, Minor, Patch, MinMajor, MinMinor, MinPatch: Integer;
begin
  Result := False;
  if not ParseVersion('{#RuntimeMinimum}', MinMajor, MinMinor, MinPatch) then Exit;
  if not ParseVersion(Text, Major, Minor, Patch) then Exit;
  if Major <> MinMajor then
    Result := NewerToo and (Major > MinMajor)
  else if Minor <> MinMinor then
    Result := Minor > MinMinor
  else
    Result := Patch >= MinPatch;
end;

{ Whether a folder has a usable version with the given file in it (so half-removed ones don't count). }
function HasVersion(const Folder, NeededFile: String; NewerToo: Boolean): Boolean;
var
  Rec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Folder) + '*', Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and IsUsable(Rec.Name, NewerToo)
        and FileExists(AddBackslash(Folder) + Rec.Name + '\' + NeededFile) then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function RootHasRuntime(const Root: String): Boolean;
begin
  Result := (Root <> '')
    and HasVersion(AddBackslash(Root) + 'host\fxr', 'hostfxr.dll', True)
    and HasVersion(AddBackslash(Root) + 'shared\Microsoft.NETCore.App', 'hostpolicy.dll', False)
    and HasVersion(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App', 'PresentationFramework.dll', False);
end;

{ The place the app itself looks for a system-wide x64 runtime: the registered location (always in the 32-bit part of
  the registry), or if there's none, Program Files\dotnet. On Arm64 PCs that holds the Arm64 runtime, which this app
  can't use; the x64 one is in its "x64" folder. }
function SystemHasRuntime(): Boolean;
var
  Location: String;
begin
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) and (Location <> '') then
    Result := RootHasRuntime(Location)
  else if IsArm64 then
    Result := RootHasRuntime(ExpandConstant('{commonpf64}\dotnet\x64'))
  else
    Result := RootHasRuntime(ExpandConstant('{commonpf64}\dotnet'));
end;

function NeedsRuntime(): Boolean;
begin
  Result := RuntimeNeeded;
end;

{ ------------------------------------------------------------------ closing the running app }

procedure SignalEvent(const Name: String);
var
  Handle: THandle;
begin
  Handle := OpenEvent(EVENT_MODIFY_STATE, False, Name);
  if Handle <> 0 then
  begin
    SetEvent(Handle);
    CloseHandle(Handle);
  end;
end;

{ Counts the programs running from exactly this file, and ends them if asked to. Copies of Convert Wheel that run
  from elsewhere aren't touched. }
function RunningFrom(const Exe: String; Terminate: Boolean): Integer;
var
  Locator, Service, Processes, Process: Variant;
  Path: String;
  I: Integer;
begin
  Result := 0;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\cimv2');
    Processes := Service.ExecQuery('SELECT * FROM Win32_Process WHERE Name = "ConvertWheel.exe"');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      if not VarIsNull(Process.ExecutablePath) then
      begin
        Path := Process.ExecutablePath;
        if CompareText(Path, Exe) = 0 then
        begin
          Result := Result + 1;
          if Terminate then Process.Terminate(1);
        end;
      end;
    end;
  except
    { Without Windows Management, a file that's still in use shows up when Setup replaces it. }
    Log('Looking for running copies failed: ' + GetExceptionMessage);
  end;
end;

function StillRunning(const Exe: String): Boolean;
begin
  Result := CheckForMutexes(AppMutex) or (RunningFrom(Exe, False) > 0);
end;

{ Closes a running copy: asks it to stop its conversions and exit, waits, and only then ends what still runs from
  this folder. False if something still does. }
function CloseRunningApp(const AppFolder: String): Boolean;
var
  Exe: String;
  Waited: Integer;
begin
  Result := True;
  Exe := AddBackslash(AppFolder) + 'ConvertWheel.exe';
  if not StillRunning(Exe) then Exit;
  SignalEvent(QuitEvent);
  SignalEvent(OldQuitEvent);
  Waited := 0;
  while StillRunning(Exe) and (Waited < CloseTimeout) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  if RunningFrom(Exe, False) = 0 then Exit;
  if SuppressibleMsgBox('Convert Wheel is still busy and hasn''t closed. Close it anyway? A conversion that is still running would be stopped.',
    mbConfirmation, MB_YESNO, IDYES) = IDYES then
  begin
    RunningFrom(Exe, True);
    Waited := 0;
    while (RunningFrom(Exe, False) > 0) and (Waited < 5000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;
  Result := RunningFrom(Exe, False) = 0;
end;

{ ------------------------------------------------------------------ installing }

function IsUpdate(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\ConvertWheel.exe')) and FileExists(ExpandConstant('{app}\unins000.exe'));
end;

function HasTasksParameter(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if (Pos('/TASKS=', Uppercase(ParamStr(I))) = 1) or (Pos('/MERGETASKS=', Uppercase(ParamStr(I))) = 1) then
      Result := True;
end;

function InstallerName(Param: String): String;
begin
  Result := ExtractFileName(ExpandConstant('{srcexe}'));
end;

{ The command that deletes the installer runs in cmd.exe, where these mean something in a path. }
function CanDeleteInstaller(): Boolean;
var
  Exe, Special: String;
  I: Integer;
begin
  Result := True;
  Exe := ExpandConstant('{srcexe}');
  Special := '%^&!()';
  for I := 1 to Length(Special) do
    if Pos(Special[I], Exe) > 0 then Result := False;
end;

{ An older version than the installed one: possible, but not by accident. }
function ConfirmDowngrade(): Boolean;
var
  Installed: String;
  OldVersion, NewVersion: Int64;
begin
  Result := True;
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Installed) then Exit;
  if not StrToVersion(Installed, OldVersion) or not StrToVersion('{#AppVersion}', NewVersion) then Exit;
  if ComparePackedVersion(OldVersion, NewVersion) > 0 then
    Result := SuppressibleMsgBox('Convert Wheel ' + Installed + ' is installed, which is newer than this one ({#AppVersion}). Replace it with the older version?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;

function InitializeSetup(): Boolean;
begin
#ifdef ForceRuntime
  RuntimeNeeded := True; { testing: behave as on a PC without .NET }
#else
  RuntimeNeeded := not SystemHasRuntime();
#endif
  Result := ConfirmDowngrade();
end;

{ On an update the startup box shows what's really set: it may have been changed in the app's settings since. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpSelectTasks) or StartupTaskSet then Exit;
  StartupTaskSet := True;
  if not IsUpdate() or HasTasksParameter() then Exit;
  if RegValueExists(HKCU, RunKey, 'ConvertWheel') then
    WizardSelectTasks('startup')
  else
    WizardSelectTasks('!startup');
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Convert Wheel will be installed to:' + NewLine + Space + ExpandConstant('{app}') + NewLine + NewLine;
  if RuntimeNeeded then
    Result := Result + 'This PC doesn''t have the .NET runtime Convert Wheel needs, so a private copy is included' + NewLine +
      Space + '(inside Convert Wheel''s folder; nothing else on the PC changes).' + NewLine + NewLine
  else
    Result := Result + 'This PC already has .NET, so it won''t be installed again.' + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { A silent update leaves the startup entry as it is, unless told otherwise. }
  KeepStartup := WizardSilent and IsUpdate() and not HasTasksParameter();
  if CloseRunningApp(ExpandConstant('{app}')) then
    Result := ''
  else
    Result := 'Convert Wheel couldn''t be closed, so it can''t be replaced. Exit it from its icon in the taskbar''s hidden icons, then try again.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep <> ssPostInstall) or KeepStartup then Exit;
  if WizardIsTaskSelected('startup') then
    RegWriteStringValue(HKCU, RunKey, 'ConvertWheel', '"' + ExpandConstant('{app}\ConvertWheel.exe') + '" --background')
  else
    RegDeleteValue(HKCU, RunKey, 'ConvertWheel');
end;

{ ------------------------------------------------------------------ uninstalling }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value, Settings: String;
begin
  Settings := ExpandConstant('{userappdata}\ConvertWheel');

  { The first step after "are you sure". }
  if CurUninstallStep = usAppMutexCheck then
    if not CloseRunningApp(ExpandConstant('{app}')) then
    begin
      SuppressibleMsgBox('Convert Wheel couldn''t be closed, so it can''t be removed. Exit it from its icon in the taskbar''s hidden icons, then try again.',
        mbError, MB_OK, IDOK);
      Abort;
    end;

  if (CurUninstallStep = usUninstall) and DirExists(Settings) then
    RemoveSettings := SuppressibleMsgBox('Remove your Convert Wheel settings too? Keep them if you might install it again.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;

  if CurUninstallStep = usPostUninstall then
  begin
    if RemoveSettings then DelTree(Settings, True, True, True);
    { The installer or the app's settings added this. One that starts a copy somewhere else stays. }
    if RegQueryStringValue(HKCU, RunKey, 'ConvertWheel', Value) then
      if Pos(Lowercase(ExpandConstant('{app}\ConvertWheel.exe')), Lowercase(Value)) > 0 then
      begin
        RegDeleteValue(HKCU, RunKey, 'ConvertWheel');
        RegDeleteValue(HKCU, ApprovedKey, 'ConvertWheel');
      end;
  end;
end;
