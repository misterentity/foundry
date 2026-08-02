---
title: Enclosure — CSG geometry and provable mechanical fit
domain: enclosure
status: active
last-reviewed: 2026-08-02
verified-against:
  - source-read: sidecar/enclosure.py, Foundry.Core/Cad/**, Foundry.Core/Sidecar/** (working tree)
  - live-run: sidecar/test_enclosure.py against real trimesh 4.12.2 + manifold3d — 9 passing
  - live-run: STEP heights measured from KiCad 10.0.3 3dmodels, checked against datasheet dimensions
---

# Enclosure — CSG geometry and provable mechanical fit

> **What's in this doc:** the CSG build (base, lid, cutouts, vents, standoffs, mounts), the coordinate conventions that make or break a cutout, where component heights come from, and the mechanical fit checks — the one part of Foundry that is fully decidable.
>
> **What's NOT:** the electrical rules engine — `Foundry.Core/Validation/**`, not yet documented (see [[_backlog]]); how the board outline is produced (→ [[pcb]]); how the enclosure schema is filled by the model (→ [[generation]]).

## Why this domain matters strategically

Mechanical fit is the **only** claim in Foundry that is decidable end to end. "Is this net driven?" needs design *intent* a netlist doesn't carry — measured over 15 real KiCad boards, the naive rule produced a false failure on ~35–49% of applicable nets (see [[_backlog#measured-gates]]). "Does the board fit in the box?" is geometry, and every number is present: outline from the placer, courtyards and Z-heights from KiCad's own models.

So this is where a verification claim can actually be *proved* rather than estimated — and no competitor does it, because it requires owning the board and the case together.

## Build pipeline

`sidecar/enclosure.py` is a Python CAD service (trimesh + manifold3d) the app spawns on `127.0.0.1`. `build_stl` (`sidecar/enclosure.py:368`) takes the schema and delegates to `_csg_build` (`:46`).

Coordinate conventions — get these wrong and features silently vanish:

- `_rounded_box(w,h,d,r)` (`:23`) is **centred in x/y with its base at z = 0**, extending to z = d.
- The **base** is `ox × oy × oz` where `oz = H + t` — a closed floor and an **open top** (`:53`).
- The **lid** is built separately (`_build_lid`, `:116`): a cap spanning z ∈ [0, capT] plus a locating lip hanging below at z ∈ [−lipH, 0].

### Cutouts belong to the part they pierce

`_cutout_solid` (`:194`) positions a prism or cylinder to pierce a named face. The routing split at `:70` is load-bearing:

- `face: "top"` features go to the **lid**, cut in lid-local coordinates at `:129-133`, with the cutter centred on `(capT − lipH) / 2` and made `capT + lipH + 2` long so it clears cap *and* lip.
- Every other face — including `bottom`, which pierces the floor — is cut from the base.

**The bug this prevents:** top-face cutouts used to be cut against the base. The base is open above, so the cutter passed through empty space and removed **exactly 0.00 mm³** — a reset hole or LED window vanished, and the flagship demo printed a sealed lid. `sidecar/test_enclosure.py` asserts material is actually removed on all six faces; reverting the fix fails 4 of its 9 tests.

Vents are expanded into thin slot cutouts by `_vent_cutouts` (`:149`) *before* the split, so top vents route to the lid too.

## Component heights — the ground truth

`Foundry.Core/Cad/StepHeights.cs` reads the STEP model KiCad ships with each footprint. KiCad 10 ships ~7,200 `.step` files and **no VRML**; STEP is ASCII, so the Z bounding box comes from reading every `CARTESIAN_POINT` (`ZExtent`, `:79`). Measured against datasheets:

| footprint | above board | below board |
|---|---|---|
| `Resistor_SMD:R_0805_2012Metric` | 0.45 mm | 0 |
| `Capacitor_SMD:C_0603_1608Metric` | 0.80 mm | 0 |
| `RF_Module:ESP32-WROOM-32` | 3.10 mm | — |
| `Package_DIP:DIP-28_W7.62mm` | 3.68 mm | 3.30 mm |
| `PinHeader_1x04_P2.54mm_Vertical` | 8.65 mm | 3.11 mm |
| `Package_TO_SOT_THT:TO-220-3_Vertical` | 18.77 mm | — |

**Z is signed about the board plane and both signs matter:** positive is what must clear the lid; negative is the pin tail that sets the minimum standoff.

**Coverage.** Of the 25 footprints `FootprintMap` can emit, 21 have a shipped model. The four that don't are covered by the curated table at `StepHeights.cs:33` — Pico, Uno, Nano, SOT-223. That makes Foundry's own vocabulary 100%. Third-party boards are a different story: across the 15 KiCad demo projects only **16.7%** of footprints came from standard KiCad libraries at all (professional designs vendor their own — `antmicro-footprints` alone is 1,123 parts), so heights for arbitrary imported boards are mostly unavailable.

A part with no model resolves to `PartHeight.Unknown` — never to zero, which would read as "flat".

## Board mounting

`_pcb_standoffs` builds posts at the board's **own** mounting holes, with M3 pilots, rising from the inner floor to the standoff height the fit math proved. Those holes come from `PcbPlacer`, which **reserves** the corner keep-outs — the border is widened to `MinMarginMm` (inset + keep-out radius) so no component can sit where a boss must go. Reserving is what lets the holes be reported as fact.

Before this the case had no board mounting at all: `_standoff_posts` builds **lid screw bosses**, positioned from the *case* corners and running nearly the full cavity height, so a printed enclosure held a loose PCB — and a port's height above the floor was undefined, which blocked cutout derivation entirely.

## Cutout derivation

`Foundry.Core/Cad/CutoutFit.cs` derives a port's face, position and size from where its component actually sits. `Cutout.Ref` names the part a port exposes; the face is the nearest board edge. The transform is fully determined:

| face | `pos[0]` | `pos[1]` |
|---|---|---|
| `front` / `back` | board X − W/2 | `wall + standoff + pcb + h/2 − oz/2` |
| `left` / `right` | board **Y** − D/2 | same |
| `top` / `bottom` | board X − W/2 | board Y − D/2 |

Side faces measure the vertical from `oz/2` because that is what `_cutout_solid` expects. Getting the left/right axis wrong puts the port on the wrong axis entirely, which is why it is encoded once and unit-tested rather than inlined.

Derivation happens inside `EnclosureSchema.ToJson`, so **the preview and the export are the same geometry** — a hole that lines up on screen but not in the print would be worse than no derivation.

**It refuses rather than guesses.** A cutout that names no component, names an unplaced one, sits more than `EdgeProximityMm` from any edge, or lacks a height for a side face keeps its authored value and reports `CUT-POS` **unproven**. On the shipped sample that is 2 derived and 3 refused — there is genuinely no USB, LED or gland component (the TP4056 is BOM-only).

### Clamped ports are reported

`clamp()` pulls an out-of-bounds feature back inside its face. That is the right geometry, but it was silent — ask for a port near a corner and you got one somewhere else. The build now returns `movedCutouts`, surfaced as `X-Foundry-Moved` and shown on the model badge.

## Lid style

`lid_style` was accepted and never read, so `snap` and `screw` produced **byte-identical** meshes: four screw bosses and four clearance holes through a lid the UI labelled snap-fit. A screw lid gets bosses and clearance holes; a snap lid gets neither, plus a retention bead around the bottom of its lip that flexes past the cavity wall.

## Fit checks

`Foundry.Core/Cad/EnclosureFit.cs` is pure: numbers in, `Finding`s out. No KiCad, no sidecar, no I/O.

- `HeightsFor` (`:47`) resolves every component through the **same** `FootprintMap.Resolve` decision the PCB build makes, so the case is measured against the parts that will actually be placed.
- `MinimumInner` (`:61`) derives the smallest cavity that works: board + 2×`SideClearanceMm`, and `standoff + PcbThicknessMm + tallest + LidClearanceMm` for depth. Constants at `:33-39`.
- `Check` (`:80`) emits `FIT-XY` (board doesn't fit the floor), `FIT-Z` (tallest part can't clear the lid), `FIT-UNDER` (pin tails need taller standoffs), `FIT-DIM` (no dimensions at all), and `FIT-UNK`.

### `unproven` is a real severity

`FIT-UNK` is emitted with severity **`unproven`** — the engine could not obtain a fact it needed. That is not a pass, and `ProjectValidator.Rollup` (`Foundry.Core/Validation/ProjectValidator.cs:28`) is the single place that decides: `fail` → `warn` → `unproven` → `pass`. Letting an unmeasured check fall through to "pass" is precisely how a validator ends up certifying what it never looked at.

`Finding.Advisory` treats `unproven` as guidance, so the UI shows it as text rather than offering an "Apply & re-run" button an AI edit can't satisfy.

The report card honours it too (`Foundry.App/ViewModels/Tabs/ValidationViewModel.cs`): `UnprovenCount` blocks the top grade and the verdict. Before this, a design with no failures and no warnings but N unmeasurable checks scored **A** and read *"safe to power on"*. It now grades **?** and says what it could not finish. There is no letter for "I don't know", so it doesn't invent one.

`unproven` has its own colour ramp — violet `#A78BFA`, deliberately outside the ok/warn/fail sequence so it never reads as a mild warning, and violet rather than grey so it reads as *unknown* rather than *disabled*. Tokens: `Brush.Unproven`, `Brush.TagUnprovenBg/Border`, `Brush.FindUnprovenBg`; all four severity converters in `Foundry.App/Converters/Converters.cs` carry the case.

**Verified by rendering**, per [[vault-conventions-local]] — and rendering caught a bug the tests had not: mechanical findings were appended after `RulesEngine.Validate` had already sorted and numbered its own, so they rendered below the passing rows with a blank number column. `ProjectValidator.Revalidate` now re-runs `RulesEngine.Order` over the combined set.

## Wiring

`ProjectValidator.Revalidate` runs `EnclosureFit.CheckProject` alongside the electrical rules, so mechanical findings land on the same report card. This is **pure** — `CheckProject` resolves footprints, runs the deterministic `PcbPlacer`, and derives the extent from the outline, none of which needs KiCad — so a case the board cannot go into is caught offline rather than at the printer. Pass `modelDir` to upgrade heights from curated/absent to measured. The call is wrapped so a geometry problem can never take down electrical validation.

A project whose `Enclosure.Inner` is absent or all-zero produces no mechanical findings: not every design has a case, and a missing one is not a defect.

### What this immediately caught

The shipped demo **fails its own fit check**. `DemoData.CreateSoilMoistureProject` declares a 62 × 48 × 26 mm cavity, but placing its parts gives a **102 × 67 mm** board — the 18650 holder alone is 88 × 21.75 mm, so no case that narrow can ever hold it. The declared dimensions were never derived from anything.

Chasing the original 220 mm figure surfaced a second defect and fixed it: `PcbPlacer`'s no-plan fallback used a uniform grid whose cells were sized to the **largest** part in both axes, so one 88 mm holder inflated every cell. It now First-Fit-Decreasing-Height bin-packs, the same shape the AI-plan path uses — 220 × 48 → 102 × 67 mm, a 35% area reduction, with the long dimension now bounded by the widest part itself. See [[pcb#default-placement]].

`Enclosure.Inner` is still filled from the model's JSON rather than from `MinimumInner`. That is deliberate for now — the engine **reports** the mismatch and offers the derived size as the fix, rather than silently overwriting what the user asked for.

## Tests

- `sidecar/test_enclosure.py` — 9 geometry tests against the real CSG kernel: watertightness, material removed on all six faces, top cutouts piercing the lid, bad cutouts not breaking the build. Runs in CI (`.github/workflows/ci.yml`), with `FOUNDRY_REQUIRE_CSG=1` so a missing kernel **fails** the lane instead of skipping it green.
- `Foundry.Tests/EnclosureFitTests.cs` — fit arithmetic, the rollup contract, and STEP heights checked against real KiCad models.
