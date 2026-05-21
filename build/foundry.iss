; Foundry — Inno Setup script (unsigned dev installer, PRD §11/§15 Phase 6).
; Build:  iscc build\foundry.iss   (requires Inno Setup 6)
; Expects build\publish (dotnet publish output) and sidecar\dist\foundry-cad (PyInstaller bundle).

#define AppName "Foundry"
#define AppVersion "0.4.1"
#define AppPublisher "Foundry"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\Foundry.exe
OutputDir=AppPackages
OutputBaseFilename=FoundrySetup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern

[Files]
; WPF app (from: dotnet publish Foundry.App -c Release -r win-x64 -o build\publish)
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs
; Frozen Python CAD sidecar (from: pyinstaller build\sidecar.spec)
Source: "..\sidecar\dist\foundry-cad\*"; DestDir: "{app}\sidecar"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Foundry.exe"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\Foundry.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
Filename: "{app}\Foundry.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
