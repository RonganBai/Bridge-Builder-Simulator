; Inno Setup script: packages the Godot export in ..\export into a Windows installer.
; Build: ISCC.exe installer\BridgeArchitect3D.iss  (output: export\installer\)

#define AppName "Bridge Architect 3D"
#define AppVersion "1.0.0"

[Setup]
AppId={{6C1B7E5A-3F2D-4B8E-9A61-2D7C4F0B9E13}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher="RonganBai"
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\export\installer
OutputBaseFilename=BridgeArchitect3D-Setup-{#AppVersion}
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\BridgeArchitect3D.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\export\BridgeArchitect3D.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\export\BridgeArchitect3D.pck"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\export\data_BridgeArchitect3D_windows_x86_64\*"; DestDir: "{app}\data_BridgeArchitect3D_windows_x86_64"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\BridgeArchitect3D.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\BridgeArchitect3D.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BridgeArchitect3D.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
