; Foundry — Inno Setup script (unsigned installer, PRD §11/§15 Phase 6).
; Build:  iscc build\foundry.iss   (requires Inno Setup 6)
; Expects build\publish (dotnet publish output). The frozen Python CAD sidecar
; (sidecar\dist\foundry-cad, optional) is bundled if present.

#define AppName "Foundry"
#define AppVersion "2.4.1"
#define AppPublisher "Foundry"

[Setup]
; Stable AppId so the updater's installer upgrades in place instead of side-by-side.
AppId={{8F3A2C71-2E2F-4D5E-9B7A-F0C1D2E3A401}
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
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
; WPF app (from: dotnet publish Foundry.App -c Release -r win-x64 -o build\publish)
; ignoreversion is REQUIRED: our DLLs are all assembly-version 1.0.0.0, so without it Inno
; skips overwriting same-version files and the app DLLs (e.g. Foundry.Core.dll) go stale on upgrade.
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Frozen Python CAD sidecar (optional — only if pyinstaller build\sidecar.spec was run)
Source: "..\sidecar\dist\foundry-cad\*"; DestDir: "{app}\sidecar"; Flags: recursesubdirs createallsubdirs skipifsourcedoesntexist ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Foundry.exe"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\Foundry.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
Filename: "{app}\Foundry.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
{ Foundry lives in the system tray, so an upgrade run over a running instance can leave }
{ files locked and produce a partial (mismatched-DLL) install. Force-kill it (and the }
{ spawned CAD sidecar) before copying files so every upgrade is clean. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM Foundry.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM foundry-cad.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
