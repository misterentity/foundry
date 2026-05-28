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

---

## v2.2 Build Recipe (concrete)

Scope for v2.2 **only**: netlist → `.kicad_pcb` with a footprint per component, every pad assigned to
the correct net, parts laid out on a simple grid, and a default rectangular `Edge.Cuts` outline. Open
it in KiCad → all parts present, ratsnest correct. **No autorouting, no DRC, no gerbers, no fab API.**

Home for all new code: `Foundry.Core/Pcb/`.

### A. Environment reality & the NotInstalled posture

KiCad is **not** installed on the dev machine, so nothing here runs pcbnew end-to-end locally. Two
hard rules (mirroring `FirmwareBuilder`):

1. Every external call degrades gracefully. `PcbBuilder.ExportAsync` returns
   `PcbResult.NotInstalled()` (same shape as `BuildResult.NotInstalled()`) when KiCad can't be located —
   the UI shows install guidance, never throws.
2. All real logic is **pure and unit-testable** without KiCad: the footprint map, the pin→pad map, the
   JSON job document we hand to the python script, and the parser for the script's JSON result are all
   string/object transforms with xUnit asserts. Only the actual pcbnew invocation is gated behind
   `KiCadInstaller.Locate() != null`; an integration test that builds a real board is guarded by that
   same check so the suite stays green here.

### B. The pcbnew Python API sequence (KiCad 8/9, SWIG `pcbnew` module)

This is what `KiCadScripts/build_board.py` (shipped as an embedded resource) does. The SWIG bindings
are deprecated-but-present through KiCad 9 (removal targeted ~KiCad 11) and remain the **only**
first-party way to programmatically build a board with footprints + nets + coordinates — `kicad-cli`
cannot synthesize a board from a netlist. Pin the recipe to KiCad 9 names; KiCad 8 differs only in the
plugin-manager class name (see note).

```python
import json, sys, pcbnew

job = json.load(open(sys.argv[1]))          # the JSON job document (see §C)
fp_dirs = job["footprintDirs"]               # e.g. ["C:/Program Files/KiCad/9.0/share/kicad/footprints"]

# 1. New empty board
board = pcbnew.BOARD()                        # in-memory board; or pcbnew.NewBoard(path) to back it with a file
# (LoadBoard/SaveBoard are the file equivalents; BOARD() + board.Save(path) at the end is cleanest.)

# 2. Create one NETINFO_ITEM per Foundry net, keep a name->net map.
nets = {}
for net in job["nets"]:                       # net["name"] is the Foundry net name (GND, +3V3, SDA, Net-(003)…)
    ni = pcbnew.NETINFO_ITEM(board, net["name"])
    board.Add(ni)
    nets[net["name"]] = ni

# 3. For each component: resolve its lib id "Lib:Footprint", load it, add it, set its reference.
for comp in job["components"]:
    lib, name = comp["footprint"].split(":", 1)      # "Resistor_SMD:R_0805_2012Metric"
    lib_dir = resolve_lib_dir(lib, fp_dirs)          # join a fp dir with lib + ".pretty"
    # KiCad 9: pcbnew.PCB_IO_MGR ; KiCad 8: pcbnew.IO_MGR  (FootprintLoad signature is identical)
    fp = pcbnew.PCB_IO_MGR.FootprintLoad(lib_dir, name)   # FOOTPRINT
    fp.SetReference(comp["ref"])                          # "U1", "R1", "J1" — = Foundry alias
    board.Add(fp)

    # 4. Assign each pad to its net using the precomputed pin-name -> pad mapping.
    for pad in fp.Pads():                                 # iterable of PAD
        padname = pad.GetName()                           # KiCad pad number/name, e.g. "1", "2", "VCC"
        netname = comp["padNets"].get(padname)            # padNets: {padName -> Foundry net name}
        if netname and netname in nets:
            pad.SetNet(nets[netname])                     # preferred; or pad.SetNetCode(nets[netname].GetNetCode())

    # 5. Grid placement (deterministic; no intelligence in v2.2).
    fp.SetPosition(pcbnew.VECTOR2I(pcbnew.FromMM(comp["x_mm"]), pcbnew.FromMM(comp["y_mm"])))
    if comp.get("rot"):
        fp.SetOrientationDegrees(comp["rot"])

# 6. Default rectangular board outline on Edge.Cuts (PCB_SHAPE in KiCad 8/9; was DRAWSEGMENT pre-7).
edge = pcbnew.Edge_Cuts                                   # layer id
for (x1, y1, x2, y2) in job["outlineSegments_mm"]:        # 4 segments of the rectangle
    seg = pcbnew.PCB_SHAPE(board)
    seg.SetShape(pcbnew.SHAPE_T_SEGMENT)
    seg.SetStart(pcbnew.VECTOR2I(pcbnew.FromMM(x1), pcbnew.FromMM(y1)))
    seg.SetEnd(pcbnew.VECTOR2I(pcbnew.FromMM(x2), pcbnew.FromMM(y2)))
    seg.SetLayer(edge)
    seg.SetWidth(pcbnew.FromMM(0.15))
    board.Add(seg)

# 7. Save.
board.Save(job["outPath"])                                # writes the .kicad_pcb
print(json.dumps({"ok": True, "out": job["outPath"],
                  "components": len(job["components"]), "nets": len(nets)}))
```

**API name cheat-sheet (verified KiCad 9 SWIG):**
| Action | Call |
|---|---|
| New board | `pcbnew.BOARD()` (or `pcbnew.NewBoard(path)`) |
| Save / load file | `board.Save(path)` / `pcbnew.LoadBoard(path)` (also `pcbnew.SaveBoard(path, board)`) |
| Load footprint from lib dir | KiCad 9 `pcbnew.PCB_IO_MGR.FootprintLoad(libDir, name)`; KiCad 8 `pcbnew.IO_MGR.FootprintLoad(...)` |
| Add item to board | `board.Add(item)` (footprint, net, shape) |
| Create a net | `pcbnew.NETINFO_ITEM(board, name)` then `board.Add(ni)` |
| Assign pad → net | `pad.SetNet(netinfo)` (or `pad.SetNetCode(netinfo.GetNetCode())`) |
| Iterate pads | `footprint.Pads()`; pad id via `pad.GetName()` |
| Set reference | `footprint.SetReference("U1")` |
| Position | `footprint.SetPosition(pcbnew.VECTOR2I(pcbnew.FromMM(x), pcbnew.FromMM(y)))` |
| Rotation | `footprint.SetOrientationDegrees(deg)` |
| Units | `pcbnew.FromMM(mm)` → internal nm int; `pcbnew.ToMM(...)` back |
| Edge.Cuts segment | `s = pcbnew.PCB_SHAPE(board); s.SetShape(pcbnew.SHAPE_T_SEGMENT); s.SetLayer(pcbnew.Edge_Cuts)` |

**Ratsnest:** confirmed implicit. The ratsnest is *rendered from connectivity*, not stored in the file.
It is derived entirely from pad→net membership when KiCad loads/computes connectivity
(`board.BuildConnectivity()` is available but not required for the saved file). So for v2.2 we only need
correct `pad.SetNet(...)` membership; opening the saved `.kicad_pcb` in KiCad shows the right ratsnest
automatically. No tracks are created — that is v2.4.

### C. Headless invocation (PcbBuilder)

`kicad-cli` **cannot** build a board from a netlist, so we run our `.py` against **KiCad's bundled
Python**, not via `kicad-cli`. That interpreter has `pcbnew` importable; a stock system Python does not.

- **Locate the interpreter (`KiCadInstaller.Locate`)** — mirror `FirmwareBuilder.Locate()`/`OpenScadInstaller`:
  1. PATH probe for `kicad-cli.exe` (confirms a KiCad install) and derive its `bin/` dir.
  2. Standard dir probe: `C:\Program Files\KiCad\<ver>\bin\` (e.g. `9.0`, `8.0`), newest first.
  3. The bundled python is `<kicad>\bin\python.exe` (verified: `C:\Program Files\KiCad\9.0\bin\python.exe`).
  Return `null` if none found. **Do NOT auto-download** — KiCad is a ~1 GB MSI, not a portable zip
  (unlike OpenSCAD). Surface clear install guidance + the documented URL
  (`https://www.kicad.org/download/windows/`); `KiCadInstaller` only *locates*.
- **Default footprint dir** (passed in the job as `footprintDirs`): `<kicad>\share\kicad\footprints`
  (verified default: `C:\Program Files\KiCad\9.0\share\kicad\footprints`), with the env var
  `KICAD9_FOOTPRINT_DIR` / `KICAD8_FOOTPRINT_DIR` as an override if set. Each library is a subdir
  `<Lib>.pretty`, so a lib id `Resistor_SMD:R_0805_2012Metric` resolves to
  `<dir>\Resistor_SMD.pretty` + footprint `R_0805_2012Metric`.
- **Run it (mirror `FirmwareBuilder.RunAsync`)**: write the job JSON + the embedded `build_board.py` to a
  temp dir, then
  `ProcessStartInfo { FileName = "<kicad>\bin\python.exe", Arguments = "\"build_board.py\" \"job.json\"" }`
  with `UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput/Error=true`, async
  `WaitForExitAsync(ct)`. Pass the netlist + footprint assignments **as a JSON job document** (path as
  argv[1]; large payloads via a temp file rather than stdin to dodge Windows arg/stdin limits).
- **Parse** the script's single-line JSON stdout into `PcbResult` exactly like `FirmwareBuilder.Parse()`
  (try-JSON, fall back to stderr scrape). Non-zero exit or `"ok": false` → diagnostics list
  (`PcbDiagnostic`, mirror `BuildDiagnostic`): "footprint X not found", "pad P unmapped", etc.

