# PCB Real-Geometry Placement + DRC Fix-Loop + Real Footprints — Design

Date: 2026-05-29
Branch: feature/pcb-real-placement (no commits)
Status: verified end-to-end against KiCad 10 on this machine

## Problem (confirmed root cause)

`PcbPlacer` packs parts using `FootprintMap.CourtyardOf` approximations that are far
SMALLER than the real KiCad footprints. Parts overlap / clearance-violate → 19 DRC
errors, 2 unrouted nets, layout crammed into a corner on the SolarPool board.

Measured proof (real KiCad-10 courtyard vs `CourtyardOf` approximation):

| lib id | real courtyard W×H (mm) | `CourtyardOf` approx (mm) | verdict |
|---|---|---|---|
| `Resistor_SMD:R_0805_2012Metric` | 3.45 × 1.99 | 2.0 × 1.25 | approx ~40% too small |
| `RF_Module:ESP32-WROOM-32` | **48.09 × 41.34** | **18.0 × 25.5** | approx <½ the area — catastrophic |
| `Connector_PinHeader_2.54mm:PinHeader_1x03_P2.54mm_Vertical` | 3.63 × 8.71 | 7.62 × 2.54 | wrong size AND rotated 90° |

On the existing `SolarPool_WiFi_Water_Monitor.kicad_pcb`, the ESP32 (`MCU`) sits at
origin (38, 49.25) and occupies ≈ x∈[14,62], y∈[29,70]. Other parts are placed at
(32.8, 7.75)…(55.3, 67.75) — i.e. directly inside the ESP32 courtyard. That is the
overlap that produces the DRC errors. The board uses the generic `PinHeader` fallback
for every connector/module/sensor (BOOST, TEMP, PANEL, CHG, ORP, BATT, PH), so pad
counts and physical sizes are also wrong.

## 1. Real-geometry placement

### Measure API (verified on KiCad 10 / pcbnew, this machine)

The correct measurement is the **courtyard** (`F.CrtYd`/`B.CrtYd`), the polygon DRC
actually enforces for `courtyards_overlap`/`clearance`. Do NOT use `fp.GetBoundingBox()`:
it includes the silkscreen `REF**` text, so an 0805 resistor reports 14.9 × 5.0 mm
instead of 3.45 × 1.99 mm — wildly inflated and useless for packing.

Verified per-footprint recipe (KiCad 10, `pcbnew.FootprintLoad(libDir, name)`):

```python
def measure(fp):
    w = h = 0.0
    for layer in (pcbnew.F_CrtYd, pcbnew.B_CrtYd):
        poly = fp.GetCourtyard(layer)            # SHAPE_POLY_SET
        if poly is not None and poly.OutlineCount() > 0:
            bb = poly.BBox()                      # BOX2I in internal units
            w = max(w, pcbnew.ToMM(bb.GetWidth()))
            h = max(h, pcbnew.ToMM(bb.GetHeight()))
    if w == 0 or h == 0:                          # rare: no courtyard layer
        bb = fp.GetBoundingHull().BBox()          # pads+edges hull, excludes text
        w, h = pcbnew.ToMM(bb.GetWidth()), pcbnew.ToMM(bb.GetHeight())
    return w, h
```

- `fp.GetCourtyard(layer)` → `SHAPE_POLY_SET`; `.OutlineCount()` guards empties;
  `.BBox()` → `BOX2I`; `GetWidth()/GetHeight()` are internal units; `pcbnew.ToMM(...)`
  converts. Union front+back so SMD-on-both-sides parts measure correctly.
- Fallback when a footprint has no courtyard: `fp.GetBoundingHull()` (also a
  `SHAPE_POLY_SET`) — the pad+edge hull, which (unlike `GetBoundingBox`) excludes
  silk text. Verified to match courtyard within ~0.05mm on all 3 test parts.

All 13 candidate footprints loaded and measured successfully (numbers below).

### MEASURE mode for `build_board.py` — JSON contract

Add a `measure` subcommand alongside the existing build path. Invocation:

```
python.exe build_board.py measure measure_job.json
```

Input `measure_job.json`:
```json
{ "mode": "measure",
  "footprintDirs": ["C:/.../KiCad/10.0/share/kicad/footprints"],
  "libIds": ["RF_Module:ESP32-WROOM-32", "Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical", "..."] }
```

Output (one JSON line on stdout, same convention as build):
```json
{ "ok": true,
  "sizes": {
    "RF_Module:ESP32-WROOM-32": { "wMm": 48.09, "hMm": 41.34, "pads": 60, "src": "courtyard" },
    "Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical": { "wMm": 8.99, "hMm": 5.59, "pads": 3, "src": "courtyard" }
  },
  "notes": ["footprint Foo:Bar not found in <dir>"] }
```

