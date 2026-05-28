# Track B — Auto-PCB Design Spec (PLAN ONLY)

Date: 2026-05-28
Status: Proposed
Owner area: `Foundry.Core/Pcb/`
Author: design pass (no code in this document)

---

## 1. Why this is the moat

Foundry already turns a prompt into a validated design: components + pins + nets + firmware,
with a deterministic `generate -> compile-check -> AI-fix` loop proving the firmware actually
builds (`FirmwareBuilder.cs`). Track B extends that exact pattern one layer down the stack — from
"this design is electrically valid and the firmware compiles" to **"here is a fab-ready 2-layer
PCB that passes DRC and could be ordered."**

That is the deepest, least-cloneable part of the product because it chains three things almost
nobody combines:

1. An **authoritative netlist** Foundry already owns (`Foundry.Core/Fabrication/KiCadNetlist.cs`,
   union-find nets, power/ground/I2C naming).
2. **LLM placement reasoning** (group by function, decoupling caps next to power pins, antenna at
   board edge, connectors at edges) — the part humans pay EE contractors for.
3. A **deterministic router + DRC gate** that keeps the LLM honest, identical in spirit to the
   firmware compile gate. The AI proposes; KiCad's DRC disposes; the AI fixes; repeat.

The design philosophy is preserved exactly: **the AI generates, validation is deterministic, and
the board (Gerbers/3D) is a RENDERER of generated + machine-verified data.** Placement is the only
genuinely "creative" step; everything downstream is deterministic tooling we shell out to.

---

## 2. Toolchain decision

We invoke external, industry-standard, free tools as subprocesses — exactly the pattern already
proven in `FirmwareBuilder.Locate()/DownloadCliAsync()` and `OpenScadInstaller`. We do **not** link
or vendor their source.

### 2.1 Primary toolchain (recommended)

| Stage | Tool | Invocation | Notes |
|---|---|---|---|
| Netlist (have it) | Foundry `KiCadNetlist` | in-process | Already ships. Needs footprint field populated (Phase v2.2). |
| Netlist -> board | **pcbnew Python API** (KiCad 9) | `python script.py` run against the bundled KiCad Python, or `kicad-cli` where it covers the step | Load footprints, add nets, place, write `.kicad_pcb`. The SWIG `pcbnew` module is the only first-party way to *programmatically build a board with footprints + nets + coordinates*. |
| Board outline / placement mutation | pcbnew Python | same | `SetPosition()`, board edge on `Edge.Cuts`, courtyard collision checks. |
| Ratsnest | pcbnew Python | same | nets are connected by pad `SetNetCode`; ratsnest is derived for display. |
| DSN export | `kicad-cli pcb export specctra` (KiCad 9) or pcbnew `ExportSpecctraDSN` | subprocess | Specctra `.dsn` is FreeRouting's input. |
| Autoroute | **FreeRouting** (Java jar) | `java -jar freerouting-x.y.z.jar -de board.dsn -do board.ses -mp <passes>` | Headless CLI, no GUI. Mature, the de-facto open autorouter. |
| SES import | pcbnew `ImportSpecctraSES` (or `kicad-cli` once it supports it) | subprocess | Applies routed tracks/vias back onto the `.kicad_pcb`. |
| DRC | `kicad-cli pcb drc --format json --exit-code-violations board.kicad_pcb` | subprocess | **This is the gate.** JSON report + nonzero exit on violations -> parse like `FirmwareBuilder.Parse()`. |
| Gerbers | `kicad-cli pcb export gerbers` | subprocess | One file per layer, X2 by default. |
| Drill | `kicad-cli pcb export drill --format excellon --generate-map` | subprocess | Excellon + map. |
| 3D preview (optional) | `kicad-cli pcb export glb` / `step` | subprocess | Feeds the existing 3D viewer path; nice-to-have. |
| Fab order (optional) | **JLCPCB API** (REST) primary; PCBWay API secondary | HTTPS | Upload Gerber zip, auto-quote, place order, track. |

