# Foundry — packaging (Phase 6)

This folder holds the packaging scaffolding (PRD §11/§15 Phase 6). It is **scaffolding**:
the scripts below describe the build; running them requires the toolchains installed
(PyInstaller, Inno Setup) and is environment-specific, so they are not executed in CI here.

## 1. Freeze the Python CAD sidecar (PyInstaller, single-folder)

The `.NET` app spawns the sidecar as a child process. For a packaged build, freeze it so
the target machine doesn't need Python:

```powershell
# from sidecar/ with the venv active
pip install pyinstaller
pyinstaller --noconfirm build/sidecar.spec
# → sidecar/dist/foundry-cad/foundry-cad.exe  (single-folder bundle)
```

`SidecarHost` already falls back to a frozen exe / system Python; for the packaged app,
point it at `dist/foundry-cad/foundry-cad.exe` (resolve next to the app, then PATH).

## 2. Publish the WPF app

```powershell
dotnet publish Foundry.App -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=false -o build/publish
```

(Use `--self-contained true` to bundle the .NET runtime if the target lacks .NET 8 Desktop.)

## 3. Build the installer (Inno Setup — unsigned dev installer)

```powershell
# requires Inno Setup 6 (iscc.exe on PATH)
iscc build/foundry.iss
# → build/AppPackages/FoundrySetup.exe
```

Inno is the pragmatic unsigned path (PRD §11 allows "WiX/Inno Setup if unsigned").
For the Store / signed distribution, MSIX is the alternative — wrap `build/publish` +
the frozen sidecar with the Windows App SDK packaging project and sign with your cert.

## Disclaimers & samples

- The "design aid — verify before you build" disclaimer is shown in the status bar and on
  the Assembly Guide (PRD §10/§13) — keep it in any repackage.
- The bundled demo project (soil-moisture sensor) is the sample project (PRD §12 first-run).