- Keyed by the exact lib id requested. A missing footprint is recorded in `notes`
  and OMITTED from `sizes` (caller falls back to `CourtyardOf` for that id).
- `src` is `"courtyard"` or `"hull"` for diagnostics.
- Reuse the existing `resolve_lib_dir` + `footprint_loader()` helpers. Dispatch on
  `sys.argv[1] == "measure"` (else fall through to today's build path → no behavior
  change for the build job).

### Wiring real sizes into `PcbPlacer` (C# side)

`PcbJob.Build` is where `FootprintMap.CourtyardOf` is called today (PcbJob.cs:93-94).
The clean seam: make `Build` accept an optional measured-size map and prefer it.

1. New overload `PcbJob.Build(... , IReadOnlyDictionary<string,(double W,double H)>? realSizes = null, ...)`.
   Replace the courtyard lookup with:
   ```csharp
   (double,double) SizeOf(string libId) =>
       realSizes is not null && realSizes.TryGetValue(libId, out var s) ? s : FootprintMap.CourtyardOf(libId);
   ```
   `CourtyardOf` stays the offline fallback (tests / no-KiCad) — purity preserved.
2. New `PcbBuilder.MeasureAsync(project, footprintDirs, ct)`: resolve the distinct
   set of lib ids the job will use (run the same `FootprintMap.Resolve` pass over
   `refs`), write a `measure_job.json`, run `build_board.py measure`, parse `sizes`
   into a `Dictionary<string,(double,double)>`. Returns empty when KiCad absent.
3. `PcbBuilder.BuildAsync(project, outputDir, plan, margin, gap, ct)` calls
   `MeasureAsync` FIRST (when KiCad located), then passes the map into `PcbJob.Build`.
   `PcbPlacer.Place` is unchanged — it just receives correct `PlacedItem.Courtyard`
   sizes, so groups/edge/gap/near logic is untouched.
4. Measure once per design (cache the map on `PcbDesigner.DesignAsync` and reuse it
   across fix-loop iterations — footprint geometry doesn't change between re-places,
   only gap/margin do). Avoids re-spawning python each iteration.

### Raise default clearance/gap so the router has room

- `PcbPlacer.Place` default `gapMm` 1.5 → **2.0**; `PcbDesigner.Knobs.Gap0` 1.5 → 2.0
  (keep the 2.5 / 4.0 escalation rungs; bump `Gap1`/`Gap2` to 3.0 / 4.5 so each rung
  still strictly loosens).
- This is additive headroom; with real (larger) courtyards now feeding the packer,
  parts already spread out, and the larger gap gives FreeRouting routing channels.

## 2. Real footprint mappings for SolarPool parts (all verified to EXIST + measured)

SolarPool refs (from the board): MCU=ESP32, BOOST=MT3608, CHG=CN3791, PANEL=6V solar,
BATT=18650, TEMP=DS18B20, PH/ORP=DFRobot Gravity analog sensors, LED.

Add these keyword rules to `FootprintMap.Resolve` (BEFORE the generic header fallback;
keep `Header(n)` as the final fallback). All lib ids confirmed present in
`.../KiCad/10.0/share/kicad/footprints/*.pretty` and measured via pcbnew:

| SolarPool part | keyword match | footprint lib id (EXISTS, verified) | courtyard W×H | pads |
|---|---|---|---|---|
| DFRobot Gravity pH / ORP (3-wire analog) | `gravity`, `ph sensor`, `orp`, `analog sensor` | `Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical` | 8.99 × 5.59 | 3 |
| DS18B20 waterproof probe (3-wire) | `ds18b20`, `1-wire`, `temperature probe` | `Package_TO_SOT_THT:TO-92_Inline` | 5.55 × 4.83 | 3 |
| 6V solar panel (2-wire) | `solar`, `panel`, `photovoltaic` | `Connector_JST:JST_PH_B2B-PH-K_1x02_P2.00mm_Vertical` | 6.99 × 5.59 | 2 |
| 18650 battery | `18650`, `li-ion cell`, `battery holder` | `Battery:BatteryHolder_Keystone_1042_1x18650` | 87.97 × 21.75 | 5 |
| MT3608 boost module | `mt3608`, `boost converter module` | size-correct pin header by pin count, e.g. `Connector_PinHeader_2.54mm:PinHeader_1x04_P2.54mm_Vertical` | 3.63 × 11.25 | 4 |
| CN3791 MPPT charger module | `cn3791`, `mppt`, `charge controller module` | size-correct pin header by pin count, e.g. `Connector_PinHeader_2.54mm:PinHeader_1x06_P2.54mm_Vertical` | 3.63 × 16.33 | 6 |

