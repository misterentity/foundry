# PRD — Foundry v2: "Design → Fabrication & Verification"

**Owner:** Dave
**Status:** Draft v2.0
**Builds on:** Foundry v1.0 (single prompt → architecture, BOM, wiring, AI firmware, printable enclosure, deterministic validation; chat iteration; local-first WPF/.NET 8 + Python CAD sidecar).
**Last updated:** 2026-05-22

---

## 1. Summary & thesis

v1 proves the loop: one prompt → a *buildable-on-paper* project, refinable by chat. v2 closes the
gap between "buildable on paper" and **"actually fabricated, flashed, and verified."** Where v1 ends
at exports, v2 carries the project the last mile: the firmware **compiles** (we prove it), the design
exports to **the tools makers actually use next** (KiCad, breadboard view), validation gets **deeper
and more trustworthy**, sourcing gets **smarter** (ranked substitutes), and good designs become
**reusable** (templates, shareable bundles). Still local-first, still one canonical Project document.

**Non-goals (unchanged from v1):** no full SPICE, no cloud account system, no manufacturability guarantee
(design aid — verify before building).

> **Update (Track B shipped):** the original "no copper auto-routing/Gerbers" non-goal no longer holds.
> Foundry now ships **Track B**: deterministic auto-placement → FreeRouting copper routing → DRC fix loop →
> Gerber/Excellon export → assisted (never auto-submitted) fab handoff (JLCPCB/PCBWay). The determinism
> boundary is preserved (AI supplies placement *intent* only; geometry/routing/DRC are computed) and the
> output is gated DRC-clean **and** connectivity-verified. It remains a **design aid, NOT a manufacturability
> guarantee, with no net→pad correctness guarantee** — review the Gerbers in a viewer before ordering. Logical
> MCU pins resolve to real pads only for parts with a pin map (ESP32 / ESP8266 / RP2040 + any KiCad part whose
> symbol resolves); anything else is **refused** (never silently mis-wired).

---

## 2. Pillars & functional requirements

### Pillar A — Firmware that actually builds (and flashes)
The biggest credibility gap in v1: the AI firmware *looks* right but is never compiled. v2 proves it.
- **G1 — Compile check.** Bundle/auto-install **arduino-cli** (and detect a local Python for MicroPython
  lint). A "Verify build" action compiles the generated sketch for the selected board, parses errors,
  and surfaces them as firmware findings (line-linked). Re-runnable after edits.
- **G2 — One-click flash.** Detect connected serial boards; flash the compiled binary
  (arduino-cli upload / esptool). Show progress + result. Degrades to "no board detected."
- **G3 — AI build-fix loop.** When a compile fails, offer "Fix build" → the AI revises firmware with
  the compiler errors as context; re-compile until clean or N attempts.

### Pillar B — Fabrication bridge (beyond a picture of the wiring)
- **G4 — KiCad netlist export.** Export `connections` + `components` to a **KiCad `.net`** file (and an
  `.csv` netlist), so users can drop into PCB layout. Deterministic, no AI.
- **G5 — Breadboard view.** A breadboard-style wiring rendering (half/full breadboard, components placed,
  jumper colors by net) in addition to the schematic view; export PNG/SVG.
- **G6 — Fritzing-friendly + pin report.** A per-MCU pin-assignment table (pin → net → peripheral) export.

### Pillar C — Validation 2.0 (deeper, more trustworthy)
Extend the deterministic rules engine (AI still never decides verdicts):
- **G7 — Decoupling & pull-ups.** Flag missing decoupling caps near ICs, missing I²C/reset pull-ups,
  missing current-limit resistors on LEDs — with deterministic auto-fix (add the part + nets).
- **G8 — Per-pin current & totals.** Per-pin source/sink limits (board rules), regulator dropout
  (Vin−Vout vs load), connector gender/mate sanity.
- **G9 — Validation report card.** A graded summary (A–F) + a one-line "is this safe to power on?"
  verdict, with the deterministic reasoning shown.

### Pillar D — Smarter sourcing & substitution
- **G10 — Ranked substitutes.** For each BOM line, propose alternates ranked by price/stock/availability
  (KB + sourcing API); "swap to cheaper / in-stock" re-runs downstream and re-validates.
