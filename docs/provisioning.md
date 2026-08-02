---
title: Provisioning, integrity, and self-update
domain: provisioning
status: active
last-reviewed: 2026-08-02
verified-against:
  - source-read: Foundry.Core/Provisioning/**, Update/**, Security/**, and the four *Installer.cs at 813593b
  - working-tree: UNCOMMITTED changes to DownloadVerifier.cs + UpdateTrustPolicy.cs are the version documented here
---

# Provisioning, integrity, and self-update

> **What's in this doc:** the one shared download/verify surface, per-tool pinning (SHA-256 vs Authenticode vs neither), the publisher allow-list, zip-slip-safe extraction, where tools land on disk, the `ToolchainProvisioner` façade, the updater trust policy, and API-key storage.
>
> **What's NOT:** what each tool is *used for* (→ [[pcb]], [[firmware]], and `Foundry.Core/Simulation/**`); how the release pipeline signs Foundry itself (→ `build/SIGNING.md` and `.github/workflows/release.yml`); the Settings UI that binds to the tool panel (→ [[desktop-ui]]).

> **⚠ This doc describes the WORKING TREE, not `main`.** `DownloadVerifier.cs` and `UpdateTrustPolicy.cs` have uncommitted changes at the time of writing, and the update-trust behaviour differs from commit `813593b` in a user-visible way — see [[#updater-trust-policy]].

## Why one shared surface

Five installers download executables from the internet and run them. `Foundry.Core/Provisioning/DownloadVerifier.cs:16-21` exists so that hardening is written once instead of "re-inlined (and re-bugged) across five files". Anything new that fetches a binary belongs here, not in a new bespoke download loop.

The three primitives:

| Primitive | What it guarantees | Failure mode |
|---|---|---|
| `DownloadVerifiedAsync` (`:30`) | streams to a `.part`, hashes while writing, moves into place only on match (`:33-57`) | `IntegrityException`, partial file deleted (`:59-63`) |
| `RequireAuthenticode` (`:142`) | valid embedded signature **and** an expected publisher token | `IntegrityException` before the file is executed |
| `ExtractZipSafe` (`:157`) | every entry resolves inside the target dir | `IntegrityException` on zip-slip (`:165-166`) |

`VerifyAuthenticode` (`:78`) is a `WinVerifyTrust` P/Invoke; it returns **false** on non-Windows or any failure, so callers fail closed (`:76-77`). The publisher check is a pure, testable substring match over the signer subject — `SignerSubjectAllowed` (`:134`) — deliberately broad enough to survive CA wording changes.

An empty expected hash skips the SHA check (`:27-28`) — that is the intended path for artefacts verified by Authenticode *after* extraction, not a licence to skip verification entirely.

## What is pinned, per tool

**The anchor must match what the publisher actually ships.** Measured with `Get-AuthenticodeSignature` on the real binaries (2026-08-02): **arduino-cli.exe, openscad.exe and Renode.exe are all `NotSigned`.** Demanding Authenticode from a publisher that doesn't sign makes the tool permanently uninstallable, so each tool is anchored on the strongest thing its publisher genuinely provides.

| Tool | Fetch | Integrity anchor | Landing dir |
|---|---|---|---|
| arduino-cli 1.5.1 | zip | **pinned SHA-256** (`Foundry.Core/Firmware/FirmwareBuilder.cs:51`) — publisher does not sign | `%LocalAppData%/Foundry/tools/arduino-cli.exe` (`:55`) |
| FreeRouting 2.2.4 jar | jar | **pinned SHA-256** (`Foundry.Core/Pcb/FreeRoutingInstaller.cs:21`) — a `.jar` isn't Authenticode-signable | `…/tools/freerouting/` (`:31`) |
| Temurin JRE 25 | zip | **SHA-256 published by Adoptium**, resolved over TLS at install time (`Foundry.Core/Pcb/FreeRoutingInstaller.cs:ResolveJreAssetAsync`) | `…/tools/java/` (`:36`) |
| Renode 1.16.1 | zip | **pinned SHA-256** (`Foundry.Core/Simulation/RenodeInstaller.cs:PortableSha256`) — publisher does not sign | `…/tools/renode/` (`:22`) |
| OpenSCAD 2021.01 | zip | **pinned SHA-256** (`Foundry.Core/Cad/OpenScadInstaller.cs:PortableSha256`) — publisher does not sign | `…/tools/openscad/` (`:15`) |
| KiCad | winget (per-user) or NSIS exe | Authenticode + publisher `KiCad` **before** silent elevated execution (`Foundry.Core/Pcb/KiCadInstaller.cs:170`) | `%LocalAppData%\Programs\KiCad` or Program Files |

Rules to preserve when adding a tool:

- **Verify before anything observable exists.** Use `ExtractVerifiedZip` (whole dedicated dir) or `ExtractVerifiedFile` (one exe into the shared tools dir). Never `ExtractZipSafe` straight into the live dir followed by a check — see the quarantine section below.
- **Pin a hash unless the publisher demonstrably signs.** Check with `Get-AuthenticodeSignature` before reaching for `RequireAuthenticode`; a publisher-name token is a guess about certificate wording that silently bricks the install when it drifts.
- **Prefer a published checksum over a pinned one for a rolling URL** (the Adoptium pattern) — no stale pin, still fail-closed.

## Quarantine-then-promote

`ExtractVerifiedZip` / `ExtractVerifiedFile` (`Foundry.Core/Provisioning/DownloadVerifier.cs`) extract into a private `.staging-<guid>` directory, verify there, and only then move the payload into place. `PromoteDirectory` retires any previous install first, so a failed *upgrade* restores the working tool rather than uninstalling it.

This shape is load-bearing, not stylistic. Extracting into the live tools dir and verifying afterwards leaves the rejected payload on disk when the check throws — and because every installer's `Locate()` is a bare file-existence test and `ToolchainProvisioner.InstallAsync:84` short-circuits on `IsInstalled`, the binary that **failed** verification is then reported as installed and executed on every subsequent run. Covered by `Foundry.Tests/DownloadVerifierTests.cs`.

Java is **locate-only, never auto-installed via the system** — `JAVA_HOME` then PATH, with a JDK download hint when absent (`Foundry.Core/Pcb/FreeRoutingInstaller.cs:10-11`, `LocateJava` at `:58`). KiCad is the one tool too large to vendor portably, hence winget-first with an NSIS fallback that may cost one UAC prompt (`Foundry.Core/Pcb/KiCadInstaller.cs:129-135`); that install is bounded by a 20-minute watchdog with process-tree kill (`:199-213`).

## The Settings façade

`Foundry.Core/Provisioning/ToolchainProvisioner.cs:30` is the single entry point the "Optional tools" panel binds to. It normalises six tools (`ToolId` at `:10`, display table at `:33-41`) behind one locate → `IsInstalled` → `InstallAsync(progress)` shape.

- `Locate` (`:44`) and `GetStatus` (`:66`) never throw (`:59`).
- `InstallAsync` (`:81`) is idempotent (`:84-88`), reports coarse stages via `IProgress<ToolProgress>` (`:12-14`), and **re-verifies by locating the tool again** before reporting success (`:120-124`).
- Everything except KiCad lands in `%LocalAppData%/Foundry/tools` — portable, no UAC (`:26-28`).

## Updater trust policy

`Foundry.Core/Update/UpdateTrustPolicy.cs:12` is the pure, unit-tested decision; `Foundry.App/App.xaml.cs:204` (`InstallerTrusted`) is the Win32/X509 plumbing that feeds it.

**Working-tree behaviour (fail-closed, strict in all cases)** — `Foundry.Core/Update/UpdateTrustPolicy.cs:17-28` refuses in four distinct cases, each with its own reason string:

1. the running app is unsigned — no publisher to verify against (`:19-20`);
2. the installer failed Authenticode (`:21-22`);
3. the installer is unsigned (`:23-24`);
4. installer signer ≠ app signer, by thumbprint (`:25-26`).

Since Foundry's public builds are currently **unsigned**, case 1 means an auto-downloaded installer is never executed: the user is pointed at the releases page instead (`Foundry.App/App.xaml.cs:178-183`).

> **Divergence from `main`.** Commit `d821bab` ("restore one-click auto-update for unsigned builds — strict only when signed") made the policy permissive when the running app is unsigned. The uncommitted working tree reverts that to strict. Confirm which behaviour is intended before releasing; `Foundry.Tests/UpdateTrustPolicyTests.cs` also has uncommitted edits. Tracked in [[_backlog]].

Supporting guarantees in the update path:

- The update repo is pinned to build-time constants (`Foundry.Core/AppInfo.cs:8-10`), **not** read from config, so a writable `config.json` cannot repoint the updater at an attacker's repo (`Foundry.App/App.xaml.cs:147-150`).
- `App.xaml.cs:208-211` only probes the installer's signature when the app itself is signed — otherwise the decision is "refuse" regardless.
- The download has a per-read **stall watchdog** (default 60 s), because `ResponseHeadersRead` means `HttpClient.Timeout` does not bound the body copy (`Foundry.Core/Update/GitHubUpdater.cs:85-91`, loop at `:105-125`). A stalled transfer aborts and deletes the partial file (`:116-120`).
- Only `http`/`https` URLs are ever handed to the shell (`Foundry.App/App.xaml.cs:232-236`).
- Version comparison strips `v` and pre-release/build metadata (`Foundry.Core/Update/GitHubUpdater.cs:133-139`).

## API keys

`Foundry.Core/Security/CredentialStore.cs:11` stores secrets in Windows Credential Manager (DPAPI-backed) under six fixed target names (`:13-18`): Anthropic, Nexar, DigiKey, Mouser, PcbWay, Jlcpcb. Keys are never written to the project file, `config.json`, or logs; the UI only renders `Mask` output (`:63-66`).

## Editing this domain safely

- **Never add a raw `HttpClient.GetAsync` + `File.WriteAllBytes` download.** Route it through `DownloadVerifiedAsync`, and decide explicitly whether the integrity anchor is a pinned hash or a publisher signature.
- **Never use `ZipFile.ExtractToDirectory`** — it has no path-traversal guard (`Foundry.Core/Provisioning/DownloadVerifier.cs:154-156`).
- Bumping a pinned version means updating the URL **and** its digest/publisher token together; `Foundry.Tests/ProvisioningTests.cs` and `Foundry.Tests/DownloadVerifierTests.cs` cover the pure parts and both have uncommitted additions.
- `RequireAuthenticode` with **no** tokens only proves *some* valid signature exists. Pass the publisher tokens for anything that will be executed.
