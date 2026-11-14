; Inno Setup script for HandyPlayer Windows installer
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)
; Built by publish-win.ps1 (passes the evaluated version, selected portable directory and output run).
; Manual: iscc /DAppVersion=<version> /DSourceDir=<portable-directory> /O<fresh-output-directory> installer-win.iss

#define AppName "HandyPlayer"
#ifndef AppVersion
  #error Pass the version: iscc /DAppVersion=<version> installer-win.iss
#endif
#define AppPublisher "HandyPlayer"
#define AppURL "https://github.com/handyplayer"
#define AppExeName "HandyPlayer.exe"
#ifndef SourceDir
  #error Pass /DSourceDir=<portable-directory> from the selected release run
#endif

[Setup]
AppId={{B7E3A4F1-8C2D-4E5F-9A1B-3D6C8E2F7A94}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=publish
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=src\HandyPlaylistPlayer.App\Assets\app-icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
