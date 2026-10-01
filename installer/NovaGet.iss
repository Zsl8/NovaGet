; NovaGet installer (Inno Setup 6).
; Built by build.ps1:  iscc /DAppVersion=x.y.z installer\NovaGet.iss
; Code signing is enabled only when build.ps1 passes /DSignEnabled together with /Ssigntool=...

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "NovaGet"
#define AppExeName "NovaGet.exe"
#define NativeHostExeName "NovaGet.NativeHost.exe"
#define NativeHostName "com.novaget.nativehost"
#define AppPublisher "NovaGet contributors"
#define AppUrl "https://github.com/Zsl8/NovaGet"
#define AppMutexName "NovaGet.SingleInstance"
#define AppUserModelId "NovaGet.DownloadManager"
#define AppGuid "{8C1B5E7A-3F7D-4C8E-9C2B-6E1F0A4D2B77}"
#define SourceDir "..\out\app"
#define ExtensionDir "..\out\extension"

#include "extension-ids.iss"

[Setup]
AppId={#StringChange(AppGuid, "{", "{{")}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
AppCopyright=Copyright (C) 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
; Per-user install by default (%LOCALAPPDATA%\Programs\NovaGet); the dialog offers "all users" (Program Files).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=NovaGet-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\icons\ico\novaget.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
AppMutex={#AppMutexName}
ChangesAssociations=yes
#ifdef SignEnabled
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.TaskDesktopIcon=Create a &desktop shortcut
english.TaskStartup=&Launch NovaGet at Windows startup
english.TaskBrowsers=&Integrate with installed browsers
english.TaskClipboard=&Monitor clipboard for download links
english.TaskGroupIntegration=Integration:
english.RunLaunch=Launch NovaGet
english.RunExtensionGuide=Open browser extension setup page
english.UninstallRemoveData=Remove your download list and settings too?%n%nChoose No to keep them for a future installation.
english.CloseRunningApp=NovaGet is running. Setup will close it first; downloads in progress are paused and can be resumed later.%n%nClose NovaGet and continue?
english.CloseRunningAppFailed=NovaGet could not be closed. Please exit it from its notification area icon (right-click, Exit), then try again.

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:TaskGroupIntegration}"
Name: "browsers"; Description: "{cm:TaskBrowsers}"; GroupDescription: "{cm:TaskGroupIntegration}"
Name: "clipboard"; Description: "{cm:TaskClipboard}"; GroupDescription: "{cm:TaskGroupIntegration}"; Flags: unchecked

[Files]
; Published app: NovaGet.exe, NovaGet.NativeHost.exe, ffmpeg\, lang\, sounds\, docs\.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ExtensionDir}\chromium\*"; DestDir: "{app}\extension\chromium"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#ExtensionDir}\firefox\*"; DestDir: "{app}\extension\firefox"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\docs\install-extension.html"; DestDir: "{app}\docs"; Flags: ignoreversion

[Dirs]
Name: "{app}\native-host"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; AppUserModelID: "{#AppUserModelId}"; Comment: "Download manager"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Start with Windows (minimized to the tray).
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"" /tray"; Tasks: startup; Flags: uninsdeletevalue

; Native messaging host registration (Brave, Opera and Vivaldi read the Chrome key).
Root: HKA; Subkey: "Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\chrome.json"; Tasks: browsers; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\chrome.json"; Tasks: browsers; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Chromium\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\chrome.json"; Tasks: browsers; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Mozilla\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\firefox.json"; Tasks: browsers; Flags: uninsdeletekey

; novaget:// links -> NovaGet.exe /d "%1"
Root: HKA; Subkey: "Software\Classes\novaget"; ValueType: string; ValueName: ""; ValueData: "URL:NovaGet Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\novaget"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\novaget\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"",0"
Root: HKA; Subkey: "Software\Classes\novaget\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" /d ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:RunLaunch}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\docs\install-extension.html"; Description: "{cm:RunExtensionGuide}"; Tasks: browsers; Flags: shellexec nowait postinstall skipifsilent

[UninstallRun]
; Removes wake-up tasks and other per-user registrations the app created at run time.
Filename: "{app}\{#AppExeName}"; Parameters: "/cleanup"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "NovaGetCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\native-host"
Type: files; Name: "{app}\install-defaults.json"
Type: dirifempty; Name: "{app}\lang"
Type: dirifempty; Name: "{app}"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1';
  NativeHostKeys = 'Software\Google\Chrome|Software\Microsoft\Edge|Software\Chromium|Software\Mozilla';