### 2.2 Why pcbnew Python *and* kicad-cli (not one or the other)

`kicad-cli` is excellent at **export/verify** verbs (gerbers, drill, drc, dsn export, glb) but it
**cannot synthesize a board from a netlist or place parts**. The only first-party programmatic way
to create footprints, assign pads to nets, and set XY positions is the `pcbnew` SWIG Python module
(`pcbnew.LoadBoard`, `PCB_IO().FootprintLoad(lib, name)`, `NETINFO_ITEM`, `pad.SetNetCode`,
`footprint.SetPosition`, `board.Save`). So:

- **Board construction + placement + SES import** -> pcbnew Python scripts we author and ship as
  resources, executed against KiCad's bundled Python interpreter.
- **Verification + export (DRC, gerbers, drill, dsn, glb)** -> `kicad-cli` (cleaner, stable,
  documented CLI surface, JSON output ideal for our `Parse()` idiom).

Both ship inside one KiCad install, so locating/installing is a single dependency
(`KiCadInstaller`), mirroring `OpenScadInstaller`.

### 2.3 Alternatives evaluated (and why not, for now)

- **KiCad built-in router only (no autoroute).** KiCad has no batch autorouter; interactive only.
  Not usable headless. Rejected for autoroute; we still use everything else KiCad.
- **Pure-LLM "route the traces" via coordinates.** Tempting but a trap — routing is a hard
  geometric/DRC-constrained search; LLMs produce shorts and DRC failures. Keep routing
  deterministic. LLM stays at the placement altitude only.
- **`SKiDL` + pcbnew.** SKiDL is a netlist generator; we already have a netlist. We'd only borrow
  its pcbnew placement idioms, not adopt it as a dependency.
- **Commercial autorouters (Electra, Topor/TopoR).** Better routing, but non-free / licensing
  friction; defeats the "could be ordered for free" MVP. Keep FreeRouting; leave a clean seam
  (the DSN/SES boundary) so a better router can be swapped in later.
- **`atopile` / declarative HDL.** Different abstraction (source-of-truth is code, not our Project
  model). Interesting long-term inspiration; not a dependency.
- **Cloud routing API (FreeRouting has an API server / Docker).** Useful escape hatch if local
  Java is a problem, but adds a network dependency to a core feature. Local jar first; Docker/API
  server as a documented fallback.

---

## 3. Licensing (must-read before any code)

- **KiCad** (kicad-cli + pcbnew): **GPL-3.0**. We invoke `kicad-cli` as a subprocess and run our
  own pcbnew Python scripts on KiCad's interpreter. No linking, no vendoring of KiCad source into
  Foundry assemblies. This is the same posture as OpenSCAD/arduino-cli today. ✅ Clean.
- **FreeRouting**: **GPL-3.0** (the project's own license page states GPLv3; do not trust the repo
  badge, which can read as Apache). Therefore: **shell out to the jar, never bundle or statically
  embed it, never link it.** Download on demand to `%LocalAppData%/Foundry/tools/freerouting/`
  exactly like the OpenSCAD portable zip. Foundry's own code stays separate and is not a derivative
  work. Surface attribution + the GPL notice in an About/licenses screen.
- **Java runtime** for FreeRouting: recent FreeRouting installers bundle a JRE; the platform jar
  needs JRE 21+ (25 recommended). Decision: prefer the FreeRouting **self-contained installer/zip
  with bundled JRE** so users don't need their own Java; fall back to a located system `java`.
- **Footprint libraries**: KiCad's standard footprint libs are **CC-BY-SA 4.0 with an exception**
  that explicitly allows using the footprints in your boards without the board inheriting the
  license. Safe to use as the footprint source. ✅
- **Fab APIs** (JLCPCB/PCBWay): commercial terms / API keys, user-supplied. No license issue for
  Foundry; just never hard-code keys.

Net: every external tool is subprocess-invoked and downloaded on demand. Foundry ships no GPL code.

---

## 4. Footprint assignment (the quiet hard problem)