Notes / alternatives (all verified to exist):
- Screw-terminal alternative for PANEL/BATT power leads (physically saner for thick
  wire): `TerminalBlock:TerminalBlock_MaiXu_MX126-5.0-02P_1x02_P5.00mm` (11.59 × 8.9, 2 pads).
  The original spec suggestion `TerminalBlock:TerminalBlock_bornier-2_P5.08mm` does
  **NOT exist** in KiCad 10 stdlib — do not use it.
- `Connector_JST:JST_XH_B2B-XH-A_1x02_P2.50mm_Vertical` (8.49 × 6.84, 2 pads) is a
  valid larger-pitch JST alternative for the panel/battery 2-wire leads.
- MT3608/CN3791 keep a header (they're modules you solder onto via header strips) but
  now sized to the correct pin count via the existing `Header(n)` path — the win is
  that they no longer hit the *generic fallback diagnostic* and pad counts are explicit.
  If a labelled module footprint is desired later, none ship in stdlib, so header is
  the manufacturable choice.

PadNet mapping: the 3-wire Gravity sensors map cleanly to a 1×03 connector (pads
1/2/3 = signal/VCC/GND by ordinal), and `build_board.py`'s existing name-then-ordinal
pad matcher already covers this — no change needed there.

## 3. UI wiring — `PcbDesigner.DesignAsync` as the primary action

### Current state (verified)
`WiringView.xaml` exposes 6 ghost buttons + the design/fab loop already exists:
- `ExportPcbCommand` "EXPORT + ROUTE PCB" — single build→route (no DRC remediation).
- `DesignPcbCommand` "DESIGN (DRC)" — already calls `PcbDesigner.DesignAsync` (the
  full build→route→DRC→remediate loop) and already surfaces per-iteration trace via
  `PcbNotes.Add($"iteration {n}/{options.MaxIterations}: {line}")` (TabViewModels.cs:545-590).

### Changes
1. **Make the fix loop primary.** Promote the DESIGN button to `BtnPrimary` and rename
   to "DESIGN PCB (DRC)"; demote "EXPORT + ROUTE PCB" to `BtnGhost` secondary (or fold
   it behind an "advanced" affordance). The primary PCB action is now the bounded
   build→route→DRC→remediate→retry loop, NOT the single build+route. `DesignPcb` already
   wires `DesignAsync` — only the XAML emphasis/order changes.
2. **Per-iteration progress already present**; tighten the wording so the count delta
   shows, e.g. format each trace line as `iteration 2/3: 19 → N errors`. `RunLoopAsync`
   already appends `attempt N: gap=… margin=… passes=… → {report.Summary}`; extend the
   trace string to include the prior error count so the "19 → N" delta is explicit
   (carry `lastErrorCount` across the loop and prepend it).
3. **Remediation re-PLACES with larger gap** — already correct in `RunLoopAsync`
   (PcbDesigner.cs:187-206): on a non-clean report it bumps `MarginMm`/`GapMm`/`Passes`
   per the dominant violation class, then the next `build(plan, knobs, ct)` call rebuilds
   AND re-places at the looser gap (because `PcbBuilder.BuildAsync` re-runs
   `PcbJob.Build` → `PcbPlacer.Place` with the new gap each iteration). With real
   courtyards now feeding the packer, the FIRST placement is already non-overlapping,
   so remediation converges fast instead of fighting a corner-crammed start.
4. No new commands required. The only code change in the VM is the trace-wording tweak;
   the substantive fixes (real geometry, footprints, larger gap) live in Core.

## Verification plan (end-to-end, KiCad 10)
1. Add measure mode → unit-confirm JSON shape against the 3 reference footprints
   (numbers above are the expected output).
2. Regenerate the SolarPool board via `PcbDesigner.DesignAsync` from its project,
   confirm: parts spread out (board no longer corner-crammed), generic-fallback
   diagnostics drop to ~0, DRC error count plummets from 19 toward 0 across iterations,
   2 unrouted nets resolve.
3. Run `dotnet build Foundry.sln` + `dotnet test Foundry.Tests` (CourtyardOf stays the
   offline fallback so existing pure placer/footprint tests are unaffected).

## Files to touch
- `Foundry.Core/Pcb/KiCadScripts/build_board.py` — add `measure` subcommand.
- `Foundry.Core/Pcb/PcbBuilder.cs` — `MeasureAsync`; call measure before build; pass map.
- `Foundry.Core/Pcb/PcbJob.cs` — `Build` overload taking `realSizes`; `SizeOf` seam.
- `Foundry.Core/Pcb/FootprintMap.cs` — SolarPool keyword rules (§2); bump default gap.
- `Foundry.Core/Pcb/PcbPlacer.cs` / `PcbDesigner.cs` — default/initial gap 1.5 → 2.0,
  escalation rungs adjusted; cache measured sizes across iterations.
- `Foundry.App/ViewModels/TabViewModels.cs` — trace wording "19 → N errors".
- `Foundry.App/Views/Tabs/WiringView.xaml` — promote DESIGN to primary.