- **G11 — Multi-distributor cart.** DigiKey + Mouser BOM/cart links; total cost per distributor; lead-time roll-up.
- **G12 — Budget mode.** A target budget; the AI/KB optimizes part choices to hit it, flagging tradeoffs.

### Pillar E — Reuse: templates, sharing, multi-board
- **G13 — Template gallery.** Curated starter templates (weather station, BLE sensor, motor driver…) that
  pre-fill a project; "save as template" from any project.
- **G14 — Project bundle.** Export/import a single `.foundryproj` (zip: Project JSON + firmware + STL/3MF +
  PDF + wiring SVG + KiCad net) for sharing/backup; double-click import.
- **G15 — Multi-board / modules (stretch).** Projects composed of multiple boards/modules with inter-board
  connectors; validation across the boundary.

---

## 3. Acceptance criteria (v2 done)

- A generated Arduino project **compiles** via arduino-cli from inside the app; a deliberately broken
  edit surfaces the compiler error line-linked, and "Fix build" recovers it. (G1, G3)
- The project exports a **KiCad netlist** that imports cleanly into KiCad's layout. (G4)
- Validation 2.0 catches a **missing I²C pull-up** and a **missing LED current-limit resistor** and offers
  a working auto-fix. (G7)
- Each BOM line shows at least one **ranked substitute**; swapping one re-runs downstream + re-validates. (G10)
- A project can be **saved as a template** and a new project started from it; a `.foundryproj` bundle
  round-trips (export → import → identical project). (G13, G14)
- Everything still degrades gracefully with no keys/tools installed; nothing crashes.

---

## 4. Technical notes & constraints

- **arduino-cli**: detect on PATH; offer to download the official zip to `%LocalAppData%/Foundry/tools`
  on first use (no admin). Compile in a temp project dir; parse `--format json` diagnostics. Flashing uses
  the same CLI; board/port detection via `arduino-cli board list`.
- **KiCad netlist**: emit the classic `(export (version D) (components ...) (nets ...))` S-expression
  `.net` format — pure string generation in `Foundry.Core/Fabrication/`, fully unit-tested. No KiCad install needed.
- **Breadboard view**: extend the WPF rendering layer; reuse the netlist; export via the existing
  RenderTargetBitmap (PNG) + an SVG serializer like the schematic.
- **Validation 2.0**: new rules in `RulesEngine`; auto-fixes via `ProjectValidator` (add part + nets).
  Keep the determinism boundary — AI never decides verdicts.
- **Sourcing**: extend `SourcingService`/`NexarSourcingProvider`; substitutes from the KB + API. Still
  optional/keyed/cached/graceful.
- **Bundle**: `.foundryproj` = zip via `System.IO.Compression`; import validates + lands in the library.
- Determinism, local-first, Credential-Manager-only secrets, "design aid" disclaimers — all unchanged.

---

## 5. Phased delivery (each independently shippable, v1.1 → v2.0)

1. **v1.1 — Fabrication bridge:** KiCad netlist export (G4) + pin report (G6). *(deterministic, no deps)*
2. **v1.2 — Firmware build:** arduino-cli compile check (G1) + AI build-fix (G3).
3. **v1.3 — Validation 2.0:** decoupling/pull-up/LED-resistor rules + auto-fixes (G7), report card (G9).
4. **v1.4 — Sourcing:** ranked substitutes (G10) + multi-distributor cart (G11).
5. **v1.5 — Reuse:** template gallery (G13) + `.foundryproj` bundle (G14).
6. **v1.6 — Flash & breadboard:** one-click flash (G2) + breadboard view (G5).
7. **v2.0 — Polish + multi-board (stretch G15)** + docs/screenshots refresh; final release.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| arduino-cli download/size/offline | Detect first; optional on-demand download; compile is a feature, not required to use the app. |
| Compile times slow the UI | Run async with progress + cancel; cache the core install. |
| KiCad format drift | Target the stable s-expr netlist; unit-test against a known-good fixture; it's import-only (forgiving). |
| Substitute quality without sourcing keys | KB-based alternates offline; API-ranked when keyed; label confidence. |
| Scope creep | Ship pillar by pillar (v1.1…); each phase is independently demoable and releasable. |