{ NovaGet.exe of an earlier installation (per-user or all-users), or ''. }
function InstalledExe: String;
var
  Location: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Location) or
     (IsWin64 and RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', Location)) or
     RegQueryStringValue(HKLM32, UninstallKey, 'InstallLocation', Location) then
    Result := AddBackslash(Location) + '{#AppExeName}';
end;

{ Asks a running NovaGet to exit (NovaGet.exe /exit sends "exit" over its pipe) and waits up to 15 s for it.
  Silent runs close it without asking. Returns False when NovaGet keeps running or the user says no. }
function CloseRunningApp(const Exe: String; const Silent: Boolean): Boolean;
var
  Code, Waited: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#AppMutexName}') then
    Exit;
  if not Silent then
    if MsgBox(CustomMessage('CloseRunningApp'), mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := False;
      Exit;
    end;
  if (Exe <> '') and FileExists(Exe) then
    Exec(Exe, '/exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Waited := 0;
  while CheckForMutexes('{#AppMutexName}') and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not CheckForMutexes('{#AppMutexName}');
  if (not Result) and (not Silent) then
    MsgBox(CustomMessage('CloseRunningAppFailed'), mbError, MB_OK);
end;

function InitializeSetup: Boolean;
begin
  Result := CloseRunningApp(InstalledExe, WizardSilent);
end;

function InitializeUninstall: Boolean;
begin
  Result := CloseRunningApp(ExpandConstant('{app}\{#AppExeName}'), UninstallSilent);
end;

{ An upgrade that unticks a task removes what an earlier installation registered for it. }
procedure RemoveUnselectedTaskEntries;
var
  Rest, Base: String;
  P: Integer;
begin
  if not WizardIsTaskSelected('browsers') then
  begin
    Rest := NativeHostKeys + '|';
    while Rest <> '' do
    begin
      P := Pos('|', Rest);
      Base := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
      RegDeleteKeyIncludingSubkeys(HKA, Base + '\NativeMessagingHosts\{#NativeHostName}');
    end;
  end;
  if not WizardIsTaskSelected('startup') then
    RegDeleteValue(HKA, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
end;

{ JSON string escaping that keeps the file pure ASCII (non-ASCII becomes \uXXXX). }
function JsonEscape(const S: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '\' then
      Result := Result + '\\'
    else if C = '"' then
      Result := Result + '\"'
    else if (Ord(C) < 32) or (Ord(C) > 126) then
      Result := Result + '\u' + Format('%.4x', [Ord(C)])
    else
      Result := Result + C;
  end;
end;

function BoolJson(const Value: Boolean): String;
begin
  if Value then
    Result := 'true'
  else
    Result := 'false';
end;

procedure WriteNativeHostManifests;
var
  Dir, HostPath, Json: String;
begin
  Dir := ExpandConstant('{app}\native-host');
  ForceDirectories(Dir);
  HostPath := JsonEscape(ExpandConstant('{app}\{#NativeHostExeName}'));

  Json := '{' + #13#10 +
    '  "name": "{#NativeHostName}",' + #13#10 +
    '  "description": "NovaGet browser integration",' + #13#10 +
    '  "path": "' + HostPath + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_origins": [ "chrome-extension://{#ChromeExtensionId}/" ]' + #13#10 +
    '}' + #13#10;
  SaveStringToFile(Dir + '\chrome.json', Json, False);

  Json := '{' + #13#10 +
    '  "name": "{#NativeHostName}",' + #13#10 +
    '  "description": "NovaGet browser integration",' + #13#10 +
    '  "path": "' + HostPath + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_extensions": [ "{#FirefoxExtensionId}" ]' + #13#10 +
    '}' + #13#10;
  SaveStringToFile(Dir + '\firefox.json', Json, False);
end;

{ First-run defaults picked from the wizard; the app applies them when it has no settings.json yet. }
procedure WriteInstallDefaults;
var
  Json: String;
begin
  Json := '{' + #13#10 +
    '  "launchOnStartup": ' + BoolJson(WizardIsTaskSelected('startup')) + ',' + #13#10 +
    '  "browserIntegration": ' + BoolJson(WizardIsTaskSelected('browsers')) + ',' + #13#10 +
    '  "monitorClipboard": ' + BoolJson(WizardIsTaskSelected('clipboard')) + #13#10 +
    '}' + #13#10;
  SaveStringToFile(ExpandConstant('{app}\install-defaults.json'), Json, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteNativeHostManifests;
    WriteInstallDefaults;
    RemoveUnselectedTaskEntries;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Default answer is No: keep the user's downloads list and settings. Silent uninstalls keep them too. }
    if (not UninstallSilent) and
       (MsgBox(CustomMessage('UninstallRemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
    end;
  end;
end;
