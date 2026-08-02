---
title: Documentation Backlog
domain: meta
status: active
last-reviewed: 2026-08-01
---

# Documentation Backlog

> **What's in this doc:** known documentation gaps, TODO-verify flags, and undocumented code paths. Append-only — items get resolved by removing them when the corresponding doc section is written/verified.
>
> **What's NOT:** finished documentation (those live in the domain docs).

## TODO verify

<!-- Sections in domain docs where verification was skipped (tool unavailable, ambiguous query). Re-verify and remove the entry. -->

- **[[provisioning]] — updater trust policy diverges from `main`.** The working tree reverts commit `d821bab` ("restore one-click auto-update for unsigned builds") back to strict fail-closed in `Foundry.Core/Update/UpdateTrustPolicy.cs:17-28`. The doc describes the *working tree*. Confirm which behaviour ships, then re-verify the section and drop this entry.
- **Live-behaviour claims were verified by source read, not by execution.** No `dotnet test`, no real KiCad/arduino-cli run was performed while seeding the vault (2026-08-01). The `pcb-live` / `live-sim` CI lanes are the ground truth for [[pcb]] and simulation; a doc-sync pass that can run them should re-verify against a real run.

## Open work

- **The shipped demo fails its own mechanical check.** `DemoData.CreateSoilMoistureProject` declares a
  62 × 48 × 26 mm case for a board that places at 102 × 67 mm. The finding is correct — an 88 mm 18650
  holder cannot go in a 62 mm case at any packing density — but the sample project should be
  self-consistent. Either give the demo a realistic case or a smaller battery.
- **`Enclosure.Inner` is still model-guessed** rather than derived from `EnclosureFit.MinimumInner`.
  Deliberate for now: the engine reports the mismatch and offers the derived size, rather than silently
  overwriting what the user asked for. Revisit if users just want it to be right.
- **The sample project's findings were hard-coded** (`DemoData.cs`) and `MainViewModel.OpenSample` now
  revalidates so the report card shows what the engine actually computes. `BuildDemoFindings` is still
  referenced for the project's stored state — decide whether to delete it or keep it as seed data.

## Undocumented behavior

<!-- Code paths or features that exist but have no domain doc section. Add an entry when noticed; resolve by writing the section. -->

Five seams are mapped in `_meta/doc-ownership.yml` but have no domain doc yet. Ordered by how much an agent would have to guess without one:

- **`simulation`** — `Foundry.Core/Simulation/**` + `sidecar-avr/**`. Two simulators behind `ISimulator` (`RenodeSimulator`, `Avr8jsSimulator`) chosen by `SimulatorFactory.For` (`Foundry.Core/Simulation/SimulatorFactory.cs:15`); Renode `.repl`/`.resc` are generated; the avr8js runtime ships as a **committed bundle** that CI fails on if stale (`.github/workflows/ci.yml:29-35`). High value — the pinning and the bundle-freshness contract are both non-obvious.
- **`project-model`** — `Foundry.Core/Project/**` + `Config/**` + `Export/**` + `Diagnostics/**`. The canonical `Project` document (`Foundry.Core/Project/Project.cs:14`) that every tab reads/writes, plus the library store, revisions, templates, `.foundryproj` bundles, `AppLog`, and `ProcessRunner`'s timeout constants.
- **`validation`** — `Foundry.Core/Validation/**` + `Wiring/**`. The deterministic rules engine (`RulesEngine.Validate`, `Foundry.Core/Validation/RulesEngine.cs:19`) whose verdict — never the model's — decides pass/fail, plus the bounded auto-fixes.
- **`sourcing`** — `Foundry.Core/Sourcing/**`. Nexar-backed live pricing vs. offline estimates, ranked alternates, multi-distributor carts, budget mode. Worth documenting the estimate-vs-live labelling honestly.
- **`build-release`** — `.github/workflows/**` + `build/**`. Three CI lanes (`test`, `pcb-live`, `live-sim`), the release pipeline's build-integrity verification step (`.github/workflows/release.yml:52`), PyInstaller sidecar freeze, Inno Setup, and conditional signing.

