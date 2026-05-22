# Foundry — Go-Live Checklist

**v1.0 reached.** Every PRD functional requirement (F1–F12) and acceptance criterion (§19) is met.
The only remaining items are **externally gated** (need the user to supply a credential), not missing
features:
- **Code-signing** — the signing pipeline (`build/sign.ps1` + `release.yml`) and the updater's
  publisher-verification are wired; add a code-signing cert as the `SIGN_PFX_BASE64`/`SIGN_PASSWORD`
  repo secrets to remove the Windows SmartScreen warning.
- **Live Nexar pricing** — `NexarSourcingProvider` is implemented; add a Nexar key in Settings to get
  live price/stock. Without it, the BOM uses generated estimates and still produces working cart links.

Status history below. Checked = done; unchecked = remaining.

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

## Done since (v0.4.2 → v0.4.18)
- [x] **Generation verified against a live key** (multiple prompts; every tab renders).
- [x] **Generic wiring diagram** — auto-layout from the netlist. **Enclosure** is real CSG (base+lid,
      cutouts, vents, mounting) from the schema; 3D view is generic.
- [x] **Project library** — generated projects auto-save; recents with reopen/delete.
- [x] **Chat iteration = real re-generation** — chat edits revise the whole project (or answer questions).
- [x] **Validation auto-fix** — deterministic where possible, else AI-generated; re-validates.
- [x] **Exports** — branded PDF (project spec + validation report), BOM CSV, guide MD, firmware folder, STL.
- [x] **Global crash handler** + diagnostics/audit log + status-bar AI progress.
- [x] **Security hardening** — signed-update verification, pinned update repo, no secrets in repo.
- [x] **Clean-machine sanity** — self-contained published build runs (3D, sidecar, PDF native deps ship).
- [x] **1.0 polish pass** — removed dead/unwired buttons and hardcoded demo strings across all tabs.

## Still open
- [ ] **Code-sign the installer + exe.** Pipeline is wired (`build/sign.ps1` + `release.yml`, the updater
      already verifies publisher signature) — just needs a real cert added as `SIGN_PFX_BASE64`/`SIGN_PASSWORD`.
      Until then SmartScreen warns. See `build/SIGNING.md`.
- [ ] **Live Nexar pricing.** `NexarSourcingProvider` is implemented but not verified against the live API
      (no credentials). BOM falls back to the generated estimates.
- [ ] **Wiring image export** (PNG/SVG) + embedding the diagram in the PDF.
- [ ] Faster iteration option (a dedicated fast chat/edit model so chat/fixes don't wait on Opus).
- [ ] Nice-to-have: high-DPI/multi-monitor QA, keyboard/accessibility, `.foundry` file association, LICENSE.

## Release mechanics (already wired)
- Bump `Foundry.Core/AppInfo.cs`, `Foundry.App.csproj <Version>`, `build/foundry.iss` AppVersion.
- `git tag vX.Y.Z && git push --tags` → Actions builds the sidecar-bundled installer and publishes the release.
- Existing installs offer the update via the tray. Repo: https://github.com/misterentity/foundry
