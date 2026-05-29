# Optional Toolchain Provisioning — Design

Date: 2026-05-28
Branch: feature/optional-toolchain-installer
Goal: the user must NEVER manually install an external tool. Every optional dependency is
one-click auto-installable from inside Foundry, with status + progress, surfaced in a single
"Optional tools" panel in Settings.

## Summary

Today four tools already auto-download on demand to `%LocalAppData%/Foundry/tools` (portable
zip/jar, no UAC): `arduino-cli`, Renode, OpenSCAD, FreeRouting jar. Two gaps require a manual
step today: **Java** (locate-only) and **KiCad** (locate-only). This spec closes both, then unifies
all six behind a single `ToolchainProvisioner` for the Settings panel.

Verified on this machine (KiCad 10 + Java 21/25 + winget present):
- `winget` resolves to `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` (per-user, no elevation).
- KiCad 10.0 is installed per-user at `%LOCALAPPDATA%\Programs\KiCad\10.0\bin` (kicad-cli.exe +
  python.exe present) — `KiCadInstaller.Locate()` per-user path is correct.
- `winget show -e --id KiCad.KiCad` → KiCad 10.0.3, Installer Type `nullsoft` (NSIS), official exe
  `https://github.com/KiCad/kicad-source-mirror/releases/download/10.0.3/kicad-10.0.3-x86_64.exe`.
- `JAVA_HOME` is unset on this machine, so FreeRouting's `LocateJava()` currently relies on PATH.

---

## 1. Temurin 25 JRE portable .zip — URL + extract recipe

**URL (Adoptium API redirect, no scraping, always latest 25 GA):**
```
https://api.adoptium.net/v3/binary/latest/25/ga/windows/x64/jre/hotspot/normal/eclipse
```
Verified: this returns `307 Temporary Redirect` to a GitHub release asset, e.g.
`https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.3%2B9/OpenJDK25U-jre_x64_windows_hotspot_25.0.3_9.zip`
— a JRE (not JDK) `.zip`, exactly the portable artifact we want. `HttpClient` follows the 307
automatically, so we just `GetByteArrayAsync(ApiUrl)`.

Why JRE 25: the pinned FreeRouting 2.2.4 jar is class file version 69 = Java 25. JRE (not JDK) is
sufficient to run a jar and is smaller.

**Extract recipe** (mirrors `OpenScadInstaller.DownloadAsync`):
1. Target dir: `%LocalAppData%/Foundry/tools/java/`.
2. Download the zip from the API URL to `tools/java/jre.zip`.
3. `ZipFile.ExtractToDirectory(zip, JavaToolsDir, overwriteFiles: true)`.
4. The zip nests everything under a single top-level folder named like `jdk-25.0.3+9-jre/`
   (Temurin names the JRE folder with a `jdk-...-jre` prefix). So `bin/java.exe` lands at
   `tools/java/jdk-25.*/bin/java.exe`. Do NOT assume a fixed folder name — locate it by recursive
   search for `java.exe`, identical to how Renode finds `Renode.exe` in its versioned subfolder.
5. Delete the zip.

**Locate recipe** — `LocateJava()` must check the app-local java dir FIRST (before JAVA_HOME/PATH):
1. `%LocalAppData%/Foundry/tools/java/**/bin/java.exe` (recursive — `Directory.EnumerateFiles(JavaToolsDir, "java.exe", SearchOption.AllDirectories)`).
2. then `JAVA_HOME/bin/java.exe` (existing).
3. then PATH (existing).

This keeps an existing system Java working while making the app-local download authoritative.

---

## 2. KiCad auto-install — command + fallback

Two evaluated approaches:

**(a) winget — RECOMMENDED primary.**
```
winget install -e --id KiCad.KiCad --scope user --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
```
- Per-user (`--scope user`) installs to `%LOCALAPPDATA%\Programs\KiCad\<ver>\bin` — **no UAC**, which is
  exactly where `KiCadInstaller.Locate()` already finds it (verified on this machine).
