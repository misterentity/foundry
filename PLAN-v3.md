# Foundry v3 — implementation plan

_Written 2026-08-02, after a 10-domain gap audit, a strategy panel, and two empirical gates.
Everything cited here was verified by reading the code or running it; nothing is inferred._

## The organising principle

**Correctness of shipped artifacts first, capability second, polish last.** A case whose ports don't
line up is worse than a case with no 3D preview. A BOM that renders invented stock levels like real
data is worse than a BOM with no stock column.

And one rule inherited from the audit, which every item below respects:

> Prove it from data we own, or report that we can't. Never guess and present the guess as a fact.

## Strategic context (why the order is what it is)

Two gates were run against real data:

- **Electrical verification of arbitrary boards — FAILED.** Over 15 real KiCad boards (4,841 nets),
  pin electrical type does not carry design *intent*: `passive` is 54% of all pins and `power_out`
  appears 46 times in 4,841 nets. Every approach produced 35–49% false failures on working boards.
- **Mechanical fit — PASSED.** Geometry is fully determined by data the app already owns. Component
  heights come out of KiCad's own STEP models accurately (R_0805 → 0.45 mm, DIP-28 → 3.68 mm,
  TO-220 → 18.77 mm). Foundry's own footprint vocabulary is 100% covered.

No competitor (Flux, Quilter, JITX, PromptPCB, EasyEDA) does the enclosure at all. So **the enclosure
is the strategic centre**, and Phase A comes first. The electrical gate's verdict does *not* kill
Phase B — it only rules out selling a general-purpose verifier for third-party boards. For Foundry's
own designs the intent is known, because the netlist came from a spec.

---

## Phase A — Make the enclosure correct

The enclosure now proves the board *fits* (`EnclosureFit`, landed). It does not yet produce a case you
could actually assemble.

### A1. Board mounting geometry — **do this first, it blocks A2**

**Evidence.** `sidecar/enclosure.py:259` `_standoff_posts` builds corner posts at a fixed 6 mm inset
from the **case** corners (`_boss_positions`, `:183`), rising `max(4.0, H - 2.0)` — nearly the full
cavity — with an M2 pilot, and the lid has matching clearance holes (`:138`). Those are **lid screw
bosses**. Grep for `pcb|board|shelf|standoff` finds nothing that mounts the PCB.

**Impact.** There is no board mounting at all. The printed case is a box with four tall posts and a
loose PCB. Worse, it blocks everything downstream: a port's height above the floor is undefined until
the board's Z position is real geometry, so cutout derivation cannot be correct without this.

**Proposal.** Introduce a real board plane. Add PCB standoffs at the board's mounting-hole positions
(short posts, height = `EnclosureFit` standoff, M2/M3 pilot), separate from the lid bosses. Add
`BoardSpec { WidthMm, DepthMm, ThicknessMm, MountHoles[] }` to the schema so the sidecar knows where
the board sits. Where the board has no mounting holes, emit `FIT-MOUNT` **unproven** rather than
inventing hole positions.

**Decidable?** Board outline and standoff height: yes, owned. Mounting-hole positions: only if the
placer emits them — today it doesn't, so this needs `PcbPlacer` to reserve and report mount holes.
**Tests.** `sidecar/test_enclosure.py`: posts exist at the requested XY, board plane clear of the
floor, volume drops for the pilot holes. `EnclosureFitTests`: standoff arithmetic.
**Effort:** L

### A2. Derive cutout positions from the placed board

**Evidence.** `Foundry.Core/Generation/ProjectGenerator.cs:429-436` reads `face`, `pos`, `size`
straight from the model's JSON. Nothing links a cutout to the component it exposes — `Cutout` has
`Label` (free text like "USB-C") and no component reference (`Project.cs:119`).

**Impact.** The USB hole lands wherever the model felt like putting it. The case is manufacturable and
wrong — the most expensive failure mode, because you only find out after printing.

**Proposal.** The transform is fully determined; I verified the conventions in `_cutout_solid`
(`sidecar/enclosure.py:194`):

| face | `pos[0]` (u) | `pos[1]` (v) |
|---|---|---|
| `top` / `bottom` | x offset from case centre | y offset from case centre |
| `front` (−Y) / `back` (+Y) | x offset from centre | z offset from `oz/2` |
| `left` (−X) / `right` (+X) | y offset from centre | z offset from `oz/2` |

The board spans `(0,0)–(W,D)` in placer coordinates with components at their centres, and the case is
centred in x/y with its inner floor at `z = t`. So for a component at `(cx, cy)` of height `h`:

```
u        = cx − W/2                     (front/back)   or  cy − D/2  (left/right)
v        = (t + standoff + pcb + h/2) − oz/2
face     = nearest board edge
size     = the component's courtyard extent on that face × its height
```

Add `Cutout.Ref` (component alias) to the model and ask for it in the generation system prompt.
**Only derive when the part is genuinely near an edge** (within a threshold); otherwise keep the
model's value and emit `CUT-UNPROVEN`. When `Ref` is absent or unresolvable, do not silently keep a
guess — report it.

**Decidable?** Provable from owned data *once A1 defines the board plane*.
**Tests.** Pure arithmetic for the transform (a `CutoutDerivationTests` mirroring `EnclosureFitTests`),
plus a CSG test that a derived cutout actually pierces the wall at the expected height.
**Effort:** L

### A3. Refuse out-of-bounds cutouts instead of silently sliding them

**Evidence.** `sidecar/enclosure.py:211-213` `clamp()` moves any feature that would fall off the face
back inside, with a 2 mm margin. Silently.

**Impact.** Ask for a port near a corner and you get a hole somewhere else, with no indication.

**Proposal.** Have the sidecar report clamped features in `stats`, and add a `CUT-OOB` **fail** finding
when a cutout's requested position differs from its applied position.
**Decidable?** Provable — it is pure arithmetic already being performed.
**Tests.** CSG test asserting the note is emitted; arithmetic test on the bounds check.
**Effort:** S

### A4. Stop presenting estimates as measurements

**Evidence.** `Enclosure.MassGrams` and `Enclosure.PrintTime` (`Project.cs:107-108`) are populated from
`EstimatePrintGrams` in the generation pass. Meanwhile the sidecar returns the **real** mesh in
`stats` and trimesh knows its exact volume. `SidecarClient.cs:80` reads `X-Foundry-Format`, and
`EnclosureViewModel.cs:243` picks the extension separately — worth confirming a 3MF request actually
writes a `.3mf` with 3MF bytes.

**Proposal.** Return `volume_mm3` from the sidecar; compute mass from volume × filament density and
label it as a computed estimate with its assumptions. Drive the written filename from the returned
`X-Foundry-Format`, and assert the mesh is watertight before handing the user a file to print.
**Decidable?** Volume and watertightness: provable. Print *time* is not — it depends on the slicer, so
either drop it or label it clearly as a rough estimate.
**Tests.** `sidecar/test_enclosure.py` for volume/watertightness; a round-trip test for the 3MF path.
**Effort:** M

### A5. Sidecar lifecycle

