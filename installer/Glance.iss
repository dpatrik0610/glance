; Glance setup (Inno Setup 6). Built by tools\release.ps1:
;   ISCC /DAppVersion=1.2.3 /DBuildDir=<published build> /O<output folder> installer\Glance.iss
; Installs per user (no admin) into %LOCALAPPDATA%\Programs\Glance, which the in-app updater can write to.
; Settings live in %APPDATA%\Glance and are kept on uninstall.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef BuildDir
  #error Pass /DBuildDir=<folder with the published Glance.exe>
#endif

[Setup]
; Never change AppId: it's how Windows ties upgrades and the uninstaller to earlier installs.
AppId={{1B17C6AB-6153-4C8D-94E5-E9F579DF2C34}
AppName=Glance
AppVersion={#AppVersion}
AppVerName=Glance {#AppVersion}
AppPublisher=dpatrik0610
AppPublisherURL=https://github.com/dpatrik0610/glance
AppUpdatesURL=https://github.com/dpatrik0610/glance/releases
DefaultDirName={localappdata}\Programs\Glance
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename=Glance-{#AppVersion}-setup
SetupIconFile=..\src\Glance\Assets\Glance.ico
UninstallDisplayIcon={app}\Glance.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Glance"; Filename: "{app}\Glance.exe"
Name: "{autodesktop}\Glance"; Filename: "{app}\Glance.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Glance.exe"; Description: "{cm:LaunchProgram,Glance}"; Flags: nowait postinstall skipifsilent

[Registry]
; Created by Glance itself (Settings > start with Windows, Explorer's "Open CLI"); removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Glance"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\GlanceCli"; ValueType: none; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GlanceCli"; ValueType: none; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\GlanceCli"; ValueType: none; Flags: uninsdeletekey dontcreatekey

[Code]
// A running Glance locks its files. "Glance.exe --exit" tells the running one to save and quit
// (the same path tools\deploy.ps1 uses); then wait until its single-instance mutex is gone.
procedure CloseGlance(const Exe: String);
var
  Code, I: Integer;
begin
  if not FileExists(Exe) then Exit;
  Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 75 do
  begin
    if not CheckForMutexes('Glance.Instance') then Exit;
    Sleep(200);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseGlance(ExpandConstant('{app}\Glance.exe'));
  if CheckForMutexes('Glance.Instance') then
    Result := 'Glance is still running. Quit it from the tray icon, then click Back and Next.'
  else
    Result := '';
end;

function InitializeUninstall: Boolean;
begin
  CloseGlance(ExpandConstant('{app}\Glance.exe'));
  Result := True;
end;