- `--silent` suppresses installer UI; `--accept-*-agreements` + `--disable-interactivity` make it
  fully non-interactive (required since we shell it from a GUI with no console).
- Presence check: `where winget` (or `(Get-Command winget).Source`). winget ships with App Installer
  on Windows 10 1809+/11, so it's present on supported targets; absence is the fallback trigger.
- Robustness notes: if `--scope user` is unsupported for this package on a given winget build, winget
  may fall back to machine scope (which triggers UAC) — treat a non-zero/elevation-declined exit as
  "fall through to (b) guidance". Run with `CreateNoWindow`, capture stdout/stderr, surface to AppLog.

**(b) Official NSIS exe, silent — FALLBACK only (may prompt UAC).**
```
kicad-10.0.3-x86_64.exe /S
```
- The KiCad NSIS installer **requests admin rights** (confirmed: its install.nsi requests
  elevation; community reports the x64 `/S` switch historically flaky). So `/S` is NOT a guaranteed
  no-UAC path — it will trigger a UAC prompt for the default Program Files install. There is no
  documented supported per-user `/D=` install that avoids elevation.
- Therefore the exe is only a fallback when winget is absent. Download the official exe (URL from the
  winget manifest / kicad.org), run `<exe> /S`, and accept that the OS may show one UAC prompt — this
  is the single unavoidable degraded case, and it is still "one click in Foundry + approve UAC", not a
  manual download/install.

**Recommendation:** primary = winget per-user (no UAC, lands where Locate() expects); fallback =
download official NSIS exe + `/S` (one UAC prompt). After either, re-run `KiCadInstaller.Locate()` to
confirm. Never block on a manual download.

Sources:
- winget install options / scope / silent / agreements: https://learn.microsoft.com/en-us/windows/package-manager/winget/install
- KiCad NSIS installer requests admin, `/S` flakiness: https://silentinstallhq.com/kicad-silent-install-how-to-guide/ ; https://gitlab.com/kicad/packaging/kicad-win-builder/-/issues/135
- Adoptium API binary endpoint: https://api.adoptium.net/v3/binary/latest/25/ga/windows/x64/jre/hotspot/normal/eclipse

---

## 3. ToolchainProvisioner contract (Foundry.Core)

A single facade over all six optional tools so the Settings "Optional tools" panel binds to one list.
It wraps the existing `Download*` methods (arduino-cli, OpenSCAD, Renode, FreeRouting jar) and the two
new installers (Java JRE, KiCad). Mirrors the existing locate → IsInstalled → InstallAsync(progress)
shape; reports progress through a simple callback and AppLog.

```csharp
namespace Foundry.Core.Toolchain;

/// <summary>Stable id for an optional external tool (used as the dictionary/registry key and UI key).</summary>
public enum ToolId { ArduinoCli, OpenScad, Renode, FreeRouting, JavaJre, KiCad }

/// <summary>A coarse progress update for the Settings panel. Percent is null when unknown (download
/// size unknown / indeterminate step); the UI shows an indeterminate bar in that case.</summary>
public sealed record ToolProgress(string Stage, int? Percent = null);

/// <summary>One optional tool's identity + live state for the "Optional tools" panel.</summary>
public sealed record ToolStatus(
    ToolId Id,
    string Name,        // "KiCad", "Java (JRE 25)", "Arduino CLI", …
    string Purpose,     // one-line why Foundry needs it
    bool Installed,
    string? Location);  // resolved exe/jar path when installed, else null

/// <summary>
/// Single entry point the Settings "Optional tools" panel binds to. Each tool exposes
/// IsInstalled + InstallAsync(progress, ct), wrapping the existing on-demand installers
/// (OpenScadInstaller / RenodeInstaller / FreeRoutingInstaller / FirmwareBuilder) plus the new
/// Java JRE and KiCad provisioning. Installs land in %LocalAppData%/Foundry/tools (portable, no UAC)
/// except KiCad, which is provisioned via winget per-user (fallback: silent NSIS exe).
/// </summary>
public static class ToolchainProvisioner
{
    /// <summary>All optional tools in display order, with their one-line purpose.</summary>
    public static IReadOnlyList<ToolDescriptor> Tools { get; }

    /// <summary>Static identity + why-string for a tool (no I/O).</summary>
    public sealed record ToolDescriptor(ToolId Id, string Name, string Purpose);

    /// <summary>Current install state for one tool (runs the tool's Locate()).</summary>
    public static ToolStatus GetStatus(ToolId id);

    /// <summary>Snapshot of every tool's state for the panel (calls GetStatus for each).</summary>
    public static IReadOnlyList<ToolStatus> Snapshot();

    /// <summary>Is this tool already installed/located?</summary>
    public static bool IsInstalled(ToolId id);

    /// <summary>
    /// Install/download the tool on demand. Reports coarse progress via <paramref name="progress"/>
    /// (also written to AppLog). Idempotent: a no-op returning quickly when already installed.
    /// Throws on failure (caller shows the message); never leaves a half-extracted dir on success.
    /// </summary>
    public static Task<ToolStatus> InstallAsync(
        ToolId id, IProgress<ToolProgress>? progress = null, CancellationToken ct = default);
}
```

