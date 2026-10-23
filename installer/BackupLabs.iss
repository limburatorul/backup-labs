; Inno Setup script for Backup Labs, modelled on File Labs'. Build it through build.ps1, which
; publishes the app first and passes the version in.

#define AppName "Backup Labs"
#define AppExe "BackupLabs.exe"
#define AppPublisher "Protagonist Labs"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it is what ties an update to the installation it replaces.
AppId={{3B9E6A12-8C4D-4F7B-A1E5-6D2F0B8C9E34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per machine since 1.6.0, in the studio's common folder (vault: Ecosistem-v3-foldere.md): installing and every
; update ask for UAC. Start with Windows stays per user (HKCU), written by the app. The code at the end moves the
; data and replaces the old per-user installation.
PrivilegesRequired=admin
DefaultDirName={autopf}\ProtagonistLabs\{#AppName}
; the user's data and the old per-user install are handled on purpose, for the user who runs setup
UsedUserAreasWarning=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; .bkl backups get the app's icon and open in it; Explorer is told to redraw them.
ChangesAssociations=yes

CloseApplications=force
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=BackupLabs-{#AppVersion}-setup
SetupIconFile=..\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Backups (.bkl) open in Backup Labs, on its Restore panel, for every user like the install (HKA = HKLM here; the
; old per-user keys are removed in the code below, or they would win over these). --open, not a bare path: a bare
; path means "back this up" (File Labs' handoff).
Root: HKA; Subkey: "Software\Classes\.bkl"; ValueType: string; ValueName: ""; ValueData: "BackupLabs.Backup"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\BackupLabs.Backup"; ValueType: string; ValueName: ""; ValueData: "Backup Labs backup"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\BackupLabs.Backup\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\BackupLabs.Backup\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --open ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; an update from inside the app is silent: start the new version again, as the user, not elevated
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]
const
  OldKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{3B9E6A12-8C4D-4F7B-A1E5-6D2F0B8C9E34}_is1';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ The app writes its own "Start with Windows" entry; don't leave it pointing at a deleted exe. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'Backup Labs');
end;

{ Setup cannot replace files the running app holds open, and the app hides in the tray instead of
  closing, so stop it first. The exe name is specific enough to kill only this app. }
procedure StopApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

function IsEmptyDir(Dir: String): Boolean;
var
  F: TFindRec;
begin
  Result := True;
  if FindFirst(Dir + '\*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') then begin Result := False; Exit; end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

{ The data folder, %APPDATA%\BackupLabs -> %APPDATA%\ProtagonistLabs\Backup Labs: the same rule as the app's
  Ecosystem.DataFolder, which runs at every start too. A rename, never a copy; a new folder that already holds
  something is never mixed with the old one; a rename that fails leaves both as they are (the app retries). }
procedure MoveData();
var
  Old, New: String;
begin
  Old := ExpandConstant('{userappdata}\BackupLabs');
  New := ExpandConstant('{userappdata}\ProtagonistLabs\Backup Labs');
  if not DirExists(Old) then Exit;
  if DirExists(New) then
  begin
    if not IsEmptyDir(New) then Exit;
    RemoveDir(New);
  end;
  ForceDirectories(ExpandConstant('{userappdata}\ProtagonistLabs'));
  if not RenameFile(Old, New) then
    Log('Backup Labs data not moved (the app will try again at its start): ' + Old);
end;

{ Up to 1.5.0 Backup Labs installed per user, in %LOCALAPPDATA%\Programs\Backup Labs, with the same AppId in HKCU.
  Its data is moved first (above); then its program folder, shortcuts, uninstall entry and .bkl keys go. Its own
  uninstaller is not run: it would delete the Start with Windows entry, which is repointed below instead. }
procedure RemoveOldInstall();
var
  Dir, Value: String;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, OldKey, 'InstallLocation', Dir) then
  begin
    Dir := RemoveBackslashUnlessRoot(Dir);
    { only a folder that really is the old Backup Labs, never wherever a damaged key might point }
    if (CompareText(Dir, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0) and FileExists(Dir + '\{#AppExe}') then
      DelTree(Dir, True, True, True);
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, OldKey);
  end;
  DelTree(ExpandConstant('{userprograms}\{#AppName}'), True, True, True);
  DeleteFile(ExpandConstant('{userdesktop}\{#AppName}.lnk'));
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\BackupLabs.Backup');
  if RegQueryStringValue(HKEY_CURRENT_USER, 'Software\Classes\.bkl', '', Value) and (Value = 'BackupLabs.Backup') then
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\.bkl');
  { Start with Windows, when it was on: the new exe, so the next sign-in starts it even before the app runs again }
  if RegValueExists(HKEY_CURRENT_USER, RunKey, 'Backup Labs') then
    RegWriteStringValue(HKEY_CURRENT_USER, RunKey, 'Backup Labs', '"' + ExpandConstant('{app}\{#AppExe}') + '" --tray');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  MoveData();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RemoveOldInstall();
end;

function InitializeUninstall(): Boolean;
begin
  StopApp();
  Result := True;
end;
