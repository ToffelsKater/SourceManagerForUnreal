; Source Manager for Unreal Engine — Inno Setup script
; Per-user install (no admin needed), Start Menu + optional desktop shortcut,
; uninstall via Apps & Features, in-place upgrades.
;
; AppId must never change — it is how newer installers upgrade older installs.
; Build with:  .\build-installer.ps1   (repo root)

#define MyAppName "Source Manager for Unreal Engine"
#define MyAppVersion "1.3.0"
#define MyAppExeName "UnrealManager.exe"

[Setup]
AppId={{38F0FE20-F4A0-4ED6-86C3-283F875ED9B0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Source Manager Community Tool
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
; Per-user by default (installs under %LocalAppData%\Programs, no UAC).
; The dialog lets someone with admin rights choose an all-users install instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=SourceManagerSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\Assets\app.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
