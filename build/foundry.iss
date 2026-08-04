; Foundry — Inno Setup script (unsigned installer, PRD §11/§15 Phase 6).
; Build:  iscc build\foundry.iss   (requires Inno Setup 6)
; Expects build\publish (dotnet publish output). The frozen Python CAD sidecar
; (sidecar\dist\foundry-cad, optional) is bundled if present.

#define AppName "Foundry"
#define AppVersion "2.8.0"
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
; Without these the SETUP EXE ships with a blank FileVersion, so the artifact could not be identified from
; its own metadata (Get-AuthenticodeSignature / file properties showed nothing to check a release against).
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup

[InstallDelete]
; Inno only ever OVERWRITES what it ships; it never removes what a previous version left behind. That
; turned {app} into an archaeological pile, and the consequence was not cosmetic:
;
;   An older build was published SELF-CONTAINED, so it installed hostfxr.dll, hostpolicy.dll, coreclr.dll
;   and a partial .NET runtime into {app}. The current build is FRAMEWORK-DEPENDENT. On launch the apphost
;   finds hostfxr.dll sitting next to Foundry.exe and prefers it over the machine-wide runtime, then
;   resolves as if self-contained and reports "No frameworks were found." That is Event ID 1023 in the
;   Windows Application log, and when it does start it silently runs on the stale pinned runtime rather
;   than the .NET 8 the user has installed and patched.
;
; Wiping {app} first is safe: Foundry keeps NOTHING user-generated here. Projects, revisions, settings and
; logs live in %AppData%\Foundry, and downloaded toolchains in %LocalAppData%\Foundry\tools.
Type: filesandordirs; Name: "{app}\*"

[Files]
; WPF app (from: dotnet publish Foundry.App -c Release -r win-x64 -o build\publish)
; ignoreversion is REQUIRED: our DLLs are all assembly-version 1.0.0.0, so without it Inno
; skips overwriting same-version files and the app DLLs (e.g. Foundry.Core.dll) go stale on upgrade.
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Frozen Python CAD sidecar (optional — only if pyinstaller build\sidecar.spec was run)
Source: "..\sidecar\dist\foundry-cad\*"; DestDir: "{app}\sidecar"; Flags: recursesubdirs createallsubdirs skipifsourcedoesntexist ignoreversion

[UninstallDelete]
; Uninstall likewise leaves anything it did not personally install. Remove the directory outright so a
; reinstall never inherits a half-populated runtime. User data in %AppData% is deliberately preserved.
Type: filesandordirs; Name: "{app}"

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