A netlist is useless to a PCB without a **footprint per component**. Today `KiCadNetlist` writes an
empty `(footprint "")`. We need a deterministic mapping `ComponentSpec -> KiCad footprint id`
("LibNickname:FootprintName", e.g. `Capacitor_SMD:C_0603_1608Metric`).

Data sources, in priority order:

1. **Explicit footprint on the part (best).** Extend `ComponentSpec` (or its KB record) with an
   optional `Footprint` field. When the KB/AI knows the exact part (e.g. an MPN), it can carry the
   canonical KiCad footprint id. This is the authoritative path and mirrors how `Ref`/MPN already
   live on the component.
2. **Package/keyword heuristic (deterministic fallback).** A `FootprintMap` table keyed on package
   hints in the part name/specs: `0603` cap -> `C_0603_1608Metric`; `SOT-23` -> `SOT-23`;
   `DIP-8` -> `DIP-8_W7.62mm`; header pin count -> `PinHeader_1xNN_P2.54mm_Vertical`; common dev
   boards/modules (ESP32-WROOM, Pico, Uno) -> their known module footprints. This is the EE
   equivalent of `FirmwareBuilder.Fqbn()` keyword inference, and lives next to it in spirit.
3. **LLM-proposed footprint (gated).** When 1 and 2 miss, ask the model to propose a footprint id,
   then **validate it exists** in the KiCad libraries via `pcbnew` `FootprintEnumerate` /
   `FootprintLoad` before accepting. If load fails, that's a deterministic error fed back into the
   fix loop ("footprint X not found, choose another") — same generate/check/fix shape.

Pin-name -> pad-number reconciliation: Foundry nets use `alias.pinname` (e.g. `U1.SDA`). KiCad
footprints use pad numbers/names. We need a per-footprint **pin map** (reuse the existing
`Foundry.Core/Firmware/PinMap` concept) so net assignment targets the right pad. For modules whose
pin names match silkscreen (headers, dev boards) this is direct; for ICs we map logical pin -> pad
number via the component's pin table.

Footprints **are the dependency that makes or breaks v2.2** — budget real time here.

---

## 5. How the LLM contributes (and where it's fenced in)

The LLM does exactly one creative job: **placement reasoning**, expressed as structured data, never
raw geometry it can get subtly wrong.

Proposed LLM output = a `PlacementPlan` (JSON), not a board:
- `groups`: functional clusters (MCU + its decoupling, power section, RF/antenna, sensor block,
  connectors).
