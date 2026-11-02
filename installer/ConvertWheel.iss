; Convert Wheel installer (Inno Setup). Built by tools\build-installer.ps1, which passes:
;   AppVersion, AppDir (published app), RuntimeDir (private .NET runtime), RuntimeVersion, OutputDir.
;
; Installs per user (no administrator prompt). The .NET runtime travels inside the installer but is only
; copied when this PC doesn't already have .NET 10 for desktop apps; the app looks in its "runtime" folder
; first, then for the system-wide runtime.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6C0B6F6E-3E8B-4C55-9F5C-8E2C1D0A7B41}
AppName=Convert Wheel
AppVersion={#AppVersion}
AppVerName=Convert Wheel {#AppVersion}
AppPublisher=Convert Wheel
DefaultDirName={localappdata}\Programs\Convert Wheel
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
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

[Messages]
FinishedLabel=Convert Wheel is installed. Hold Ctrl over any file to convert it; find it in the taskbar's hidden icons.

[Tasks]
Name: "startup"; Description: "Start Convert Wheel when Windows starts"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[InstallDelete]
; A runtime from an earlier install is replaced (or removed when the PC now has its own).
Type: filesandordirs; Name: "{app}\runtime"

[Files]
Source: "{#AppDir}\ConvertWheel.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDir}\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: NeedsRuntime

[Icons]
Name: "{autoprograms}\Convert Wheel"; Filename: "{app}\ConvertWheel.exe"
Name: "{autodesktop}\Convert Wheel"; Filename: "{app}\ConvertWheel.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ConvertWheel"; \
  ValueData: """{app}\ConvertWheel.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\ConvertWheel.exe"; Description: "Open Convert Wheel now"; Flags: postinstall nowait skipifsilent
; Deletes the installer once it has closed (it is still running at this point, so keep trying for a minute).
Filename: "{cmd}"; Parameters: "/c ""for /l %i in (1,1,60) do (ping -n 2 127.0.0.1 >nul & del /f /q ""{srcexe}"" >nul 2>&1 & if not exist ""{srcexe}"" exit)"""; \
  Description: "Delete the installer file ({code:InstallerName})"; Flags: postinstall runhidden nowait skipifsilent

[UninstallDelete]
; Settings, the log, the unpacked video tools and the app's extracted libraries.
Type: filesandordirs; Name: "{localappdata}\ConvertWheel"
Type: filesandordirs; Name: "{userappdata}\ConvertWheel"
Type: filesandordirs; Name: "{%TEMP}\.net\ConvertWheel"
Type: dirifempty; Name: "{app}"

[Code]
const
  AppMutex = 'ConvertWheel.SingleInstance';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  RuntimeNeeded: Boolean;

{ Whether a folder holds a "10.*" version subfolder (e.g. shared\Microsoft.WindowsDesktop.App\10.0.5). }
function HasVersion10(const Folder: String): Boolean;
var
  Rec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Folder) + '10.*', Rec) then
  try
    repeat
      if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
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
  Result := (Root <> '') and DirExists(AddBackslash(Root) + 'host\fxr')
    and HasVersion10(AddBackslash(Root) + 'shared\Microsoft.NETCore.App')
    and HasVersion10(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App');
end;

{ The same places the app itself looks for a system-wide runtime: the registered location, then Program Files. }
function SystemHasRuntime(): Boolean;
var
  Location: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then
    Result := RootHasRuntime(Location);
  if not Result then
    Result := RootHasRuntime(ExpandConstant('{commonpf64}\dotnet'));
end;

function NeedsRuntime(): Boolean;
begin
  Result := RuntimeNeeded;
end;

function InstallerName(Param: String): String;
begin
  Result := ExtractFileName(ExpandConstant('{srcexe}'));
end;

function InitializeSetup(): Boolean;
begin
#ifdef ForceRuntime
  RuntimeNeeded := True; { testing: behave as on a PC without .NET }
#else
  RuntimeNeeded := not SystemHasRuntime();
#endif
  Result := True;
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

{ Closes a running copy: politely first (it finishes and exits), then forcefully if it doesn't. }
procedure CloseRunningApp(const AppFolder: String);
var
  Code, Waited: Integer;
  Exe: String;
begin
  if not CheckForMutexes(AppMutex) then Exit;
  Exe := AddBackslash(AppFolder) + 'ConvertWheel.exe';
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Waited := 0;
  while CheckForMutexes(AppMutex) and (Waited < 8000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  if CheckForMutexes(AppMutex) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ConvertWheel.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp(ExpandConstant('{app}'));
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Unticking "start with Windows" on a reinstall removes an earlier startup entry. }
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('startup') then
    RegDeleteValue(HKCU, RunKey, 'ConvertWheel');
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp(ExpandConstant('{app}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { The app may have added its own startup entry from its settings. }
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, RunKey, 'ConvertWheel');
end;