**Evidence.** `Foundry.Core/Sidecar/SidecarHost.cs` — needs a full read for port selection, orphan
handling, startup race and crash recovery. `App.xaml.cs:239-246` and `:263-269` dispose it on both
exit paths, which is right; the failure modes in between are the question.
**Effort:** M (pending the investigation's detail)

### A6. Make the sample self-consistent

**Evidence.** `DemoData.CreateSoilMoistureProject` declares a 62 × 48 × 26 mm case; its board places at
102 × 67 mm because it carries an 88 mm 18650 holder. `OpenSample` now revalidates, so the sample opens
on a genuine `FIT-XY` failure.

**Proposal.** Owner's call (see Open questions). Either give the demo a realistic case or a smaller
cell. Also decide whether `BuildDemoFindings` is now dead code.
**Effort:** S

---

## Phase B — The ground-truth spine

The audit's #1 theme, still untouched. This is the root cause behind six domains' worst findings.

### B1. Extract a `PartResolver`

**Evidence.** The `McuPinMap → SymbolPinMap → ChipCatalog` chain is inlined at exactly one call site,
`Foundry.Core/Pcb/PcbJob.cs:121-127`, where it correctly refuses. Everywhere else the model's own JSON
is treated as fact — `ProjectGenerator.cs:334` builds the KB from `project.Components`.

**Proposal.** Lift that chain into `Foundry.Core/Kb/PartResolver` and expose it to the generation path.
**Effort:** M

### B2. Grounding pass in generation

For every component the resolver recognises, verify each model-declared pin normalises to a real pad.
Emit `PIN-UNK` (**fail**) for pins that don't exist, `PIN-UNVERIFIED` (**unproven**) for parts Foundry
has no map for. The severity machinery already exists — `unproven` and `ProjectValidator.Rollup` landed
with the enclosure work.
**Effort:** L

### B3. Widen `ComponentSpec` and round-trip it

`ComponentSpec` (`Kb/ComponentSpec.cs`) lacks the fields the rules need: per-pin `RailV`/`AbsMaxV`/
`SourceMaMax`, and `MaxOutputMa`/`DropoutMv` on the part. `I2cAddress` **is** declared (`:40`) and
consumed (`RulesEngine.cs:250,265`) but never populated outside tests — it isn't in the system-prompt
schema and `MapComponents` doesn't parse it, so the I²C duplicate-address rule is dead code in every
real project. Round-trip every field through the prompt schema, `MapComponents`, and `BuildGenJson`.
**Effort:** L

### B4. Port-aware `PinMap`

`Firmware/PinMap.cs:111` derives GPIO numbers by regexing trailing digits, so `A0` becomes pin 0 (the
RX line) and `PA5`/`PB5` both become 5. `Simulation/GpioPinMap.cs:64-71` already has the port awareness
this needs. Refuse rather than emit a trailing-digit guess.
**Effort:** M

---

## Phase C — Stop presenting guesses as measurements

### C1. BOM price provenance
Live-vs-estimate is not labelled; stock and lead time are model-invented but rendered like real data.
Add `PriceSource` + `PricedAtUtc` to `BomLine`, bind a provenance chip, and suppress
stock/lead/distributor entirely when `SourcingService.Shared.IsLive` is false. **Effort:** M

### C2. Overview tab literals
Six hardcoded strings from the original design comp still ship in `OverviewView.xaml` (e.g. a
rev-over-rev cost delta) sitting directly beneath genuinely bound values. Bind or delete each.
**Effort:** S

---

## Phase D — Recovery and durability

### D1. Footprint and pin overrides — the missing escape hatch
`ComponentSpec.Footprint` (`Kb/ComponentSpec.cs:47`) is the designed override and **nothing produces
it**. When the fail-closed gate correctly refuses, the user is dead-ended. Add `footprint` to the
generation schema and persist per-alias `PinMapOverrides` consumed as step 0 of `PcbJob`'s resolution
chain. **Effort:** M

### D2. Editable netlist grid on the Wiring tab
Add/delete/retarget with aliases and pins populated from `Project.Components`, calling
`ProjectValidator.Revalidate` live. **Effort:** L

### D3. Atomic writes + schema version
`ProjectStore`/`ConfigStore`/`RevisionStore`/`TemplateStore` write in place — a crash mid-write
corrupts the library. Temp-file + `File.Move` + `.bak`, plus a `schemaVersion` and migration chain.
**Effort:** M

### D4. Delete confirmation
`ProjectsViewModel.Delete` removes a project with no confirmation. The `.rev` cleanup landed; the
dialog didn't. **Effort:** S

---

## Phase E — Verification infrastructure

- **E1.** `Foundry.App` is ~4,800 lines behind 10 tests. The `FOUNDRY_SHOT` hook makes render checks
  cheap — this session used it to catch two bugs the unit tests missed (the report card still graded
  "A" on unmeasurable checks; mechanical findings bypassed ordering). Add a launch-smoke lane. **M**
- **E2.** Extend the `FOUNDRY_REQUIRE_*` strictness pattern (already used for `FOUNDRY_REQUIRE_CSG`)
  to the other live lanes so a missing toolchain fails rather than skipping green. **S**

---

## Do not do

- **Do not build a general-purpose verifier for third-party boards.** The gate settled it: 35–49%
  false failures. Foundry can only prove things about designs where it owns the intent.
- **Do not auto-overwrite `Enclosure.Inner`** with `MinimumInner`. Report the mismatch and offer the
  derived size. Silently rewriting what the user asked for breaks the determinism boundary.
- **Do not chase PCB feature parity** (ground pours, 4-layer, manual placement editing). That race is
  lost to better-funded CAD tools and it is not where the defensibility is.
- **Do not invent mounting-hole positions** when the board has none. Emit `unproven`.

## Open questions (owner's call)

1. **The demo's battery.** An 88 mm 18650 holder cannot go in a pocket-sized case. Smaller cell, bigger
   case, or a different demo device? This affects the README screenshots.
2. ~~**Version bump.**~~ Cut **2.6.0** on 2026-08-02 — a minor bump, because this adds capability
   (board mounting, cutout derivation, printable exports) rather than only fixing defects.
3. **Print-time estimates** — keep them clearly labelled, or remove them as unknowable?
4. **`BuildDemoFindings`** — delete, or keep as seed data for the stored project state?
