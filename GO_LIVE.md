# Foundry — Go-Live Checklist

Status of the build and what's left before a public 1.0. Checked = done; unchecked = remaining.

## Done (shipping in v0.4.2)
- [x] Full UI: onboarding, library launcher, workspace (rail/main/chat), all 7 tabs.
- [x] Real Anthropic Messages API behind `IAnthropicClient` (offline stub when no key).
- [x] Deterministic validation engine (power/voltage/pins/I²C).
- [x] Firmware generation (`pinmap.h` from netlist + Arduino/MicroPython project export).
- [x] Enclosure CAD sidecar (FastAPI → STL) + HelixToolkit 3D preview; **frozen sidecar bundled in the installer**.
- [x] Live sourcing (Nexar) + cart links + DigiKey BOM CSV; offline fallback.
- [x] Settings (keys → Credential Manager, model dropdown, generation/export/sourcing, updates).
- [x] System-tray app + GitHub-releases auto-update (download + run installer).
- [x] Inno installer + GitHub Actions release pipeline (test → publish → freeze sidecar → installer → release).
- [x] Demo data removed from the default flow: empty library, real "New project" prompt, explicit "Open sample".
- [x] 38 unit tests; scrollbars/disclaimers match the design.

## P0 — blocking for a real public release
- [ ] **Verify generation against a live key.** `ProjectGenerator` is unit-tested on a fixture but has NOT
      been run against the real API. Run several prompts, confirm the JSON parses and every tab is sane.
      Tune the system prompt / add `output_config` JSON-schema for guaranteed structure.
- [ ] **Code-sign the installer + exe** (Authenticode / EV cert). Unsigned builds trip SmartScreen
      ("Windows protected your PC") — a major drop-off for testers. Add signing to `release.yml`.
- [ ] **Generic wiring diagram + enclosure preview.** Both are currently hand-laid for the sample
      (`WiringDiagramControl`, `EnclosureIsoControl` use fixed coordinates). For arbitrary generated
      projects they don't reflect the real netlist/dimensions. Implement an auto-layout wiring renderer
      (the connection ledger + 3D enclosure already are generic) and drive the iso preview from the schema.
- [ ] **Clean-machine install test.** Install `FoundrySetup.exe` on a fresh Windows 11 box (no .NET, no
      Python): app launches, tray works, sidecar (frozen) spawns, 3D preview renders, update check works.
- [ ] **Auto-update upgrade test.** Install v0.4.1, then confirm tray → Check for updates pulls v0.4.2,
      runs the installer, and upgrades in place (stable AppId).

## P1 — important for a good experience
- [ ] **Project save/load + recent list.** Persist generated projects (`ProjectStore`) to the library;
      "Recent" is currently an empty state.
- [ ] **Chat iteration = real re-generation.** `ChatPipeline` returns NL replies; wire chat turns to
      re-run affected stages and mutate the Project (PRD §7 staged generation).
- [ ] **Finding auto-fix.** The "Apply & re-run" / suggested-fix CTAs render but aren't wired to mutate
      the netlist + re-validate.
- [ ] **Remaining exports.** Wiring SVG/PNG and guide PDF (BOM CSV, guide MD, firmware folder, STL done).
- [ ] **Sourcing depth.** Verify Nexar OAuth/GraphQL live; optional direct DigiKey/Mouser; real cart upload.
- [ ] **Error handling pass.** Per-stage failure UI, retry-once, no uncaught exceptions crash the shell
      (PRD §13). Add a global `DispatcherUnhandledException` handler + user-friendly error surface.

## P2 — polish / hardening
- [ ] Logging (no secrets) + a way to grab logs for bug reports; verify keys never hit disk/logs (PRD §14).
- [ ] High-DPI + multi-monitor QA; keyboard nav / accessibility names.
- [ ] App icon polish (proper multi-size .ico), Start-menu/uninstall metadata, file association (`.foundry`).
- [ ] LICENSE, privacy note (network calls: Anthropic + configured distributors only), README screenshots.
- [ ] Performance on large designs (many parts/nets); cancellation of in-flight generation.
- [ ] Telemetry opt-in (if any) with clear consent; crash reporting.

## Release mechanics (already wired)
- Bump `Foundry.Core/AppInfo.cs`, `Foundry.App.csproj <Version>`, `build/foundry.iss` AppVersion.
- `git tag vX.Y.Z && git push --tags` → Actions builds the sidecar-bundled installer and publishes the release.
- Existing installs offer the update via the tray. Repo: https://github.com/misterentity/foundry