**Job document shape** (pure C# builds this from the `Project`; fully unit-testable):
```json
{
  "outPath": "C:/.../board.kicad_pcb",
  "footprintDirs": ["C:/Program Files/KiCad/9.0/share/kicad/footprints"],
  "outlineSegments_mm": [[0,0,80,0],[80,0,80,60],[80,60,0,60],[0,60,0,0]],
  "nets": [{"name":"GND"},{"name":"+3V3"},{"name":"SDA"},{"name":"Net-(003)"}],
  "components": [
    {"ref":"U1","footprint":"Package_QFP:LQFP-48_7x7mm_P0.5mm","x_mm":20,"y_mm":20,"rot":0,
     "padNets":{"1":"GND","2":"+3V3","15":"SDA"}},
    {"ref":"R1","footprint":"Resistor_SMD:R_0805_2012Metric","x_mm":40,"y_mm":20,"rot":0,
     "padNets":{"1":"SDA","2":"+3V3"}}
  ]
}
```
Net names come straight from `KiCadNetlist`'s existing union-find net model (GND/power/I²C naming) —
**reuse it; do not recompute nets.** Grid placement for v2.2: order components, lay them out left→right
on a fixed pitch (e.g. 12–15 mm), wrap into rows of N (e.g. ceil(sqrt(count))); size the rectangular
`Edge.Cuts` to the grid extent + margin. No collision logic yet (that's v2.3 `PcbPlacer`).

### D. Footprint library id map (`FootprintMap.cs`) — keyword heuristics

The EE analogue of `FirmwareBuilder.Fqbn()`: deterministic, keyword-driven, with a safe generic
fallback. Priority: (1) explicit `Footprint` field on the component/KB record; (2) this keyword map;
(3) [later phases] LLM-proposed, validate-then-accept. All names below are real KiCad standard-library
lib ids (`Lib:Footprint`), verified against the KiCad footprint libraries / KLC naming conventions.

| Part class | Keyword hints (in name/specs, case-insensitive) | Default lib id | Notes |
|---|---|---|---|
| Resistor (SMD) | "resistor", "res", "ohm", "0603"/"0805"/"1206" | `Resistor_SMD:R_0805_2012Metric` | swap size token if package given (0603→`R_0603_1608Metric`) |
| Resistor (THT) | "resistor" + "through-hole"/"axial"/"thru" | `Resistor_THT:R_Axial_DIN0207_L6.3mm_D2.5mm_P10.16mm_Horizontal` | |
| Capacitor (SMD) | "cap", "capacitor", "uF"/"nF"/"pF", size token | `Capacitor_SMD:C_0603_1608Metric` | size-token swap as above |
| Capacitor (electrolytic) | "electrolytic", "elec cap" | `Capacitor_THT:CP_Radial_D5.0mm_P2.50mm` | |
| LED (THT) | "led" (no SMD hint) | `LED_THT:LED_D5.0mm` | 3mm → `LED_D3.0mm` |
| LED (SMD) | "led" + "smd"/"0805"/"0603" | `LED_SMD:LED_0805_2012Metric` | |
| Diode (THT) | "diode", "1n400", "rectifier" | `Diode_THT:D_DO-41_SOD81_P10.16mm_Horizontal` | |
| Diode (SMD) | "diode" + "smd"/"sod" | `Diode_SMD:D_SOD-123` | |
| Transistor (SMD) | "transistor", "bjt", "mosfet", "sot-23" | `Package_TO_SOT_SMD:SOT-23` | |
| Transistor (THT) | "transistor" + "to-92"/"thru" | `Package_TO_SOT_THT:TO-92_Inline` | |
| Regulator (THT) | "regulator", "7805", "ldo", "to-220" | `Package_TO_SOT_THT:TO-220-3_Vertical` | |
| Regulator (SMD) | "regulator"/"ldo" + "sot-223"/"sot-23" | `Package_TO_SOT_SMD:SOT-223-3_TabPin2` | |
| Pin header | "header", "connector", "pins" + pin count N | `Connector_PinHeader_2.54mm:PinHeader_1x{N:00}_P2.54mm_Vertical` | N from pin count; 2-row → `PinHeader_2x{N}` |
| Screw terminal | "terminal block", "screw terminal" | `TerminalBlock_Phoenix:TerminalBlock_Phoenix_MKDS-1,5-2_1x02_P5.00mm_Horizontal` | |
| ESP32 module | "esp32", "wroom" | `RF_Module:ESP32-WROOM-32` | |
| ESP8266 | "esp8266", "esp-12", "nodemcu" | `RF_Module:ESP-12E` | dev board: generic header strip fallback |
| Arduino Uno-class | "uno", "atmega328p" + DIP | `Package_DIP:DIP-28_W7.62mm` | bare MCU; the *board* is headers, see fallback |
| Raspberry Pi Pico | "pico", "rp2040" | `RPi_Pico:RPi_Pico_SMD_TH` | (Module lib; if absent, 2x20 header) |
| IC, SOIC | "soic", "so-8" | `Package_SO:SOIC-8_3.9x4.9mm_P1.27mm` | pin count from KB |
| IC, DIP | "dip", "pdip" | `Package_DIP:DIP-8_W7.62mm` | pin count from KB |
| IC, QFP | "qfp", "lqfp", "tqfp" | `Package_QFP:LQFP-48_7x7mm_P0.5mm` | pin count/pitch from KB |
| **Generic fallback** | anything unmatched | `Connector_PinHeader_2.54mm:PinHeader_1x{N}_P2.54mm_Vertical` (N = pin count, ≥1) | a board is always *producible*: an unknown part becomes a pin-count-correct header so every net node still resolves to a pad. Emit a `PcbDiagnostic` warning so the fix loop can upgrade it later. |

Size-token rule: if the part name/specs contain an imperial passive size (`0402/0603/0805/1206`), swap
the size token in the chosen `R_/C_/LED_` id to the matching `*_{imperial}_{metric}Metric` name
(0402→1005, 0603→1608, 0805→2012, 1206→3216). Header pin count `N` comes from the component's pin
table (`ComponentSpec` pins / KB). This map lives in `FootprintMap.cs` next to where `Fqbn()` lives in
spirit — pure, table-driven, unit-tested with string asserts (no KiCad needed).

### E. Pin-name → pad-number mapping (`padNets`)

Foundry nets address endpoints as `alias.pinname` (e.g. `U1.SDA`, `J1.3`). KiCad pads are addressed by
`pad.GetName()` (numbers like `"1"` or named pads like `"VCC"`). v2.2 must produce, per component, a
`padNets: { padName -> netName }` map. Reuse the `PinMap`/`PinReport` pin/pad concepts:

1. **Numeric pins** (`J1.3`, `R1.2`): pin name is already the pad number → identity map. Covers passives,
   diodes, headers, most connectors. (Matches `KiCadNetlist.PinOf` which already defaults a missing pin
   to `"1"`.)
2. **Named pins that equal silkscreen** (modules/dev boards: `U1.VCC`, `U1.SDA`): if the footprint has a
   pad with that exact name, map directly. Module footprints (ESP32-WROOM, headers) name pads to match.
3. **Logical IC pins** (`U1.SDA` on a QFP/SOIC): map logical pin → pad number via the component's pin
   table order — reuse the KB pin list / `PinReport` ordering so pin *index* gives the pad number. This
   is the per-footprint **pin map** §4 calls for.
4. **Validation (pure, testable):** before emitting the job, assert every net node's `(alias,pin)`
   resolves to a `padName` in that component's map; an unresolved node becomes a `PcbDiagnostic`
   ("net X node U1.SDA: no pad") rather than a silent mis-wire — same generate→check→fix shape.

Building `padNets` is pure C# over the existing `Project` model + KB pin tables — fully unit-testable
without KiCad. The actual `pad.GetName()` reconciliation (case 2/3 against the *real* footprint pads) is
verified inside `build_board.py` at load time and any mismatch is reported back in the result JSON.

### F. New code (v2.2 only)

- `Foundry.Core/Pcb/KiCadInstaller.cs` — locate kicad-cli + bundled `bin/python.exe` + footprint dir
  (no download; install guidance). Mirrors `OpenScadInstaller`/`FirmwareBuilder.Locate`.
- `Foundry.Core/Pcb/FootprintMap.cs` — §D map + size-token logic (pure).
- `Foundry.Core/Pcb/PcbJob.cs` — the §C job-document DTO + builder from `Project` (reuses
  `KiCadNetlist` nets + §E `padNets`) (pure).
- `Foundry.Core/Pcb/PcbResult.cs` / `PcbDiagnostic.cs` — mirror `BuildResult`/`BuildDiagnostic`, with
  `NotInstalled()`/`Skipped()`.
- `Foundry.Core/Pcb/PcbBuilder.cs` — orchestrator: locate → build job → run bundled python on
  `build_board.py` → parse (mirror `FirmwareBuilder`). `NotInstalled()` when KiCad absent.
- `Foundry.Core/Pcb/KiCadScripts/build_board.py` — embedded resource (§B).
- Populate `(footprint "...")` in `KiCadNetlist` from `FootprintMap` (currently writes `""`).
- UI: an **"Export to KiCad PCB"** action next to the existing Wiring-tab **KICAD** netlist button,
  same wiring/placement.

Tests (`Foundry.Tests`, all KiCad-free): `FootprintMap` keyword→id asserts incl. size-token + generic
fallback; `PcbJob` builder (net set, `padNets`, grid coords, outline) from a sample `Project`; `padNets`
validation flags an unresolved node; `PcbResult.Parse` for ok/error/not-installed; an integration test
that actually invokes `build_board.py` guarded by `if (KiCadInstaller.Locate() is null) return;`.

---

## v2.3 LLM Placement (concrete)

Scope for v2.3 **only**: replace v2.2's naive grid with **AI-informed placement that still produces a
guaranteed-valid, non-overlapping board**, while keeping the `PcbJob`/`PcbJobComponent` output contract
byte-for-byte unchanged (so `build_board.py` + `PcbBuilder` consume it exactly as today). The LLM is
**fenced to placement intent only**: it proposes functional groups + relative/edge intent; the
deterministic `PcbPlacer` owns every coordinate and can never emit an overlapping or off-board layout.
Same posture as the rest of Foundry: **AI proposes, deterministic geometry disposes.** KiCad is NOT
required for any of this — placement math is pure C#, fully unit-tested against a fixture
`PlacementPlan` JSON (mirroring how `ProjectGenerator` is tested on a fixture design).

Three new pure files + two touched files:
- `Foundry.Core/Pcb/PlacementPlan.cs` — the LLM-facing DTO + tolerant JSON parser (new).
- `Foundry.Core/Pcb/PcbPlacer.cs` — deterministic plan → coordinates + outline (new, pure).
- `Foundry.Core/Pcb/PcbPlanner.cs` — the structured Claude call mirroring `ProjectGenerator` (new).
- `Foundry.Core/Pcb/FootprintMap.cs` — add `CourtyardOf(libId)` approximate-size table (touched).
- `Foundry.Core/Pcb/PcbJob.cs` — `Build` takes an optional `PlacementPlan` and delegates placement to
  `PcbPlacer`; the grid path becomes `PcbPlacer`'s empty-plan default (touched).

### 1. The `PlacementPlan` contract

Minimal, every field optional, sane defaults — a sparse or garbled AI reply still yields a valid board
because the placer treats a missing plan as "tidy grid" (= v2.2 behavior). The plan is **advice keyed
by component ref/alias**, never coordinates.

```csharp
namespace Foundry.Core.Pcb;

public enum EdgeAffinity { None, Left, Right, Top, Bottom }

/// <summary>One functional cluster the AI proposes (e.g. "power", "mcu", "sensor-i2c", "connectors").</summary>
public sealed record PlacementGroup(
    string Id,                       // "power" | "mcu" | "sensor-i2c" | "connectors" | "rf" | ...
    IReadOnlyList<string> Members,   // component refs/aliases ("U1","C1","J1") — matched case-insensitively
    EdgeAffinity Edge = EdgeAffinity.None);  // whole-group edge intent (e.g. connectors group → Left)

/// <summary>Per-component placement intent. All fields optional; defaults make a sparse hint safe.</summary>
public sealed record PlacementHint(
    string Ref,                      // the component this hint targets ("C3")
    string? Group = null,            // group id it belongs to (redundant with PlacementGroup.Members; either works)
    EdgeAffinity Edge = EdgeAffinity.None,  // connector/antenna edge pin (overrides its group's edge)
    string? NearRef = null,          // "keep adjacent to <ref>" — decoupling cap next to its IC power pin
    double Rotation = 0);            // coarse rotation hint in degrees (snapped to 0/90/180/270 by the placer)

/// <summary>
/// The AI's placement proposal — functional grouping + relative/edge intent + a coarse region order.
/// Pure advice; <see cref="PcbPlacer"/> turns it into collision-free mm coordinates. Empty = tidy grid.
/// </summary>
public sealed record PlacementPlan(
    IReadOnlyList<PlacementGroup> Groups,
    IReadOnlyList<PlacementHint> Hints,
    IReadOnlyList<string> RegionOrder)   // left→right order of group ids, e.g. ["power","mcu","sensor-i2c","connectors"]
{
    public static PlacementPlan Empty { get; } = new(
        Array.Empty<PlacementGroup>(), Array.Empty<PlacementHint>(), Array.Empty<string>());

    /// <summary>
    /// Tolerant parse of the AI's JSON (same defensive style as ProjectGenerator.ExtractJson + Map).
    /// Unknown enum strings → None; missing arrays → empty; any exception → <see cref="Empty"/>.
    /// NEVER throws — a malformed plan degrades to the tidy-grid default, never a broken board.
    /// </summary>
    public static PlacementPlan Parse(string? json) { /* JsonDocument, defensive readers, try/catch → Empty */ }
}
```

**The JSON the AI returns** (the contract in the system prompt; note it carries NO coordinates):

```json
{
  "groups": [
    {"id": "power",        "members": ["U2","C1","C2","D1"], "edge": "none"},
    {"id": "mcu",          "members": ["U1","C3","C4"],      "edge": "none"},
    {"id": "sensor-i2c",   "members": ["U3","R1","R2"],      "edge": "none"},
    {"id": "connectors",   "members": ["J1","J2"],           "edge": "left"},
    {"id": "rf",           "members": ["ANT1"],              "edge": "top"}
  ],
  "hints": [
    {"ref": "C3",  "near": "U1"},
    {"ref": "C4",  "near": "U1"},
    {"ref": "C1",  "near": "U2"},
    {"ref": "J1",  "edge": "left"},
    {"ref": "ANT1","edge": "top", "rotation": 0}
  ],
  "regionOrder": ["power", "mcu", "sensor-i2c", "connectors"]
}
```

Robustness rules baked into `Parse`:
- Any missing top-level key → empty list. Unknown `edge` value → `None`. Non-numeric `rotation` → 0.
- A ref in a group/hint that doesn't exist in the project is silently ignored at placement time.
- A ref omitted from every group/hint is still placed (the placer assigns it to a synthetic
  `"_unassigned"` group). **No component is ever dropped.**

### 2. The deterministic `PcbPlacer` algorithm

Input: the project's `PcbJobComponent` list **without positions** (ref + footprint + padNets), the
`PlacementPlan`, and `FootprintMap.CourtyardOf(libId)` sizes. Output: the same components **with**
`XMm/YMm/Rot` set, plus the rectangular `OutlineSegmentsMm` sized to fit. Guaranteed non-overlapping
even when the plan is `Empty`.

```
PlaceResult Place(IReadOnlyList<PlacedItem> items, PlacementPlan plan, double marginMm = 5, double gapMm = 1.5)
  PlacedItem = { Ref, LibId, Wmm, Hmm (from CourtyardOf), Rot }
```

Algorithm (all units mm, all coordinates deterministic — sort everything by ref for stable output):

1. **Resolve courtyard size per item.** `(w,h) = FootprintMap.CourtyardOf(libId)`; if rotation hint is
   90/270, swap w/h. Pad each footprint's effective box by `gapMm` on every side so packing leaves
   clearance → guaranteed courtyard separation without any collision-resolution pass.

2. **Assign each item to a group.** Build ref→group from `plan.Groups[].Members` and `plan.Hints[].Group`
   (hint wins on conflict). Unassigned refs → group `"_unassigned"`. If the plan is empty, ALL items
   land in one `"_unassigned"` group → step 6 degrades to a single tidy grid = exact v2.2 behavior.

3. **Order the groups into regions (left→right).** Use `plan.RegionOrder` first, then any remaining
   group ids alphabetically, with `"_unassigned"` last. Edge-affinity groups (connectors/RF) are pulled
   out of the region flow and handled in step 5.

4. **Pack each non-edge group into a shelf/row bin** (classic shelf bin-packing, deterministic):
   - Target each group's internal width to a square-ish aspect: `groupW ≈ sqrt(Σ area) * 1.3`.
   - Lay items left→right on the current shelf; when the next item would exceed `groupW`, start a new
     shelf at `y += currentShelfHeight + gap`. Shelf height = max item height on that shelf.
   - **Decoupling "near <ref>" override:** before shelf-packing, items with `NearRef` set are pulled
     out and queued to be placed immediately adjacent (right side, then top) of their target's final
     box, inside the target's group — so a cap sits against its IC. If the target is in another group,
     the cap is moved into the target's group first. Adjacency is collision-checked against already
     placed boxes in that group; if the preferred slot is occupied, try the other three sides, else
     fall back to the group's normal shelf flow (never overlap).
   - Result per group: a local bounding box `(gw, gh)` and each member's local `(x,y)`.

5. **Lay out regions + pin edge groups.**
   - Non-edge group boxes are placed left→right in region order, each at `x = cursorX`, vertically
     centered in the board band; `cursorX += gw + regionGap`.
   - Compute the provisional board extent from the packed regions.
   - Edge-affinity groups/items are then pinned to the named board edge: Left/Right → packed into a
     vertical strip flush to that edge (x just inside the margin); Top/Bottom → a horizontal strip.
     Per-item `Edge` (hint) overrides its group's edge. Antennas/RF with `Top` go to the top strip.
     The board extent is grown if an edge strip needs more room — edge items are always reachable and
     never overlap interior regions (the strip reserves its own band).

6. **Empty-plan fast path (tidy grid).** When every item is `"_unassigned"` and no edge/near hints
   exist, skip region logic and lay items on a single shelf grid of `cols = ceil(sqrt(n))` using the
   same packing primitive — identical visual result to v2.2's grid, but now courtyard-aware (no two
   parts can touch). This guarantees the degrade path is a strict superset of v2.2.

7. **Size the outline.** `boardW = maxX + margin`, `boardH = maxY + margin` over all placed boxes
   (including edge strips); emit the 4-segment closed rectangle exactly like v2.2
   (`[0,0,w,0],[w,0,w,h],[w,h,0,h],[0,h,0,0]`). Snap all coordinates to 0.05 mm so output is stable.

**Non-overlap proof sketch (why it's always valid):** every item is packed as a `gap`-inflated box, and
the only placement primitives are (a) shelf packing, which never reuses x-ranges on a shelf and advances
y past the tallest box, and (b) edge strips, which occupy reserved bands the interior regions are pushed
clear of. "near" items are explicitly collision-checked against placed boxes before commit. There is no
code path that places two inflated boxes with overlapping rectangles, so courtyards (the un-inflated
inner boxes) are always strictly separated by ≥ `2*gap`.

`CourtyardOf` table to add to `FootprintMap` (approximate W×H mm, courtyard-ish; reasonable estimates,
keyed by a coarse match on the lib id so it covers the ids `Resolve` actually produces):

| Footprint class (lib id contains) | Approx W×H (mm) | Notes |
|---|---|---|
| `R_0402`/`C_0402`/`LED_0402` | 1.0 × 0.5 | imperial-size passive bodies |
| `R_0603`/`C_0603`/`LED_0603` | 1.6 × 0.8 | |
| `R_0805`/`C_0805`/`LED_0805` (default passive) | 2.0 × 1.25 | |
| `R_1206`/`C_1206` | 3.2 × 1.6 | |
| `R_Axial`/`CP_Radial`/`D_DO-41`/`LED_D5.0mm`/`LED_D3.0mm` | 7.0 × 3.0 | THT passives/LEDs |
| `D_SOD-123` | 2.7 × 1.6 | |
| `SOT-23` | 3.0 × 3.0 | |
| `SOT-223` | 7.0 × 7.0 | tab regulator |
| `TO-220` | 10.0 × 4.5 | vertical reg footprint |
| `TO-92` | 5.0 × 5.0 | |
| `SOIC-N` | (N/2)*1.27 + 2 × 6.0 | scale length by pad count |
| `DIP-N` | (N/2)*2.54 + 3 × 9.0 | |
| `LQFP-N` / QFP | side ≈ sqrt(N)*1.6 + 8 (square) | coarse |
| `PinHeader_1xN` | N*2.54 × 2.54 | the generic fallback + headers |
| `PinHeader_2xN` | N*2.54 × 5.08 | |
| `TerminalBlock` | 10.0 × 8.0 | |
| `ESP32-WROOM` | 18.0 × 25.5 | |
| `ESP-12E` | 16.0 × 24.0 | |
| `RPi_Pico` | 21.0 × 51.0 | |
| **unmatched** | 10.0 × 10.0 | safe generous default (never 0) |

`CourtyardOf` is pure + table-driven, parses the `1xNN`/`SOIC-N`/`DIP-N`/`LQFP-N` count out of the id
with the same regex idiom `FootprintMap` already uses, and returns a `(double WMm, double HMm)` tuple
— **fully unit-testable with string asserts, no KiCad.**

### 3. The AI call — `PcbPlanner` (mirrors `ProjectGenerator`)

A new `PcbPlanner` class taking `IAnthropicClient` + model id in its ctor (identical shape to
`ProjectGenerator`). One method, one structured call, defensive parse, offline fallback:

```csharp
public sealed class PcbPlanner
{
    private readonly IAnthropicClient _ai;
    private readonly string _model;
    public PcbPlanner(IAnthropicClient ai, string? model = null) { ... }   // same as ProjectGenerator

    /// <summary>
    /// Ask the model for a PlacementPlan for this design. No key / any failure / unparseable reply →
    /// <see cref="PlacementPlan.Empty"/> (deterministic tidy-grid default). NEVER throws.
    /// </summary>
    public async Task<PlacementPlan> PlanAsync(Project.Project project, CancellationToken ct = default)
    {
        if (!_ai.HasKey) return PlacementPlan.Empty;               // offline fallback — same gate idiom
        try
        {
            var user = BuildUserPrompt(project);                   // parts + nets summary, like EnrichFirmwareAsync
            var raw  = await _ai.CompleteAsync(SystemPrompt, user, _model, ct);
            return PlacementPlan.Parse(ExtractJson(raw));          // Parse already degrades to Empty on garbage
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("pcb", $"placement plan failed: {ex.Message} — using tidy-grid default");
            return PlacementPlan.Empty;
        }
    }
}
```

The user prompt reuses the `EnrichFirmwareAsync` summarization shape — a compact parts list (ref, name,
resolved footprint, pin count) and the netlist (`from -> to [net]`), so the model reasons about real
groupings and which caps decouple which ICs.

**System prompt (senior PCB-layout-engineer, fenced to intent):**

```
You are a senior PCB layout engineer. You are given a parts list and a netlist. Propose a PLACEMENT
PLAN as ONE JSON object — functional groups and relative/edge INTENT only. You do NOT output
coordinates; a deterministic placer turns your plan into exact positions and guarantees no overlaps.

Apply these layout principles:
- Group parts by FUNCTION: power/regulation, the MCU and its support, each sensor/peripheral block
  (name I2C/SPI blocks by bus), and connectors. One group per function.
- Put every decoupling/bypass capacitor in the SAME group as the IC it serves and set its
  "near" to that IC's ref — caps must sit directly against their IC's power pin.
- Put CONNECTORS, USB, power input, and ANTENNAS/RF at a BOARD EDGE (set "edge"): connectors on a
  side edge, antenna/RF on the nearest edge (prefer top), pointing outward.
- Keep high-speed / bus nets (I2C, SPI, crystal) short: place those parts adjacent within one group.
- Keep noisy power/switching away from sensitive analog/RF: order regions so power is at one end and
  RF/analog at the other.
- Every component ref in the parts list must appear in exactly one group.

Return ONLY this JSON (no prose, no fences). All fields optional except group "id" and "members":
{"groups":[{"id":"power","members":["U2","C1"],"edge":"none"}],
 "hints":[{"ref":"C3","near":"U1"},{"ref":"J1","edge":"left"}],
 "regionOrder":["power","mcu","sensor-i2c","connectors"]}
edge ∈ none|left|right|top|bottom. rotation ∈ 0|90|180|270 (optional).
```

**Offline fallback is the heart of testability:** no key → `PlacementPlan.Empty` → `PcbPlacer` tidy
grid → v2.2 board. Tests never need a live key; they exercise (a) `PlacementPlan.Parse` on fixture
JSON (well-formed, sparse, and garbage cases), and (b) `PcbPlacer.Place` against fixture plans, asserting
non-overlap + edge pinning + near-adjacency + correct degrade-to-grid — exactly mirroring how
`GenerationTests` feed `ProjectGenerator` a fixture via `FakeAi`.

### 4. How `PcbJob.Build` switches grid → placer WITHOUT changing the `PcbJobComponent` contract

`PcbJobComponent` (ref, footprint, x_mm, y_mm, rot, padNets) and the whole `PcbJob` shape are
**unchanged** — only the source of the x/y/rot numbers changes. `Build` gains an optional plan param
(defaulted, so every existing call site and test compiles untouched):

```csharp
public static PcbJob Build(Project.Project project, string outPath,
                           IReadOnlyList<string> footprintDirs,
                           PlacementPlan? plan = null)
{
    // ... identical net/ref/footprint/padNets resolution as v2.2 (unchanged) ...
    // Build placer items from the same resolved (ref, libId) pairs:
    var items = refs.Select(alias => new PcbPlacer.PlacedItem(
        alias, choiceByRef[alias].LibId, FootprintMap.CourtyardOf(choiceByRef[alias].LibId), rotHint: 0)).ToList();

    var placement = PcbPlacer.Place(items, plan ?? PlacementPlan.Empty);   // pure, deterministic

    var components = refs.Select(alias =>
    {
        var pos = placement[alias];                       // (XMm, YMm, Rot)
        return new PcbJobComponent(alias, choiceByRef[alias].LibId, pos.XMm, pos.YMm, pos.Rot,
                                   FootprintMap.PadNets(alias, endpointNets));
    }).ToList();

    var outline = placement.OutlineSegmentsMm;            // sized by the placer, same 4-segment shape
    // ... same net-node validation + diagnostics + return as v2.2 ...
}
```

- `plan == null` (or `Empty`) → placer's tidy-grid path → **identical output to v2.2**, so the existing
  `PcbJobTests` (distinct grid positions, rectangular outline, net/padNets asserts) keep passing.
- `PcbBuilder.BuildAsync` gains an optional `IAnthropicClient`: if supplied and keyed, it calls
  `await new PcbPlanner(ai, model).PlanAsync(project, ct)` and passes the plan into `PcbJob.Build`;
  otherwise it passes `null`. The python script + result parsing are **completely untouched** — they
  still read `x_mm/y_mm/rot/padNets` exactly as before. `build_board.py` requires NO changes.

### 5. Tests (all KiCad-free, mirror `GenerationTests`)

- `PlacementPlanTests`: `Parse` of the well-formed fixture (groups/hints/regionOrder populated);
  `Parse` of a sparse plan (only groups); `Parse` of garbage/empty/null → `PlacementPlan.Empty`;
  unknown `edge` → `None`.
- `PcbPlacerTests`: non-overlap over a fixture plan (assert every pair of inflated boxes is disjoint);
  empty plan → grid with distinct positions (matches v2.2 invariant); edge-affinity item lands flush to
  the named edge; "near" cap is adjacent (centre-distance ≤ courtyard half-sum + gap + ε) to its IC;
  every input ref appears exactly once in the output (nothing dropped); outline contains all boxes.
- `PcbPlannerTests`: `FakeAi` returning a fixture plan JSON → expected `PlacementPlan`; no-key stub →
  `PlacementPlan.Empty`; garbage reply → `PlacementPlan.Empty`.
- `PcbJobTests` (existing) stay green: `Build` with no plan == v2.2 grid; add one asserting `Build` with
  a fixture plan still serializes the identical `PcbJobComponent` shape (same keys, just different x/y).
- `FootprintMapTests`: `CourtyardOf` returns sane sizes per class incl. `1xNN`/`SOIC-N`/`DIP-N` count
  scaling and the generous unmatched default (never 0×0).

---

## v2.4 Autoroute Recipe (concrete)

Scope for v2.4 **only**: take the placed, net-assigned `.kicad_pcb` that `PcbBuilder.BuildAsync` already
produces (v2.2 geometry + v2.3 AI placement) and add **copper tracks** — export Specctra DSN → run
FreeRouting headless → import the routed SES back → save a routed `.kicad_pcb`. **No DRC (v2.5), no
gerbers (v2.6).** The deliverable is "Autoroute produces copper," likely not DRC-perfect.

Home for all new code: `Foundry.Core/Pcb/`. Same posture as v2.2/v2.3: every external call degrades to
`NotInstalled()`; all logic (command construction, installer locate, result/SES parsing) is pure and
unit-testable; the one true integration test is guarded by
`KiCadInstaller.Locate() != null && FreeRoutingInstaller.LocateJava() != null && jar present`.

### A. Environment reality

Neither **KiCad** nor **Java** is installed on the dev machine, so nothing here routes end-to-end
locally. Hard rules (mirror `FirmwareBuilder` / v2.2):
1. `RouteAsync` returns `RouteResult.NotInstalled()` (same shape as `PcbResult.NotInstalled()`) when
   KiCad's bundled python OR Java OR the FreeRouting jar can't be located — the UI shows install/download
   guidance, never throws.
2. The only KiCad-dependent steps are the two pcbnew python invocations (DSN export, SES import); the only
   Java-dependent step is the jar run. Everything else — command construction, installer locate, the
   FreeRouting log/`-drc` JSON parse — is pure string/object transforms with xUnit asserts.

### B. Step 1 — Specctra DSN export from the placed `.kicad_pcb`

**Decision: use pcbnew Python, NOT `kicad-cli`.** Confirmed against the KiCad **master/10** CLI reference:
`kicad-cli pcb export` has verbs for gerbers, drill, dxf, gencad, glb, step, svg, pos, ipc2581, ipcd356,
odb, vrml, stats, etc. — **there is NO `specctra`/`dsn` export verb and NO SES import verb at all**
(checked KiCad 9 and master). So DSN export and SES import **must** go through the SWIG `pcbnew` module,
exactly like `build_board.py`. This also keeps the dependency surface a single `KiCadInstaller`
(`<kicad>\bin\python.exe`), no second tool.

The exact module-level functions (verified in the official pcbnew python namespace doxygen) are:
- `ExportSpecctraDSN(*args)` — overloaded. The standalone overload operates on a `BOARD` (the form that
  does **not** need a `PCB_EDIT_FRAME`, so it works in a headless script). Returns `bool`.
- `ImportSpecctraSES("wxString" aFullFilename)` — operates on the **current/loaded board**. Returns `bool`.
- `LoadBoard(*args)` → `BOARD*`; `SaveBoard("wxString" aFileName, "BOARD" aBoard, "bool" aSkipSettings=False)` → `bool`.

Because `ExportSpecctraDSN`'s in-frame overload exists and historically the bare module call has been
flaky across versions ("`module has no attribute ExportSpecctraDSN`" reports on some nightlies), the
script must (a) `LoadBoard` first so a board exists, (b) pass that board to the export call, and (c)
`hasattr`-guard the call and report a clean diagnostic if the binding is missing in the installed KiCad.

`KiCadScripts/export_dsn.py` (new embedded resource):

```python
import json, sys, pcbnew

job = json.load(open(sys.argv[1]))          # {"in": "...board.kicad_pcb", "dsn": "...board.dsn"}
board = pcbnew.LoadBoard(job["in"])         # BOARD with placed footprints + assigned nets (v2.2/v2.3 output)

if not hasattr(pcbnew, "ExportSpecctraDSN"):
    print(json.dumps({"ok": False, "stage": "dsn", "error": "ExportSpecctraDSN not in this KiCad's pcbnew"}))
    sys.exit(2)

# Standalone overload: BOARD + filename (does not need a PCB_EDIT_FRAME).
ok = pcbnew.ExportSpecctraDSN(board, job["dsn"])
print(json.dumps({"ok": bool(ok), "stage": "dsn", "dsn": job["dsn"]}))
sys.exit(0 if ok else 1)
```

Invoked exactly like `build_board.py` (`FirmwareBuilder.RunAsync` pattern):
`<kicad>\bin\python.exe export_dsn.py job.json`, redirected stdout/stderr, async `WaitForExitAsync(ct)`,
parse the single-line JSON. (KiCad 8 vs 9: the call name is identical; only `build_board.py`'s
`PCB_IO_MGR`/`IO_MGR` differed — Specctra export does not.)

### C. Step 2 — FreeRouting headless

**Version: FreeRouting v2.2.4** (latest release, published 2026-05-13).
**Jar download URL** (single file, on-demand download like the OpenSCAD zip / `FirmwareBuilder.DownloadCliAsync`):
`https://github.com/freerouting/freerouting/releases/download/v2.2.4/freerouting-2.2.4.jar` (~58 MB).
Releases index: `https://github.com/freerouting/freerouting/releases`.

**Java requirement: JRE 21+ (the project builds/runs on Java 25; 21 is the floor).** Java is **locate-only**
(no auto-install): probe `JAVA_HOME\bin\java(.exe)` then PATH, mirroring how OpenSCAD/arduino-cli are
located. If no Java ≥ 21 is found → `RouteResult.NotInstalled()` with guidance to install a JRE 21+.
(Note: FreeRouting also ships per-OS installers with a bundled JRE — `freerouting-2.2.4-windows-x64.msi`
etc. — but for Foundry we follow the "download the single jar, locate system Java" seam; the bundled-JRE
installer is the documented fallback if the user has no Java.)

**Headless CLI invocation** (verified against the current `docs/command_line_arguments.md`; FreeRouting 2.x
runs headless without a display when the GUI is disabled):

```
java -jar freerouting-2.2.4.jar --gui.enabled=false -de board.dsn -do board.ses -mp <passes> -mt <threads>
```

Flags (current 2.2.x):
- `--gui.enabled=false` — disables the GUI for true headless / no-display operation. **Required** here.
- `-de <file.dsn>` — design input (loads the DSN; can also take `dsn+ses+rules`).
- `-do <file.ses>` — design output; `.ses` extension → writes a Specctra session file (the routed result).
- `-mp <n>` — upper limit on autorouter passes (e.g. 10). Caps runtime.
- `-mt <n>` — optimizer thread count.
- `--logging.console.level=INFO` (or `--logging.file.enabled=true`) — controls the log we parse for outcome (§E).
- `-drc <file.json>` — **bonus**: FreeRouting can itself write a DRC report in KiCad JSON format; we do NOT
  use it as the gate in v2.4 (that's v2.5/`kicad-cli pcb drc`), but it's a cheap secondary signal of
  unrouted/violation count and worth capturing into the result if present.

`FreeRoutingInstaller.cs` (new): `Locate()` (jar in `%LocalAppData%/Foundry/tools/freerouting/`),
`DownloadJarAsync()` (single-file GET of the URL above, mirrors `FirmwareBuilder.DownloadCliAsync`),
`LocateJava()` (JAVA_HOME→PATH, parse `java -version` ≥ 21). The run reuses `RunAsync` with `ct` + a
timeout (routing can take minutes) and "keep best SES so far"/cancellation per the risk plan.

### D. Step 3 — SES import back into the board, then save

Again pcbnew Python (no `kicad-cli` verb exists). `ImportSpecctraSES(aFullFilename)` reads the `.ses` and
**relocates modules and replaces all vias and tracks** on the current board; we then `SaveBoard` to the
routed output path.

`KiCadScripts/apply_ses.py` (new embedded resource):

```python
import json, sys, pcbnew

job = json.load(open(sys.argv[1]))          # {"in": "...board.kicad_pcb", "ses": "...board.ses", "out": "...board_routed.kicad_pcb"}
board = pcbnew.LoadBoard(job["in"])         # the SAME placed board the DSN was exported from

if not hasattr(pcbnew, "ImportSpecctraSES"):
    print(json.dumps({"ok": False, "stage": "ses", "error": "ImportSpecctraSES not in this KiCad's pcbnew"}))
    sys.exit(2)

# ImportSpecctraSES applies to the current/loaded board. Frame-free standalone form: pass the board.
ok = pcbnew.ImportSpecctraSES(board, job["ses"])   # falls back to pcbnew.ImportSpecctraSES(job["ses"]) if single-arg binding

# Outcome metrics for RouteResult (see §E): unconnected nets remaining after routing.
board.BuildConnectivity()
unconnected = board.GetUnconnectedNetCount()
tracks = sum(1 for _ in board.GetTracks())          # tracks + vias after import (0 before routing)

pcbnew.SaveBoard(job["out"], board)
print(json.dumps({"ok": bool(ok), "stage": "ses", "out": job["out"],
                  "unconnected": int(unconnected), "tracks": int(tracks)}))
sys.exit(0 if ok else 1)
```

`board.GetUnconnectedNetCount()` and `board.BuildConnectivity()` are confirmed pcbnew BOARD methods;
`BuildConnectivity()` must be called before the count is valid. Note `ImportSpecctraSES` argument arity
has varied across versions (frame-bound single-filename form vs standalone board+filename form), so the
script attempts the board+filename overload and falls back to the single-arg form — both reported cleanly.

### E. Step 4 — Reading the routing outcome (RouteResult)

Report **"fully routed" vs "N nets unrouted"** from two independent signals, preferring the deterministic
board-derived one:

1. **Authoritative (post-import, board-derived):** in `apply_ses.py`, after `BuildConnectivity()`, read
   `board.GetUnconnectedNetCount()`. `0` ⇒ **fully routed**; `N>0` ⇒ **N nets unrouted**. Also emit the
   track count (was 0 before routing; >0 confirms copper was actually applied). This is the source of
   truth because it reflects what actually landed on the saved `.kicad_pcb`, independent of FreeRouting's
   self-report. (Compare against the pre-route ratsnest count captured right after `BuildAsync` to report
   "routed M of M+N connections.")
2. **Secondary (FreeRouting log):** parse the jar's stdout/log (`--logging.console.level=INFO`) for its
   completion summary — FreeRouting prints incomplete/unrouted counts and pass progress; and if `-drc
   <json>` was passed, parse that KiCad-format JSON for an `unconnected`/violation count. Used to enrich
   the message ("FreeRouting: 12 passes, 0 incomplete") and as a fallback if the board query is
   unavailable.

`RouteResult.cs` (mirror `PcbResult`): `Installed`, `Routed` (bool), `FullyRouted` (== `Unconnected==0`),
`Unconnected` (int), `TrackCount` (int), `RoutedPcbPath`, `DsnPath`, `SesPath`, `Summary`
("Fully routed (32 tracks)" / "27 tracks, 3 nets unrouted"), `Diagnostics` (`List<PcbDiagnostic>`),
`NotInstalled()`/`Skipped()`. The stdout/JSON parsers are pure → unit-tested on captured fixture logs/JSON
with no KiCad/Java present.

### F. Orchestration & new code (v2.4 only)

`PcbBuilder.RouteAsync(string placedPcbPath, RouteOptions opts, CancellationToken ct)` (or a sibling
`PcbRouter`) chains: locate (KiCad python + Java + jar; download jar on demand) → temp workdir →
`export_dsn.py` → `java -jar … --gui.enabled=false -de … -do … -mp …` → `apply_ses.py` → parse → `RouteResult`.
Any missing dependency short-circuits to `NotInstalled()`; any non-zero exit / `"ok": false` becomes a
`PcbDiagnostic`. New files:
- `Foundry.Core/Pcb/FreeRoutingInstaller.cs` — locate/download jar + locate Java ≥ 21 (mirrors `OpenScadInstaller` + `FirmwareBuilder.DownloadCliAsync`).
- `Foundry.Core/Pcb/RouteResult.cs` — the result DTO above (mirrors `PcbResult`).
- `Foundry.Core/Pcb/RouteOptions.cs` — passes (`-mp`, default ~10), threads (`-mt`), timeout.
- `Foundry.Core/Pcb/KiCadScripts/export_dsn.py`, `apply_ses.py` — embedded resources (§B, §D).
- `PcbBuilder.RouteAsync` (+ command-construction helper, pure) — orchestrator.
- UI: a **ROUTE / Autoroute** affordance on the Wiring tab next to v2.2's EXPORT PCB, badge
  "Fully routed" / "3 unrouted", off the UI thread with cancel (per §9 perf risk).

Tests (`Foundry.Tests`, all KiCad/Java-free): FreeRouting command builder emits exactly
`--gui.enabled=false -de <dsn> -do <ses> -mp <n> -mt <n>`; `FreeRoutingInstaller` jar-path/Java-version
parsing; `RouteResult` parse of fixture `export_dsn.py`/`apply_ses.py` JSON (ok / not-found-binding /
error) and of a captured FreeRouting log (incomplete-count extraction); `NotInstalled()` when any
dependency is absent; one integration test guarded by
`if (KiCadInstaller.Locate() is null || FreeRoutingInstaller.LocateJava() is null) return;`.

### G. Sources (v2.4)

- KiCad CLI reference (master) — confirms NO specctra/dsn export verb, NO SES import verb:
  https://docs.kicad.org/master/en/cli/cli.html ; KiCad 9 CLI: https://docs.kicad.org/9.0/en/cli/cli.html
- pcbnew Python namespace (signatures `ExportSpecctraDSN(*args)`, `ImportSpecctraSES(wxString)`,
  `LoadBoard`, `SaveBoard`): https://docs.kicad.org/doxygen-python/namespacepcbnew.html
- pcbnew scripting helpers (Specctra export/import are frame-free standalone overloads):
  https://docs.kicad.org/doxygen/pcbnew__scripting__helpers_8h.html ;
  forum confirmation: https://forum.kicad.info/t/pcbnew-export-import-scripting-functions/16343 ;
  binding-flakiness note: https://forum.kicad.info/t/python-apis-exportspecctradsn-broken-in-nightly/24011
- BOARD connectivity API (`GetUnconnectedNetCount`, `BuildConnectivity`, `GetFullRatsnest`):
  https://docs.kicad.org/doxygen-python-6.0/classpcbnew_1_1BOARD.html
- FreeRouting latest release v2.2.4 (2026-05-13) + jar URL:
  https://github.com/freerouting/freerouting/releases/download/v2.2.4/freerouting-2.2.4.jar ;
  releases: https://github.com/freerouting/freerouting/releases
- FreeRouting command-line arguments (`--gui.enabled=false`, `-de`, `-do`, `-mp`, `-mt`, `-drc`, logging):
  https://github.com/freerouting/freerouting/blob/master/docs/command_line_arguments.md
- FreeRouting + KiCad DSN/SES workflow + Java requirement (21+, builds on 25):
  https://freerouting.org/freerouting/using-with-kicad ; https://github.com/freerouting/freerouting
- FreeRouting license GPLv3 (subprocess only, never link/vendor): https://freerouting.org/freerouting/gpl-v3

---

## v2.5 DRC + Fix Loop (concrete)

**Goal (v2.5 only):** run `kicad-cli pcb drc` on the v2.4 routed board as a **deterministic gate**. If it
reports violations, run a **bounded fix loop** — deterministic parameter bumps first (clearance / board
margin / router passes), AI placement revision (fenced to advice) only when a bump can't help — then
re-place → re-route → re-DRC, up to ~3 iterations, returning the **best (fewest-violation)** board. No
gerbers (v2.6). The deterministic placer/router still own every coordinate; the AI never sees or emits
geometry. KiCad is absent here, so every piece below is pure + unit-testable and the runner degrades to
`DrcReport.NotInstalled()` exactly like `PcbResult`/`RouteResult`.

### A. The `kicad-cli pcb drc` invocation

KiCad 8/9/10 expose DRC as `kicad-cli pcb drc` (it exists in all three — same flags). Exact command for
**"DRC a standalone `.kicad_pcb` with NO schematic, report clearance/track/unconnected violations as JSON"**:

```
kicad-cli pcb drc --format json --output <report.json> --severity-error --exit-code-violations <board.kicad_pcb>
```

Flag-by-flag (verbatim from the CLI reference, KiCad 8.0):

- `--format json` — "Report file format. Options are `report` (default) or `json`." We always pass `json`
  so the report is machine-parseable (schema below). Without it we'd get a human `.rpt`.
- `--output <file>` — "Output filename for the generated DRC report. When this argument is not used, the
  output filename will be the same as the input file, with the `.rpt` or `.json` file extension." We write
  to an explicit temp path (`board.drc.json`) and read that file — DRC does **not** stream the JSON to
  stdout, so file output is the contract (mirrors how `export_dsn.py` writes a file, not the
  `PcbResult` stdout-JSON idiom).
- `--exit-code-violations` — "Return an exit code depending on whether or not DRC violations exist. The
  exit code is **0 if no violations are found, and 5 if any violations are found**." This is the
  deterministic GATE signal: **exit 0 ⇒ clean, exit 5 ⇒ violations, anything else ⇒ tool/IO error.** We
  still parse the JSON for counts/types regardless of exit code (the JSON is authoritative for *what* the
  violations are; the exit code is the fast clean/dirty bit).
- `--severity-error` — "Report all error-level DRC violations." We gate on **errors only** by default;
  warnings (e.g. silk-over-silk) are reported but do not fail the gate. `--severity-warning` /
  `--severity-all` / `--severity-exclusions` are the other selectors and combine. (Pass `--severity-all`
  in a "strict" mode if we later want warnings to count.)
- **`--schematic-parity`: do NOT pass it.** It is opt-in ("Test for parity between PCB and schematic") and
  defaults OFF. There is no schematic in this pipeline (Track B builds the board straight from the
  in-memory netlist, no `.kicad_sch`), so enabling parity would false-fail with spurious
  `schematic_parity` violations. Omitting the flag means the `schematic_parity[]` array comes back empty —
  exactly what we want. (The router-applied netlist is what we DRC against; clearance + connectivity are
  the real checks here.)
- `--units mm` — report coordinates in mm (default; matches the placer's mm coordinate space). Optional.
- `--all-track-errors` — "Report all errors for each track" (don't collapse multiple errors on one track).
  Optional; useful for richer remediation but not required for the gate.
- `--define-var KEY=VALUE` — project-var override; unused here.

So the parse layer treats it as: **exit 0 = pass, exit 5 = fail-with-violations (read JSON), other =
infra error (read stderr)** — directly analogous to FirmwareBuilder's compile exit handling.

### B. The DRC JSON schema (model `DrcReport` / `DrcViolation`)

Schema lives in KiCad source at `resources/schemas/drc.v1.json`. Top-level object:

| Key | Type | Notes |
|-----|------|-------|
| `source` | string | input board path |
| `date` | string | ISO 8601 |
| `kicad_version` | string | e.g. `8.0.5` |
| `coordinate_units` | string | `mm` \| `mils` \| `in` |
| `violations` | array | the design-rule violations (clearance, courtyard, edge, dangling, …) |
| `unconnected_items` | array | nets the router left open — **same object shape as a violation** |
| `schematic_parity` | array | empty when `--schematic-parity` is omitted (our case) |

Each **violation / unconnected_item / schematic_parity** entry:

```json
{
  "type": "clearance",
  "severity": "error",
  "description": "Clearance violation (netclass 'Default' clearance 0.2 mm; actual 0.13 mm)",
  "excluded": false,
  "items": [
    { "uuid": "…", "description": "Pad 1 of C3", "pos": { "x": 12.7, "y": 8.4 } },
    { "uuid": "…", "description": "Track on F.Cu",  "pos": { "x": 12.9, "y": 8.5 } }
  ]
}
```

Item fields: `uuid` (string), `description` (string), `pos` `{x,y}` (numbers, in `coordinate_units`).
`excluded` (bool, default false) + optional `comment` mark suppressed violations — we ignore excluded ones
when counting the gate.

**Model (new `Foundry.Core/Pcb/DrcReport.cs`, mirroring `RouteResult`):**

```csharp
public sealed record DrcItem(string Uuid, string Description, double X, double Y);
public sealed record DrcViolation(string Type, string Severity, string Description,
    bool Excluded, IReadOnlyList<DrcItem> Items);

public sealed record DrcReport(
    bool Installed, bool Ok, string Summary,
    IReadOnlyList<DrcViolation> Violations,        // errors+warnings, excluded filtered out
    IReadOnlyList<DrcViolation> Unconnected,        // from unconnected_items[]
    int ErrorCount, int WarningCount, int UnconnectedCount,
    IReadOnlyList<string> Notes)
{
    public bool Clean => Ok && ErrorCount == 0 && UnconnectedCount == 0;   // the gate
    public static DrcReport NotInstalled() =>
        new(false, false, $"DRC needs KiCad — install it from {KiCadInstaller.DownloadUrl}.",
            Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 0, 0, 0, Array.Empty<string>());
    public static DrcReport Failed(string summary, IEnumerable<string>? notes = null) => …;

    // Parse the report FILE contents (not stdout) + the cli exit code, like RouteResult.Parse.
    public static DrcReport Parse(string reportJson, int exitCode, string stderr) { … }
}
```

`Parse` is pure (operates on the file text + exit code), tolerant in the `RouteResult.Parse` style:
JsonDocument over the file; `severity == "error"` and not `excluded` ⇒ counts toward `ErrorCount`;
`unconnected_items[]` entries (or `violations[]` of type `unconnected_items`) ⇒ `UnconnectedCount`;
exit 5 with zero parsed errors is reconciled to the JSON (JSON wins for *what*, exit for clean/dirty);
non-{0,5} exit ⇒ `Ok=false` with stderr folded into a note; missing/garbage file ⇒ `Failed(...)`. Never
throws. Fully unit-testable against captured fixture JSON with no KiCad present.

### C. The DRC runner — `PcbDrc.CheckAsync` (mirrors `PcbRouter.RouteAsync`)

`Foundry.Core/Pcb/PcbDrc.cs`, `public static async Task<DrcReport> CheckAsync(string kicadPcbPath,
DrcOptions? options = null, CancellationToken ct = default)`:

1. `KiCadInstaller.Locate()` → null ⇒ `DrcReport.NotInstalled()`. (`kicad-cli` is *not* the bundled
   python — DRC is a native CLI verb, so we invoke `kicad.KicadCliPath`, not `kicad.PythonPath`.)
2. temp workdir; `report = work\board.drc.json`.
3. run `kicad.KicadCliPath` with `pcb drc --format json --output "<report>" --severity-error
   --exit-code-violations "<kicadPcbPath>"` via the same `RunAsync` (redirect stdout/stderr) helper used by
   `PcbRouter`.
4. read the `report` file; `DrcReport.Parse(reportText, exitCode, stderr)`; log + return; delete workdir.

A pure **command builder** (`PcbDrc.BuildArgs(cli, board, reportPath, DrcOptions)`) emits exactly
`pcb drc --format json --output "<report>" --severity-error --exit-code-violations "<board>"` so it's
unit-asserted without KiCad (the v2.4 FreeRouting-command-builder test precedent). `DrcOptions` carries
`Strict` (adds `--severity-warning` so warnings gate too) and `Units` (default mm).

### D. Per-class fix strategy (what's deterministic vs. needs the AI)

The placer/router own geometry, so most fixes are a **parameter bump + re-run** — no AI. The AI is only
pulled in when *where things sit* is the problem and a bump can't resolve it (and even then it's fenced to
the `PlacementPlan` advice contract). The placer already exposes the two knobs that matter:
`PcbPlacer.Place(items, plan, marginMm = 5.0, gapMm = 1.5)` and the router exposes `RouteOptions.Passes`.

| Violation `type` | Root cause in THIS pipeline | Remediation | Kind |
|---|---|---|---|
| `clearance`, `hole_clearance` | traces/pads too close after routing | **bump `gapMm`** (more courtyard spacing → more routing channel) and/or widen netclass clearance; re-place→re-route | deterministic |
| `courtyards_overlap` | two parts packed too tight (rare — placer guarantees non-overlap of *inflated* boxes, but a tiny `gapMm` can still trip the courtyard rule) | **bump `gapMm`** and re-place | deterministic |
| `copper_edge_clearance` | a part/track sits too near the board outline | **bump `marginMm`** (placer sizes the outline from margin) and re-place | deterministic |
| `track_dangling`, `via_dangling`, `unconnected_items` | the router left a net open / a stub | **bump `RouteOptions.Passes`** and re-route the *same* placement first; if still unrouted after the pass bump, it's a routability/density problem → **AI placement revision** (loosen grouping / spread the dense block) then re-place→re-route | deterministic first, then AI |
| `silk_over_copper`, `silk_overlap` (warning) | cosmetic silkscreen | not gated by default (`--severity-error`); reported only | none (report) |
| persistent `clearance` after max gap bump | genuinely congested layout the placer can't relieve by spacing alone | **AI placement revision** — feed the violating refs back, ask for a revised `PlacementPlan` (more spread / different region order); placer still owns coordinates | AI |

**Bump schedule** (deterministic, monotonic so each iteration strictly loosens):
`gapMm: 1.5 → 2.5 → 4.0`, `marginMm: 5.0 → 7.0 → 10.0`, `Passes: 10 → 20 → 40`. Which knob bumps is
chosen from the dominant violation class that iteration (edge → margin, clearance/courtyard → gap,
unconnected/dangling → passes; bump all relevant if mixed).

**AI fence (unchanged contract):** the fix-AI is given the violating refs + their classes and asked for a
**revised `PlacementPlan`** (same JSON contract `PcbPlanner` already uses — groups/edge/near/regionOrder,
NEVER coordinates). It is invoked through a `RevisePlanAsync(project, currentPlan, violations)` method on
`PcbPlanner` that appends a "these parts had clearance/unconnected violations — spread them / revise
grouping" instruction to the existing system prompt. Unparseable / no-key ⇒ keep the current plan
(degrade to deterministic-only), exactly like `PlanAsync` → `PlacementPlan.Empty`.

### E. The fix loop — `PcbDesigner.DesignAsync` (mirrors `FixFirmwareAsync`)

New orchestrator `Foundry.Core/Pcb/PcbDesigner.cs` chains build → route → drc, then the bounded remediation
loop. Returns the **best** board seen (fewest errors, then fewest unconnected), so a partial improvement is
never thrown away. Loop is pure control-flow over injectable gate/router/placer delegates → unit-testable
with fakes (a fake DRC that returns scripted reports, a fake router/placer), no KiCad needed.

```
DesignAsync(project, outputDir, ai, opts, ct) -> PcbDesignResult:
    plan      = ai.PlanAsync(project)            // PlacementPlan (Empty if no key)
    gapMm     = 1.5;  marginMm = 5.0;  passes = 10
    best      = null                              // (board, DrcReport)

    for attempt in 1..MaxIterations (=3):
        placed  = PcbPlacer.Place(items, plan, marginMm, gapMm)   // deterministic coords
        built   = PcbBuilder.BuildAsync(project, outputDir, plan) // .kicad_pcb
        if not built.Ok: return PcbDesignResult.From(built)        // build failure is terminal
        routed  = PcbRouter.RouteAsync(built.KicadPcbPath, new RouteOptions(passes))
        if not routed.Installed: return PcbDesignResult.NotInstalled()
        report  = PcbDrc.CheckAsync(routed.RoutedPcbPath ?? built.KicadPcbPath)
        if not report.Installed: return PcbDesignResult.NotInstalled()

        best = Better(best, (routed, report))     // keep fewest-violation board

        if report.Clean:                          // GATE PASSED — exit 0, no errors, no unconnected
            return PcbDesignResult.Passed(routed, report, attempt)

        // ---- remediate for next iteration ----
        classes = report.DominantClasses()
        bumped  = false
        if classes has edge:                  marginMm = NextMargin(marginMm); bumped = true
        if classes has clearance|courtyard:   gapMm    = NextGap(gapMm);       bumped = true
        if classes has unconnected|dangling:  passes   = NextPasses(passes);   bumped = true

        // if a bump can't help (already maxed, or congestion persists) revise the PLAN via AI (fenced)
        if (not bumped) or (attempt >= 2 and StillClearanceBound(report)):
            plan = ai.RevisePlanAsync(project, plan, report.Violations)   // advice only; Empty-safe

    // loop exhausted — return the best board with a "DRC not clean after N tries" summary
    return PcbDesignResult.Exhausted(best, MaxIterations)
```

`PcbDesignResult` (mirrors `RouteResult`): `Installed`, `Ok` (== gate passed), `Summary`
("DRC clean on attempt 2 — 0 errors, fully connected" / "Best of 3: 2 clearance errors remain"),
`KicadPcbPath` (the best board), `Report` (final `DrcReport`), `Iterations`, `Notes`, plus
`NotInstalled()`/`Passed`/`Exhausted` factories. Bound `MaxIterations` defaults to 3 (a `DrcOptions`/
`DesignOptions` knob) — same "small bounded loop, return last/best" shape as `ProjectGenerator`'s 2-attempt
parse and `FixFirmwareAsync`'s single re-compile.

### F. New code (v2.5 only)

- `Foundry.Core/Pcb/DrcReport.cs` — DTO + `DrcItem`/`DrcViolation` + tolerant `Parse(reportJson, exit, stderr)` + `NotInstalled()`/`Failed()` (mirror `RouteResult`).
- `Foundry.Core/Pcb/DrcOptions.cs` — `Strict` (warnings gate), `Units` (mm), `MaxIterations` (=3).
- `Foundry.Core/Pcb/PcbDrc.cs` — `CheckAsync` (invokes `kicad-cli pcb drc`) + pure `BuildArgs(...)`.
- `Foundry.Core/Pcb/PcbDesigner.cs` — `DesignAsync` build→route→drc fix loop (control flow over injectable steps).
- `PcbPlanner.RevisePlanAsync(project, currentPlan, violations)` — fenced AI plan revision (advice-only, Empty-safe).
- UI: a **DRC / Check** badge on the Wiring tab next to v2.4's ROUTE — "DRC clean" / "2 errors", off the UI thread with cancel.

Tests (`Foundry.Tests`, all KiCad/AI-free):
- `PcbDrc.BuildArgs` emits exactly `pcb drc --format json --output "<r>" --severity-error --exit-code-violations "<b>"` (and `--severity-warning` added in strict mode).
- `DrcReport.Parse` over fixture JSON: clean report (empty arrays, exit 0) ⇒ `Clean`; a report with 2 `clearance` errors + 1 `unconnected_items` (exit 5) ⇒ correct counts; `excluded:true` violation not counted; garbage/missing file ⇒ `Failed`; non-{0,5} exit ⇒ `Ok=false` with stderr note.
- Fix-loop control flow with a **fake gate/router/placer**: clean-on-first-try returns `Passed(attempt:1)`; dirty-then-clean returns `Passed(attempt:2)` and shows the bumped `gapMm`/`passes`; always-dirty returns `Exhausted` with the **fewest-violation** board (verify "best" selection keeps the better of two dirty attempts).
- AI remediation against a fixture: `RevisePlanAsync` parse of a revised-plan JSON; no-key/garbage ⇒ unchanged plan.
- `NotInstalled()` when `KiCadInstaller.Locate()` is null; one integration test guarded by `if (KiCadInstaller.Locate() is null) return;`.

### G. Sources (v2.5)

- `kicad-cli pcb drc` reference — flags `--format json`, `--output`, `--severity-error/-warning/-all/-exclusions`, `--exit-code-violations` (exit 0 = clean, 5 = violations), `--schematic-parity` (opt-in, omit it), `--all-track-errors`, `--units`:
  KiCad 8.0: https://docs.kicad.org/8.0/en/cli/cli.html ; KiCad 9.0: https://docs.kicad.org/9.0/en/cli/cli.html ; master: https://docs.kicad.org/master/en/cli/cli.html
- DRC JSON report schema (`source`/`date`/`kicad_version`/`coordinate_units`/`violations[]`/`unconnected_items[]`/`schematic_parity[]`; violation `type`/`severity`/`description`/`excluded`/`items[]`{`uuid`,`description`,`pos`{`x`,`y`}}):
  https://gitlab.com/kicad/code/kicad/-/raw/master/resources/schemas/drc.v1.json
- DRC violation `type` strings (`clearance`, `hole_clearance`, `courtyards_overlap`, `copper_edge_clearance`, `track_dangling`, `via_dangling`, `silk_over_copper`, `unconnected_items`):
  https://docs.kicad.org/doxygen/drc__item_8cpp_source.html

---

## v2.6 Gerber + Drill Export (concrete)

The capstone: take v2.5's DRC-clean `.kicad_pcb` (`PcbDesignResult.KicadPcbPath`) and produce a single
fab-ready ZIP (Gerbers + Excellon drill) a board house (JLCPCB / PCBWay) accepts for a standard 2-layer
board. Two native `kicad-cli` verbs (`pcb export gerbers`, `pcb export drill`) write into one temp dir,
then `System.IO.Compression.ZipFile` bundles that dir. NO ordering/upload (v2.7). Mirrors `PcbDrc`'s
exact pattern: pure `BuildArgs(...)`, process invocation of `KiCadInstaller.Locate().KicadCliPath`,
`FabResult.NotInstalled()` when KiCad is absent, never throws.

### A. Layer set (standard 2-layer fab)

Nine layers — the JLCPCB/PCBWay 2-layer set. Use the canonical KiCad-9 layer tokens (KiCad 9 renamed
`F.SilkS`→`F.Silkscreen`, `B.SilkS`→`B.Silkscreen`; the old names still parse in 8.0 but the new ones are
accepted by both 8 and 9, so emit the new names):

```
F.Cu,B.Cu,F.Paste,B.Paste,F.Silkscreen,B.Silkscreen,F.Mask,B.Mask,Edge.Cuts
```

The board outline lives on **Edge.Cuts** (it MUST be present and closed — that is the fab's board
profile). Paste layers are not strictly needed by the fab to make the board (they drive a stencil), but
including them matches the KiCad/JLCPCB default set and is harmless. Define this as
`FabOptions.Default.Layers` (a single comma-joined string) so it is one unit-testable constant.

### B. Command 1 — `kicad-cli pcb export gerbers` (exact)

```
kicad-cli pcb export gerbers \
  --output "<outdir>" \
  --layers "F.Cu,B.Cu,F.Paste,B.Paste,F.Silkscreen,B.Silkscreen,F.Mask,B.Mask,Edge.Cuts" \
  --subtract-soldermask \
  --use-drill-file-origin \
  "<board.kicad_pcb>"
```

- `--output` is a **directory** ("The output folder for the exported files. One file is output for each
  layer."). One `.g*` file is written per layer.
- `--layers` = the comma-separated set above.
- **Protel extensions are KEPT (do NOT pass `--no-protel-ext`)**: JLCPCB's own KiCad-9 guide says "Select
  Use Protel filename extensions … JLCPCB prefers Protel filename extensions." So we want the default
  (`.gtl/.gbl/.gtp/.gbp/.gto/.gbo/.gts/.gbs/.gm1`), which is what omitting `--no-protel-ext` gives.
- **X2 is KEPT (do NOT pass `--no-x2`)**: JLCPCB's guide says "Select Use extended X2 format." Default X2
  is fine; the netlist/X2 attributes JLCPCB tolerates.
- `--subtract-soldermask` removes silk from un-masked areas (matches the standard fab plot config).
- `--use-drill-file-origin` makes the gerber origin match the drill origin — keep gerbers and drill on the
  **same** origin. (Pair this with `--drill-origin plot` below, OR drop both and use absolute on both; the
  invariant is *gerbers and drill share one origin*. We use the drill/place origin on both.)
- `INPUT_FILE` (the board) is the last positional, quoted.

### C. Command 2 — `kicad-cli pcb export drill` (exact)

```
kicad-cli pcb export drill \
  --output "<outdir>/" \
  --format excellon \
  --drill-origin plot \
  --excellon-units mm \
  --excellon-zeros-format decimal \
  --excellon-separate-th \
  --generate-map \
  --map-format gerberx2 \
  "<board.kicad_pcb>"
```

- `--output` is the drill **directory** (KiCad is picky: pass a trailing separator, e.g. `"<outdir>\"`,
  or the literal dir — same `<outdir>` as the gerbers so everything lands together for the zip).
- `--format excellon` (default, but explicit) — Excellon NC drill, the universal fab format.
- `--drill-origin plot` = use the drill/place origin (pairs with gerbers' `--use-drill-file-origin` so
  drill and copper align). Absolute also works if gerbers also use absolute; do not mix.
- `--excellon-units mm` + `--excellon-zeros-format decimal` — JLCPCB's guide: Units = Millimeters,
  Zeros = Decimal.
- `--excellon-separate-th` — separate plated-through vs non-plated-through files (`-PTH.drl` / `-NPTH.drl`),
  which fabs prefer for correct processing.
- `--generate-map --map-format gerberx2` — a drill map (a gerber-format map travels in the zip with the
  rest; `pdf` is the default but a gerber map keeps the package single-format). Optional — fabs ignore the
  map; include it for human/QA inspection.

### D. Expected file set in the ZIP (2-layer, Protel ext)

With Protel extensions + `--excellon-separate-th`, the output dir (and thus the ZIP) contains:

| File (suffix on `<board>`) | Layer / content |
|---|---|
| `<b>-F_Cu.gtl` | F.Cu (top copper) |
| `<b>-B_Cu.gbl` | B.Cu (bottom copper) |
| `<b>-F_Paste.gtp` | F.Paste |
| `<b>-B_Paste.gbp` | B.Paste |
| `<b>-F_Silkscreen.gto` | F.Silkscreen |
| `<b>-B_Silkscreen.gbo` | B.Silkscreen |
| `<b>-F_Mask.gts` | F.Mask |
| `<b>-B_Mask.gbs` | B.Mask |
| `<b>-Edge_Cuts.gm1` | Edge.Cuts (board outline) |
| `<b>-PTH.drl` | Excellon plated through-holes |
| `<b>-NPTH.drl` | Excellon non-plated through-holes |
| `<b>-PTH-drl_map.gbr` / `*-NPTH-drl_map.gbr` | drill maps (optional) |

Gotchas:
- **A single ZIP of the whole output dir is exactly what JLCPCB/PCBWay want** ("zip the out folder and
  place the order"). No per-file renaming, no manifest. We zip the temp `<outdir>` to
  `<projectName>-fab.zip`.
- **Edge.Cuts is mandatory** — no `.gm1`/outline ⇒ fab rejects (no board profile). Treat a missing
  `Edge.Cuts` gerber as a failure in `Parse`.
- The exact suffix casing (`-F_Cu` etc.) and Protel extensions are KiCad's defaults — don't hardcode the
  filenames as the success test; test on *count/extension presence* + the required PTH/Edge files (see E),
  since a renamed board or KiCad minor differences shift the stem.
- If `--excellon-separate-th` is omitted you get one merged `<b>.drl` instead of `-PTH`/`-NPTH`; JLCPCB
  accepts both, but separate is the recommended default.

### E. Success detection (`FabResult.Parse`)

Mirror `DrcReport.Parse` / `RouteResult.Parse` — exit code is the fast bit, the produced file set is
authoritative; never throw:

1. **Two runs, both must succeed.** Run gerbers then drill; if either is cancelled, propagate. Capture
   each `(stdout, stderr, exitCode)`.
2. **Exit code**: `kicad-cli` returns **0 on success, non-zero on failure** for both verbs. `exit != 0` on
   either run ⇒ `Failed` with the trimmed stderr as a note. (Unlike `pcb drc`, export has no special
   "violations" exit code — 0 = wrote files, anything else = error.)
3. **File-set check (the real gate)**: enumerate `<outdir>` and require, at minimum:
   - at least one copper gerber (`.gtl` AND `.gbl`, or `.gbr` if `--no-protel-ext` were ever used),
   - the **Edge.Cuts** outline gerber (`*-Edge_Cuts.gm1` / `*.gm1`),
   - at least one drill file (`*.drl`) — and, when `--excellon-separate-th`, the `*-PTH.drl`.
   Build this as a pure helper `FabFileSet.Validate(IEnumerable<string> producedFiles)` over filenames so
   it is unit-testable with **fake** file lists (no KiCad). Returns the missing-required list.
4. **ZIP**: only after both exits are 0 AND the file-set validates, `ZipFile.CreateFromDirectory(outdir,
   zipPath)`. Then assert `File.Exists(zipPath)` and a non-trivial entry count. `Ok` ⇔ all of the above.
5. `FabResult` shape: `(bool Installed, bool Ok, string Summary, string? ZipPath, int GerberCount, int
   DrillCount, IReadOnlyList<string> Files, IReadOnlyList<string> Notes)` with `NotInstalled()` /
   `Failed(summary, notes)` factories — `Summary` like "Exported 9 gerbers + 2 drill files → board-fab.zip"
   or "Couldn't export fab files: <reason>".

### F. New code (v2.6 only)

- `Foundry.Core/Pcb/Fab/FabOptions.cs` — `Layers` (the 9-token string), `Units` (mm), `SeparatePlated`
  (true), `GenerateMap` (true), `MapFormat` (gerberx2), `UseDrillOrigin` (true). One `Default`.
- `Foundry.Core/Pcb/Fab/FabResult.cs` — DTO + tolerant `Parse(...)` + `NotInstalled()`/`Failed()` (mirror
  `RouteResult`/`DrcReport`).
- `Foundry.Core/Pcb/Fab/FabFileSet.cs` — pure `Validate(producedFiles)` (required-file gate, fake-able).
- `Foundry.Core/Pcb/Fab/PcbFab.cs` — `ExportAsync(kicadPcbPath, options)` (two `kicad-cli` runs into a
  temp dir, validate, zip) + pure `BuildGerberArgs(...)` / `BuildDrillArgs(...)` mirroring `PcbDrc.BuildArgs`.
- `PcbDesigner.DesignAsync` end-to-end tie-in OR a thin `PcbDesigner.DesignAndExportAsync` that chains
  clean-board → `PcbFab.ExportAsync` (keep the export decoupled so it can run on any clean board).
- UI: a **Fab / Export** affordance on the Wiring tab next to v2.5's DRC badge — "Export fab ZIP" →
  surfaces `ZipPath` (open-folder), off the UI thread with cancel; greyed/guidance when KiCad absent.

Tests (`Foundry.Tests`, all KiCad-free):
- `PcbFab.BuildGerberArgs` emits exactly `pcb export gerbers --output "<d>" --layers "F.Cu,...,Edge.Cuts"
  --subtract-soldermask --use-drill-file-origin "<b>"` (and that `--no-protel-ext` is NOT present — Protel
  kept for JLCPCB).
- `PcbFab.BuildDrillArgs` emits `pcb export drill --output "<d>" --format excellon --drill-origin plot
  --excellon-units mm --excellon-zeros-format decimal --excellon-separate-th --generate-map --map-format
  gerberx2 "<b>"`.
- `FabFileSet.Validate` over fake filename lists: full Protel set + `-PTH.drl` + `.gm1` ⇒ no missing;
  missing `Edge.Cuts` ⇒ reports it; missing all `.drl` ⇒ reports it; `--no-protel-ext` `.gbr` set still
  validates copper.
- `FabResult.Parse`: both exits 0 + valid file list + zip present ⇒ `Ok` with right counts/summary; gerber
  exit≠0 ⇒ `Failed` w/ stderr note; exits 0 but Edge.Cuts missing ⇒ `Failed`; zip-missing ⇒ `Failed`.
- `FabResult.NotInstalled()` when `KiCadInstaller.Locate()` is null; one integration test guarded by
  `if (KiCadInstaller.Locate() is null) return;` that actually exports + asserts the zip.

### G. Sources (v2.6)

- `kicad-cli pcb export gerbers` reference — `--output` (output folder, one file per layer), `--layers`
  ("comma-separated list of layer names … such as `F.Cu,B.Cu`"), `--no-protel-ext` ("Use .gbr file
  extension instead of Protel file extensions"), `--subtract-soldermask`, `--use-drill-file-origin`,
  `--no-x2`, `--no-netlist`, `--common-layers`, `--precision`; valid layer tokens (`F.Cu`,`B.Cu`,
  `F.Paste`,`B.Paste`,`F.Silkscreen`,`B.Silkscreen`,`F.Mask`,`B.Mask`,`Edge.Cuts`,…):
  KiCad 9.0: https://docs.kicad.org/9.0/en/cli/cli.html ; KiCad 8.0: https://docs.kicad.org/8.0/en/cli/cli.html ; master: https://docs.kicad.org/master/en/cli/cli.html
- `kicad-cli pcb export drill` reference — `--output` (drill output dir), `--format` (`excellon` default /
  `gerber`), `--drill-origin` (`absolute` default / `plot`), `--excellon-units` (`mm` default / `in`),
  `--excellon-zeros-format` (`decimal`/`suppressleading`/`suppresstrailing`/`keep`),
  `--excellon-separate-th` (separate plated / non-plated), `--generate-map`, `--map-format`
  (`pdf` default / `gerberx2`/`ps`/`dxf`/`svg`), `--excellon-mirror-y`, `--excellon-min-header`:
  https://docs.kicad.org/9.0/en/cli/cli.html ; https://docs.kicad.org/8.0/en/cli/cli.html
- JLCPCB Gerber/drill requirements for KiCad 9 — required 2-layer layer set; "use Protel filename
  extensions (JLCPCB prefers Protel)"; "use extended X2 format"; drill Excellon / mm / decimal zeros;
  "zip the out folder and place the order":
  https://jlcpcb.com/help/article/how-to-generate-gerber-and-drill-files-in-kicad-9
- PCB Export overview (kicad-cli source/behavior — exit-code-on-success, one-file-per-layer):
  https://deepwiki.com/KiCad/kicad-source-mirror/2.6-pcb-export