- per-component: target region/side, rotation, and **relational constraints** ("C3 within 2 mm of
  U1 pin VCC", "U2 antenna keep-out toward nearest board edge", "J1 connector on edge").
- board hints: rough size, layer count (2), preferred edge for connectors/USB, mounting-hole
  intent.

Deterministic code (`PcbPlacer`) turns that plan into actual coordinates with hard rules the LLM
can't violate:
- snap to grid, enforce courtyard non-overlap (collision resolution), keep parts inside the board
  outline, force connectors/antenna to edges, place decouplers on the same side as their pin.
- if the LLM omits/contradicts, deterministic defaults fill in (force-directed-ish spread by net
  connectivity). The board is always *producible* even from a weak plan.

Then the **gate**: `kicad-cli pcb drc`. Violations (clearance, unconnected, courtyard, edge) are
parsed to a `List<DrcViolation>` (mirror `BuildDiagnostic`) and fed back to the model as a fix
prompt — *the same generate -> check -> fix loop already used for firmware*, with a max-iteration
cap and "best board so far" retention so we never regress to nothing.

LLM never: routes traces, sets exact track widths, or emits Gerbers. Those are deterministic.

---

## 6. C# home & shape (mirrors existing idioms)

`Foundry.Core/Pcb/`:

- `KiCadInstaller.cs` — locate/download KiCad (kicad-cli + bundled Python). Mirrors
  `OpenScadInstaller` (PATH probe + `%LocalAppData%/Foundry/tools/kicad/`, on-demand download).
- `FreeRoutingInstaller.cs` — locate/download FreeRouting (+ bundled JRE). Mirrors OpenSCAD zip.
- `FootprintMap.cs` — deterministic `ComponentSpec -> footprint id` + pad pin-map (the §4 logic).
- `PcbBuilder.cs` — orchestrator, the `FirmwareBuilder` analogue: takes a `Project`, runs the
  pipeline, returns a `PcbResult` (Installed/Built/Routed/DrcClean/Ok/Summary/Diagnostics/artifact
  paths). Owns the generate->DRC->fix loop.
- `PlacementPlan.cs` — the LLM-facing DTO (§5).
- `PcbPlacer.cs` — deterministic plan -> coordinates + constraint enforcement.
- `KiCadScripts/` (embedded resources) — the pcbnew Python we ship: `build_board.py`,
  `apply_ses.py`, `export_dsn.py` (those `kicad-cli` can't do).
- `DrcReport.cs` / `DrcViolation.cs` — parsed DRC (mirror `BuildResult`/`BuildDiagnostic`).
- `Fab/JlcpcbClient.cs`, `Fab/PcbWayClient.cs` — optional order APIs.

UI: a new **PCB** tab (or sub-mode of the existing Wiring tab's SCHEMATIC|BREADBOARD toggle ->
add `PCB`) hosting a board renderer, driven by `ShellViewModel` + `TabDescriptor` with observable
`BadgeText`/`BadgeKind` ("DRC clean" / "3 violations") exactly like other tabs. The rendered board
is a renderer of the generated+verified `.kicad_pcb` (or its exported glb), consistent with how
`BreadboardControl`/`WiringDiagramControl` render generated data.

Process invocation everywhere reuses the `FirmwareBuilder.RunAsync` pattern (ProcessStartInfo,
redirected stdout/stderr, async wait, cancellation), and JSON parsing reuses the `Parse()` shape.

---

## 7. Phased plan (each phase independently shippable)

Versions align with the existing v2.x cadence.

### v2.2 — Netlist -> KiCad board with footprints + ratsnest (foundation)
- Add optional `Footprint` to the component/KB record; build `FootprintMap` (explicit -> heuristic).
- `KiCadInstaller` (locate/download). Populate `(footprint ...)` in `KiCadNetlist` export.
- `build_board.py`: create `.kicad_pcb`, load footprints, add nets, assign pads, **arbitrary/grid
  placement** (no intelligence yet), draw a default rectangular `Edge.Cuts` outline.
- Deliverable: open the generated `.kicad_pcb` in KiCad and see all parts with a correct ratsnest.
  **Shippable**: "Export to KiCad PCB" button. No routing, no DRC yet.

### v2.3 — LLM placement
- `PlacementPlan` prompt + `PcbPlacer` constraint enforcement (groups, decoupling-near-pin,
  connectors/antenna at edges, courtyard non-overlap, fit-in-outline).
- Auto-size board outline from placed extent.
- Deliverable: parts are sensibly arranged, not a grid dump. Visible in the new PCB tab render.
  **Shippable** even without routing — a placed board is already valuable.

### v2.4 — Autoroute via FreeRouting
- `FreeRoutingInstaller`; `export_dsn.py` (or `kicad-cli` dsn export); run jar headless; `apply_ses.py`.
- Deliverable: a routed 2-layer board (tracks + vias). Likely not DRC-perfect yet.
  **Shippable**: "Autoroute" produces copper.

### v2.5 — DRC + fix loop (the gate)
- `kicad-cli pcb drc --format json --exit-code-violations`; parse to `DrcViolation`s.
- Wire violations into the generate->check->fix loop: re-place / re-route on failure, max N
  iterations, keep best board. Re-uses the firmware loop's control flow.
- Deliverable: **a board that passes DRC.** This is the credibility milestone.

### v2.6 — Gerber + drill export
- `kicad-cli pcb export gerbers` + `export drill` (Excellon + map); zip in fab-friendly layout
  (JLCPCB/PCBWay naming). Optional `export glb` for the 3D preview.
- Deliverable: **downloadable fab package.** End of the "could be ordered" MVP.

### v2.7 — Fab order API (optional, opt-in)
- `JlcpcbClient` (primary): upload Gerber zip, auto-quote, place order, track. `PcbWayClient`
  secondary. User-supplied API keys, never stored in repo.
- Deliverable: one-click quote/order from inside Foundry.

---

## 8. Minimum viable "holy shit"

**A real 2-layer board, generated from a prompt, that passes `kicad-cli pcb drc` cleanly and exports
a Gerber+drill package a fab would accept.** That lands at **end of v2.6**. v2.5 (DRC-clean) is the
true credibility moment; v2.6 makes it tangible/orderable; v2.7 (one-click order) is the demo
flourish.

---

## 9. Risks & mitigations

- **Footprint coverage gaps** (biggest risk). Unknown part -> no footprint -> no board. Mitigate:
  layered fallback (§4), validate-then-accept LLM footprints, and a clear deterministic error in
  the fix loop. Start with a curated map covering Foundry's common parts (MCUs, regulators, common
  sensors, passives, headers, USB).
- **Pin-name -> pad-number mismatch** silently mis-wires copper. Mitigate: explicit per-footprint
  pin map, and a sanity check that every net node resolves to a real pad before routing.
- **FreeRouting flakiness / no convergence / Java env.** Mitigate: bundled-JRE installer, pass
  count caps, timeout + cancellation (RunAsync already supports `ct`), keep "best routed so far",
  and the Docker/API-server fallback documented. The DSN/SES seam lets us swap routers later.
- **kicad-cli surface drift between KiCad 8/9/10.** Pin a known-good KiCad version in the installer;
  feature-detect verbs; tolerate JSON shape changes in `Parse()` like the firmware parser does.
- **DRC fix loop divergence.** Cap iterations, retain best board, and treat "warnings only" as
  shippable (severity filtering via `--severity-error`), exactly like the firmware loop tolerates
  warnings.
- **GPL hygiene.** Never link/vendor KiCad or FreeRouting; subprocess + on-demand download only;
  attribution/licenses screen. (See §3.)
- **Performance.** Routing can take minutes; run off the UI thread (async, already the norm), show
  pipeline stages via `PipelineStage` like chat turns do, and allow cancel.

---

## 10. Sources

- [KiCad CLI reference (8.0)](https://docs.kicad.org/8.0/en/cli/cli.html) ·
  [KiCad CLI (9.0)](https://docs.kicad.org/9.0/en/cli/cli.html) ·
  [KiCad CLI (master)](https://docs.kicad.org/master/en/cli/cli.html)
- [pcbnew Python bindings (dev docs)](https://dev-docs.kicad.org/en/apis-and-binding/pcbnew/index.html) ·
  [Programmatic layout with KiCad + Python (Jeff McBride)](https://jeffmcbride.net/programmatic-layout-with-kicad-and-python/)
- [KiCad Specctra DB doxygen (DSN/SES LoadPCB/LoadSESSION/ExportPCB)](https://docs.kicad.org/doxygen/classDSN_1_1SPECCTRA__DB.html)
- [FreeRouting repo](https://github.com/freerouting/freerouting) ·
  [FreeRouting + KiCad usage (DSN/SES, CLI args)](https://freerouting.org/freerouting/using-with-kicad) ·
  [FreeRouting license: GPLv3](https://freerouting.org/freerouting/gpl-v3) ·
  [FreeRouting releases](https://github.com/freerouting/freerouting/releases)
- [JLCPCB API platform](https://api.jlcpcb.com/) ·
  [JLCPCB API: how to place an order](https://api.jlcpcb.com/help/article/how-do-i-place-an-order) ·
  [JLCPCB: generate Gerber/drill in KiCad 9](https://jlcpcb.com/help/article/how-to-generate-gerber-and-drill-files-in-kicad-9)