### Per-tool wiring

| ToolId       | Name             | Purpose (one-line why)                                  | IsInstalled / Locate              | InstallAsync delegates to                              |
|--------------|------------------|---------------------------------------------------------|-----------------------------------|--------------------------------------------------------|
| ArduinoCli   | Arduino CLI      | Compile + flash generated firmware to your board.       | `FirmwareBuilder.Locate()`        | `FirmwareBuilder.DownloadCliAsync`                     |
| OpenScad     | OpenSCAD         | Render parametric SCAD enclosures to mesh.              | `OpenScadInstaller.Locate()`      | `OpenScadInstaller.DownloadAsync`                      |
| Renode       | Renode           | Headless firmware simulation (RP2040 etc.).             | `RenodeInstaller.Locate()`        | `RenodeInstaller.DownloadAsync`                        |
| FreeRouting  | FreeRouting      | Auto-route the PCB (needs Java).                        | `FreeRoutingInstaller.JarPresent` | `FreeRoutingInstaller.DownloadJarAsync`                |
| JavaJre      | Java (JRE 25)    | Runs the FreeRouting auto-router jar.                   | `FreeRoutingInstaller.LocateJava()` (app-local first) | new `FreeRoutingInstaller.DownloadJreAsync` |
| KiCad        | KiCad            | Design + export the PCB and run DRC/fab.                | `KiCadInstaller.Locate()`         | new `KiCadInstaller.InstallAsync` (winget → exe)       |

Notes:
- `InstallAsync` maps the long-running download/extract to `ToolProgress` stages: `"Downloading…"`
  (indeterminate or byte-percent), `"Extracting…"`, `"Verifying…"`, `"Installed"`. AppLog gets an
  `Info` line per stage, matching the existing installers' logging category (`build`/`cad`/`sim`/`pcb`).
- KiCad's `InstallAsync` shells `winget` (capture output, no window); on winget-absent or non-zero
  exit it downloads the official NSIS exe and runs `/S`, then re-locates. It reports `"Installing via
  winget…"` / `"Running installer…"`.
- Java's new `DownloadJreAsync` extracts the Temurin zip and the app-local `bin/java.exe` becomes the
  launcher `Locate()`/`LocateJava()` returns first — so FreeRouting works with zero system Java.

### Settings UI hook (out of scope of Core, noted for the implementer)

`SettingsViewModel` gets an `ObservableCollection<OptionalToolVm>` built from
`ToolchainProvisioner.Snapshot()`, each with an `InstallCommand` ([RelayCommand], CanExecute =
`!Installed && !Busy`) that calls `InstallAsync` with an `IProgress<ToolProgress>` marshalled to the
status text/bar — mirroring the API-key sections in `SettingsView.xaml` and the in-flow Renode/PCB
install UX in `TabViewModels.cs`. No version bumps.
