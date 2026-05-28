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