## Partial coverage (seeded docs)

<!-- `node scripts/vault-doctor.mjs --check-coverage` — informational, but recorded so low coverage isn't mistaken for full coverage. -->

Measured 2026-08-01. A doc's declared `paths:` are wider than what it currently cites; an agent reading the doc must not assume the uncited files behave as described.

| Doc | Cited | Notable uncovered |
|---|---|---|
| [[provisioning]] | 12/14 | `Foundry.Tests/CredentialStoreTests.cs`, `Foundry.Tests/UpdaterTests.cs` |
| [[firmware]] | 4/8 | `FirmwareGenerator.cs` (the deterministic offline fallback), `FirmwareViewModel.cs` |
| [[generation]] | 8/16 | `ChatPipeline.cs` / `IPipeline.cs` (the chat turn pipeline), `ProjectGenerator.Enclosure.cs`, `ComponentKb.cs` |
| [[pcb]] | 20/48 | `Fabrication/KiCadNetlist.cs` + `PinReport.cs`, `DrcReport.cs` parsing, `Fab/BoardDimensions.cs`, `Fab/FabEstimator.cs`, the JLCPCB/PCBWay providers |
| [[desktop-ui]] | ~21% of `Foundry.App/**` | every `Views/**.xaml` and most per-tab view models — **by design**; per-tab behaviour is owned by its domain doc. `coverage-extensions: [.cs, .xaml]` is set to drop fonts/icons. |

Highest-value gaps to close first: `KiCadNetlist.Nets` (the netlist that [[pcb]] treats as given), `DrcReport.Parse`/`DominantClasses` (the loop's remediation input), and `FirmwareGenerator.Generate` (what the AI firmware pass falls back to).

## Measured gates

<!-- Empirical results that constrain what can be built. Re-measure if KiCad or the corpus changes. -->

Measured 2026-08-02 over 15 real KiCad demo boards (4,841 nets / 16,635 pins), exported with
`kicad-cli sch export netlist --format kicadsexpr` and read by `Foundry.Core/Fabrication/KiCadNetlistReader.cs`.

**Electrical verification of third-party boards — FAILED the gate.**

| approach | consumer-net coverage | false FAILs on working boards |
|---|---|---|
| per-net only | 34.8% | 49.1% (188/383) |
| + traversal through passives | 98.7% | 34.9% (413/1184) |
| + connectors treated as unprovable | 75.5% | 36.6% (331/905) |

Coverage is rescuable; precision is not. `pintype` says what a pin *is*, never what the design
*intends* — `passive` is 54% of all pins and `power_out` appears **46 times in 4,841 nets**. A generic
"is this net driven?" verifier is not shippable on netlist data alone. **Ingest, however, is solved:**
99.5% of pins carry a usable type and a 221-net board exports in 0.9 s.

**Mechanical verification — passes, but only for Foundry's own designs.**

- Only **16.7%** of footprints on those boards come from standard KiCad libraries (real designs vendor
  their own), so heights for arbitrary imported boards are mostly unavailable.
- Of the 25 footprints `FootprintMap` emits, 21 ship a STEP model; the remaining 4 are curated. **100%.**

Conclusion driving the roadmap: proof is achievable where Foundry owns both the design intent and the
footprint vocabulary — i.e. on its own output — not on arbitrary third-party boards.

## Unmatched files

<!-- Files appearing in `match-docs.mjs` output that don't map to any doc-ownership.yml entry. Resolve by either adding an ownership entry or confirming the file legitimately doesn't belong to any doc. -->

- Not yet run. Execute `node scripts/match-docs.mjs main` and record genuinely unowned files here.
- Root-level `PRD.md`, `PRD-v2.md`, `PLAN.md`, `p0-plan.md`, `CLAUDE_CODE_KICKOFF.md`, `GO_LIVE.md` are **per-feature design history** and are deliberately out of vault scope (`_meta/vault-conventions.md` → Scope). They are not unowned; they are non-vault.
