# PRD — AI Hardware Design Studio (working title: "Foundry")

**Owner:** Dave
**Status:** Draft v1.0
**Last updated:** 2026-05-20
**Build target:** Claude Code CLI
**Reference product:** Blueprint.am by 3E8 Robotics (we clone its core and extend it)

---

## 1. Summary

A standalone **desktop app** that turns a single plain-language prompt into a
complete, buildable hardware project. Like Blueprint.am, the user describes what
they want ("a battery-powered soil-moisture sensor that texts me when my plants
are dry") and the app produces, in a chat-style workflow:

- a **hardware architecture** (the system broken into subsystems + chosen parts),
- a **bill of materials (BOM)**,
- a **wiring diagram**,
- a **step-by-step assembly guide**.

We then go **further than Blueprint.am** with four added capabilities:

1. **Printable CAD enclosures** — generate real, 3D-printable STL/3MF enclosures
   and mounts sized to the chosen components (not just a render).
2. **Firmware / code generation** — starter firmware (Arduino / MicroPython) with
   correct pin mappings and required libraries.
3. **Live sourcing + cart** — real-time price/availability and one-click BOM cart
   links via distributor APIs.
4. **Design validation** — deterministic electrical sanity checks (power budget,
   voltage/logic-level mismatches, pin conflicts, current draw).

Everything runs locally as a **native Windows 11 desktop application** (C# / .NET);
the user supplies their own Claude API key (and optional distributor API keys).

---

## 2. Goals and non-goals

### Goals
- Match Blueprint.am's core outputs from a single prompt, with chat-based iteration.
- Add the four extensions above so the output is *buildable end to end*: parts to
  buy, how to wire them, code to flash, an enclosure to print, and a validation
  pass that flags obvious mistakes.
- Maintain one canonical **Project** document as the single source of truth that
  every generator reads from and writes to, so outputs stay mutually consistent.
- Ship as an installable **Windows 11** desktop app that works offline for
  everything except AI calls and live sourcing.

### Non-goals (v1)
- Not a full EDA/PCB layout tool (no copper routing, Gerbers). Wiring diagrams are
  breadboard/connection-level, not manufacturable PCBs.
  > **Superseded in Track B (v2.x):** Foundry now does deterministic auto-placement → copper routing
  > (FreeRouting) → DRC fix loop → Gerber/Excellon export → assisted fab handoff. AI supplies placement
  > intent only; geometry/routing/DRC are computed and gated (DRC-clean + connectivity-verified). Still a
  > design aid with no manufacturability/net→pad guarantee — verify the Gerbers before ordering; parts with
  > no pin map are refused, never mis-wired. See PRD-v2 "Non-goals".
- No SPICE-level circuit simulation. Validation is rule-based, not simulated.
- No guarantee of manufacturability — outputs are design aids; the PRD requires
  clear "verify before you build" framing in the UI.
- No account system / cloud sync in v1 (local projects only).

---

## 3. Users and use cases

**Primary user:** makers, students, and hardware-curious engineers who can wire a
breadboard and flash a board but want to skip the slow early steps of part
selection, wiring research, and enclosure modeling.

**Secondary user:** experienced engineers using it as a fast first-draft / sanity
check.

**Representative prompts:**
- "A Raspberry Pi Pico weather station with temp, humidity, and pressure, on an
  OLED, powered by USB."
- "A motion-activated LED strip for under a desk, battery powered, that runs ~2
  weeks per charge."
- "An ESP32 garage-door sensor that reports open/closed to Home Assistant."

Each should yield architecture + BOM + wiring + guide + enclosure + firmware +
sourcing + validation, then be refinable by chat ("make it battery powered",
"swap the OLED for an e-ink display", "make the enclosure wall-mountable").

---

## 4. Competitive baseline and how we go further

| Capability | Blueprint.am | Foundry (this PRD) |
|---|---|---|
| Prompt → architecture | Yes | Yes (parity) |
| BOM | Yes | Yes + **live price/availability + cart links** |
| Wiring diagram | Yes | Yes (parity) |
| Mechanical render | Yes (render only) | **Printable CAD: STL/3MF enclosure + mounts** |
| Assembly guide | Yes | Yes (parity) |
| Chat iteration | Yes | Yes (parity) |
| Firmware/code | No | **Starter firmware + pin map + libraries** |
| Validation | Limited | **Rule-based electrical checks** |
| Delivery | Web | **Desktop app, local-first** |

---

## 5. Form factor and high-level architecture

**Form factor:** Native **Windows 11 desktop app**, C# / .NET 8, local-first.

**Recommended stack (justify or swap if you prefer):**
- **WPF on .NET 8 (C#, XAML, MVVM)** for the shell/UI. Rationale: it's the most
  pragmatic Windows-native choice *for this app specifically* because the 3D
  enclosure preview is a hard requirement and **HelixToolkit.Wpf** renders STL
  meshes in WPF out of the box. Use **CommunityToolkit.Mvvm** for MVVM plumbing.
  - *Alternative — WinUI 3 / Windows App SDK:* gives the more modern Windows 11
    Fluent/Mica look, but embedded real-time 3D is harder (no turnkey STL viewer —
    you'd host a SwapChainPanel + a 3D engine). Choose WinUI 3 only if the native
    Win11 aesthetic matters more to you than 3D-preview effort. Both options are
    C#/XAML/MVVM, so the core logic is portable between them.
- **C# orchestration layer** for AI calls, the Project model, BOM, wiring, firmware,
  sourcing, and validation (plain `HttpClient`, or the community `Anthropic.SDK`).
- **Bundled Python sidecar for CAD** — the enclosure generator uses a parametric
  CAD kernel (**build123d** or **CadQuery**, both OpenCASCADE-based) that exports
  STEP/STL/3MF directly, with **no Fusion or external CAD dependency**. The .NET app
  spawns it as a child process and talks to it over `127.0.0.1` (FastAPI). This keeps
  the CAD kernel in Python (where the mature bindings live) while the rest of the app
  stays native C#. (Alternative: OpenSCAD via CLI; build123d preferred for fidelity +
  STEP output.)

```
┌─────────────────────────────────────────────┐
│ Windows 11 app (.NET 8 / C#)                 │
│  ┌────────────────┐  ┌──────────────────┐    │
│  │ WPF UI (XAML)  │←→│ C# core          │    │
│  │ chat, BOM,     │  │ - AI orchestrator│    │
│  │ wiring, 3D     │  │ - Project store  │    │
│  │ (HelixToolkit) │  │ - sourcing/valid │    │
│  └────────────────┘  └────────┬─────────┘    │
│                               │ localhost     │
│                      ┌────────▼─────────┐     │
│                      │ Python sidecar   │     │
│                      │ build123d → CAD  │     │
│                      └──────────────────┘     │
└─────────────────────────────────────────────┘
        │ HTTPS                  │ HTTPS
   Anthropic API          Distributor APIs (Nexar/DigiKey/Mouser)
```

---

## 6. Core data model — the Project document

A single JSON document is the source of truth. Every generator reads it, mutates
its section, and re-validates. Chat turns produce diffs to this document.

```jsonc
{
  "id": "uuid",
  "title": "Soil moisture sensor",
  "spec": {
    "prompt": "original user text",
    "requirements": ["battery powered", "wireless alert", "outdoor"],
    "constraints": { "budget_usd": 40, "size": "palm", "power": "battery" }
  },
  "architecture": {
    "subsystems": [
      { "id": "mcu", "role": "controller", "component_ref": "esp32_devkit" },
      { "id": "sensor", "role": "soil moisture", "component_ref": "cap_sensor_v1" }
    ],
    "summary": "ESP32 reads a capacitive sensor and pushes to ..."
  },
  "components": [
    {
      "ref": "esp32_devkit", "name": "ESP32 DevKit v1", "category": "mcu",
      "specs": { "logic_v": 3.3, "input_v": [5,5], "pins": [...], "current_ma": 160 },
      "footprint": { "l_mm": 51, "w_mm": 28, "h_mm": 13, "mount_holes": [...] },
      "qty": 1, "mpn": "ESP32-DEVKITC-32E", "sourcing": { /* filled by §9.7 */ }
    }
  ],
  "connections": [   // the netlist that drives the wiring diagram
    { "from": "esp32_devkit.GPIO34", "to": "cap_sensor_v1.AOUT", "net": "signal" },
    { "from": "esp32_devkit.3V3",   "to": "cap_sensor_v1.VCC",  "net": "power" },
    { "from": "esp32_devkit.GND",   "to": "cap_sensor_v1.GND",  "net": "ground" }
  ],
  "enclosure": { /* parametric CAD schema, see §9.5 */ },
  "firmware": { "platform": "arduino", "files": [ /* see §9.6 */ ], "libraries": [...] },
  "assembly": [ { "step": 1, "text": "...", "refs": ["esp32_devkit"] } ],
  "validation": { "status": "pass|warn|fail", "findings": [ /* see §9.8 */ ] },
  "chat": [ /* message history for iteration context */ ]
}
```

**Key invariant:** `connections` and `components[].specs.pins` are authoritative;
the wiring diagram, firmware pin map, validation, and enclosure cutouts are all
*derived* from them, so they never disagree.

---

## 7. AI orchestration pipeline

Use the Claude API with **tool use / structured outputs**. Rather than one giant
prompt, run a staged pipeline; each stage emits a validated slice of the Project
document. Stages can re-run individually when the user iterates by chat.

1. **Spec extraction** — prompt → `spec.requirements` + `constraints`. Ask
   clarifying questions in chat only if a hard requirement is missing.
2. **Architecture + component selection** — choose subsystems and concrete parts,
   writing `architecture` + `components` (with electrical specs and footprints
   pulled from the component KB, §10).
3. **Wiring / netlist** — produce `connections` from component pin definitions.
4. **Firmware** — generate `firmware` from the netlist + parts (§9.6).
5. **Enclosure** — produce the parametric CAD schema from component footprints (§9.5).
6. **Assembly guide** — derive ordered steps from architecture + connections.
7. **Validation** — deterministic checks over the assembled document (§9.8); may
   loop back to stage 2 with findings.

**Iteration model:** a chat message is interpreted as an instruction to modify the
Project. Claude returns which stages to re-run + a structured edit; the app
re-runs only those stages and re-validates, preserving everything else.

**Output discipline:** every stage uses a strict JSON schema (tool input schema or
response prefilling). Defensive parse + schema validation before mutating the
Project; never crash on a malformed response — surface a readable error.

---

## 8. Feature specifications

### 8.1 Prompt + chat (parity)
Multi-line prompt box and a persistent chat thread. Each turn updates the Project
and re-renders affected panels. Show a per-stage progress indicator
("Selecting parts… Wiring… Generating enclosure…").

### 8.2 Architecture & BOM (parity + sourcing hook)
Render the subsystem breakdown and a BOM table (qty, name, MPN, est. price). BOM
rows link to the live sourcing panel (§8.7). Allow manual part swaps that re-run
downstream stages.

### 8.3 Wiring diagram (parity)
Render `connections` as a readable diagram. v1 approach: lay out component blocks
with labeled pin headers and draw nets as colored edges (power=red, ground=black,
signal=other), exported to **SVG** and PNG. Use an in-app graph/diagram renderer
(e.g., a force/orthogonal layout over the netlist). A breadboard-style view is a
future enhancement.

### 8.4 Assembly guide (parity)
Ordered, numbered steps referencing components and connections, exportable to
Markdown/PDF. Each step links to the relevant wiring net and BOM item.

### 8.5 [FURTHER] Printable CAD enclosures
Generate a **parametric enclosure** sized to the component footprints, exported as
**STL/3MF** (and STEP for editing). Reuse a closed **design schema** of operations
so generation is reliable (Claude fills the schema; the Python sidecar builds it
deterministically — Claude never writes CAD code).

Enclosure schema (built by build123d/CadQuery in the sidecar):
```jsonc
{
  "type": "box_enclosure",
  "inner": [l_mm, w_mm, h_mm],       // sized from component bounding boxes + clearance
  "wall_mm": 2.0,
  "lid": { "style": "snap|screw", "screw": "M3" },
  "standoffs": [ { "for": "esp32_devkit", "holes": [[x,y],...], "height_mm": 4 } ],
  "cutouts": [
    { "face": "side", "shape": "rect", "size": [w,h], "pos": [x,y], "for": "usb" },
    { "face": "top",  "shape": "circle", "d": 12, "pos": [x,y], "for": "button" }
  ],
  "vents": { "pattern": "slots", "face": "side", "count": 6 },
  "mounts": { "type": "wall|din|none" }
}
```
Cutouts and standoffs are derived from `components[].footprint` so ports line up.
The UI shows a live 3D preview (HelixToolkit.Wpf) of the exported mesh.

> Note: this is the same schema-driven, deterministic-builder pattern from the
> earlier Fusion concept, but implemented with build123d so the desktop app needs
> no external CAD install.

### 8.6 [FURTHER] Firmware / code generation
From the netlist + parts, generate a starter sketch:
- **Platforms:** Arduino C++ (default) and MicroPython; chosen by MCU.
- **Outputs:** `firmware.files[]` (e.g., `main.ino` / `main.py`), a **pin-map header**
  generated directly from `connections` (single source of truth — no hand-typed
  pins), and a `libraries[]` list (with versions) for the chosen sensors/displays.
- Compiles conceptually against the selected board; include `// TODO` markers for
  user secrets (Wi-Fi creds, API tokens). Export as a ready-to-open project folder.

### 8.7 [FURTHER] Live sourcing + cart
- **Aggregator:** prefer **Nexar / Octopart API** (covers DigiKey, Mouser, etc.) for
  unified price/availability by MPN; fall back to direct **DigiKey** and **Mouser**
  APIs if configured. Optional Amazon product search for hobbyist parts.
- For each BOM line: show lowest price, stock, lead time, distributor, datasheet
  link; compute total project cost.
- **Cart:** build per-distributor cart/BOM-upload links (DigiKey and Mouser both
  support BOM/cart URL upload) so the user checks out in one click.
- Cache results; degrade gracefully (show "offline / no key" state) when sourcing
  APIs aren't configured.

### 8.8 [FURTHER] Design validation
A deterministic rules engine over the assembled Project (not AI-judged), producing
`validation.findings[]` with severity (`info|warn|fail`):
- **Power budget:** sum component `current_ma` vs. supply/regulator/battery capacity;
  warn on overdraw; estimate battery life if battery-powered.
- **Voltage / logic level:** flag a 5V output driving a 3.3V-only input, or VCC
  mismatches, per `specs.logic_v` / `input_v`.
- **Pin conflicts:** two nets claiming the same MCU pin; use of input-only or
  strapping pins for outputs (board-specific rules, e.g., ESP32 GPIO34–39 input-only).
- **Connector/qty sanity:** every component powered and grounded; required pull-ups
  present; I²C address collisions.
Findings link to the offending connection/component and, where possible, offer an
auto-fix that re-runs the relevant stage.

### 8.9 Settings panel
A dedicated **Settings** view (the first-run wizard reuses the same controls) where
the user configures their Claude access and app preferences.

**Claude API key**
- Masked input field for the user's Anthropic API key.
- **"Test connection"** button: validates the key by calling
  `GET https://api.anthropic.com/v1/models` (cheap, no token spend); shows a clear
  ✓ valid / ✗ invalid result with the reason on failure.
- On save, the key is written to **Windows Credential Manager** (DPAPI-backed),
  never to the Project file or logs. After saving, the UI shows only a masked
  summary (e.g. `sk-ant-…AB12`) with "Replace" / "Remove" actions — never the full
  key again.

**Claude model selector**
- A **dropdown of public Claude models** the user can choose from. Each entry shows
  a friendly name + the API model id, and the choice persists as the default for
  all generation stages.
- **Populate dynamically:** when Settings opens (and after a valid key is entered),
  call `GET /v1/models` and list the returned public models, so the dropdown stays
  current without an app update.
- **Curated fallback list** (used offline or if the call fails), most → least capable:
  - Claude Opus 4.6 — `claude-opus-4-6` (most capable; best for complex full designs)
  - Claude Sonnet 4.6 — `claude-sonnet-4-6` (recommended default; fast + strong)
  - Claude Haiku 4.5 — `claude-haiku-4-5-20251001` (fastest/cheapest; good for small chat edits)
- Optional: a **per-stage model override** (e.g., Haiku for quick chat tweaks, Opus
  for the full first pass) — default off, single model for everything.

**Other settings (grouped)**
- *Generation:* max output tokens, temperature, default firmware platform
  (Arduino C++ / MicroPython).
- *Export:* default output folder, enclosure format (STL / 3MF / STEP), units (mm).
- *Sourcing (optional):* Nexar/Octopart, DigiKey, Mouser API keys — each with its
  own masked field + Test button, also stored in Credential Manager. Sourcing
  features degrade gracefully when these are blank.

Non-secret settings persist in a local config file; all secrets live in Credential
Manager. An invalid/blank Claude key disables generation with a clear "add your API
key in Settings" prompt rather than failing mid-run.

---

## 9. Component knowledge base

Validation, wiring, firmware pin maps, and CAD cutouts all need structured
component data (electrical specs, pinouts, physical footprint). v1:
- Ship a curated local JSON KB of common maker parts (popular MCUs, sensors,
  displays, drivers, power) with `specs`, `pins`, and `footprint`.
- Augment at runtime from the sourcing API (specs/datasheet) and let Claude
  propose KB entries for unknown parts, flagged as "unverified" until the user
  confirms.
- KB schema matches `components[]` in §6 so entries drop straight into a Project.

---

## 10. Functional requirements

| # | Requirement |
|---|-------------|
| F1 | User enters a prompt and receives architecture, BOM, wiring diagram, and assembly guide. |
| F2 | User can refine the design through chat; only affected stages re-run. |
| F3 | App generates a printable enclosure (STL/3MF) sized to the parts, with a 3D preview. |
| F4 | App generates starter firmware with a pin map derived from the netlist + a library list. |
| F5 | BOM shows live price/availability and produces distributor cart links (when keys set). |
| F6 | App runs validation and lists findings with severity and (where possible) auto-fixes. |
| F7 | All outputs export to disk: BOM (CSV), wiring (SVG/PNG), guide (MD/PDF), enclosure (STL/3MF/STEP), firmware (project folder). |
| F8 | Projects save/load locally as a single Project JSON. |
| F9 | Missing API keys / offline state degrade gracefully with clear messaging, never crashes. |
| F10 | Every screen carries a "design aid — verify before building" disclaimer. |
| F11 | A Settings panel lets the user enter + test their Claude API key and pick which public Claude model to use; the choice applies to all generation. |
| F12 | The model dropdown is populated live from the Claude `/v1/models` endpoint, with a curated fallback list when offline. |

---

## 11. Technical requirements and constraints

- **Platform:** Windows 11, .NET 8, C#. Target `net8.0-windows`.
- **Local-first:** all generation works offline except Claude calls and live
  sourcing. CAD runs locally in the Python sidecar.
- **No external CAD install:** enclosure generation via bundled build123d/CadQuery.
- **Claude API:** Messages API over HTTPS (`HttpClient` or `Anthropic.SDK`),
  structured/tool-use outputs, model configurable (default a Sonnet-class model;
  allow Opus for hard designs).
- **Sourcing APIs:** Nexar/Octopart primary; DigiKey/Mouser optional; all keys
  user-supplied and stored in **Windows Credential Manager** (DPAPI-backed), never
  plaintext.
- **Determinism boundary:** AI fills schemas; geometry, pin maps, and validation
  are computed deterministically from the Project — AI never emits final CAD code
  or final validation verdicts.
- **Packaging:** **MSIX** installer for Windows 11 (or WiX/Inno Setup if unsigned);
  the Python sidecar is bundled via PyInstaller (single-folder) and shipped inside
  the app package, spawned at runtime.
- **3D preview:** HelixToolkit.Wpf mesh viewer hosted in the WPF UI.
- **Units:** CAD schema authored in millimeters.

---

## 12. UX flows

1. **First run:** prompt for Claude API key (and optional sourcing keys), stored in
   Windows Credential Manager. Show a sample project.
2. **New project:** type prompt → progress through stages → tabbed result view
   (Overview / BOM / Wiring / Enclosure / Firmware / Validation / Guide / Settings).
3. **Iterate:** chat instruction → diff preview ("I'll swap the OLED and re-run
   wiring, firmware, enclosure cutouts") → apply → panels update.
4. **Buy:** open BOM tab → review live prices → "Add all to DigiKey cart".
5. **Build:** export enclosure to slicer, open firmware folder in IDE, follow guide.

---

## 13. Error handling

- AI stage failure → retry once, then a readable error in that panel; other panels
  remain valid.
- Malformed AI JSON → defensive parse + schema validation; report the bad field.
- CAD sidecar error → show the failing operation; keep electrical outputs intact.
- Sourcing API error/no key → "pricing unavailable" badge, manual MPN links.
- Validation never blocks export; it informs.
- No uncaught exception may crash the app shell.

---

## 14. Security and privacy

- All API keys in Windows Credential Manager (DPAPI-backed); never logged, never
  written to the Project file.
- Project files contain no secrets (firmware uses `TODO` placeholders for creds).
- Network calls limited to Anthropic + configured distributor APIs; list these
  explicitly in settings.

---

## 15. Phased build plan (suggested for Claude Code)

**Phase 0 — Shell.** .NET 8 WPF scaffold (MVVM via CommunityToolkit.Mvvm),
settings + Windows Credential Manager, Project model + save/load, empty tabbed UI.

**Phase 1 — Core clone (parity).** Spec → architecture → BOM → netlist → wiring SVG
→ assembly guide, with chat iteration. Curated component KB. This alone matches
Blueprint.am.

**Phase 2 — Validation.** Rules engine over the Project (power, voltage, pins);
findings UI + auto-fix hooks.

**Phase 3 — Firmware.** Pin-map generation from netlist + sketch + library list;
export project folder.

**Phase 4 — Enclosure CAD.** Python sidecar with build123d; enclosure schema →
STL/3MF/STEP; three.js preview; cutouts derived from footprints.

**Phase 5 — Live sourcing + cart.** Nexar/Octopart integration; price/stock on BOM;
distributor cart links; total cost.

**Phase 6 — Polish + packaging.** Exports, signed installers, sample projects,
disclaimers, onboarding.

Each phase is independently demoable.

---

## 16. Risks and mitigations

| Risk | Mitigation |
|---|---|
| AI picks wrong/incompatible parts | Validation stage catches electrical mismatches; curated KB; user can swap parts. |
| Wiring/diagram correctness | Netlist is the source of truth and is validated; diagram is a pure render of it. |
| CAD enclosure doesn't fit real parts | Cutouts/standoffs derived from KB footprints + clearance; show dimensions; STEP export for manual edit. |
| Sourcing API access/cost/keys | Optional, cached, graceful degradation; aggregator first to minimize integrations. |
| Hallucinated component specs | Unknown parts flagged "unverified"; prefer KB + datasheet-backed specs. |
| Liability ("it caught fire") | Prominent "design aid, verify before building" disclaimers; validation warnings surfaced. |
| Packaging a Python sidecar in a .NET app | Standard pattern (PyInstaller single-folder shipped in the MSIX, spawned over localhost); pin versions; health-check the sidecar on startup. |
| 3D in WPF being limited | Use HelixToolkit.Wpf (turnkey STL viewer); if richer 3D is needed later, evaluate HelixToolkit.SharpDX. |

---

## 17. Future enhancements

- Breadboard-accurate and Fritzing-style wiring views; export to KiCad netlist.
- True PCB generation (footprints → layout) as a separate module.
- Component substitution suggestions ranked by price/stock/availability.
- Firmware over-the-air flashing / serial flashing from the app.
- Cloud project sync + sharing; community template gallery.
- Cost/latency routing (Haiku for simple edits, Opus for full designs).
- SPICE/logic simulation for deeper validation.

---

## 18. Target repository structure

```
Foundry.sln                 # .NET 8 solution
  Foundry.App/              # WPF UI (net8.0-windows)
    App.xaml / MainWindow.xaml
    Views/                  # Overview, BOM, Wiring, Enclosure, Firmware, Validation, Guide, Settings
    ViewModels/             # MVVM (CommunityToolkit.Mvvm)
    Chat/
    Viewer/                 # HelixToolkit.Wpf 3D preview control
  Foundry.Core/             # C# class library — all logic, UI-agnostic
    Project/                # Project model + load/save + diffing
    Ai/
      AnthropicClient.cs    # Messages API (HttpClient or Anthropic.SDK), tool use
      Pipeline.cs           # staged generation orchestrator
      Prompts/              # per-stage system prompts + JSON schemas
    Wiring/                 # netlist -> SVG layout
    Firmware/               # sketch + pin-map generation
    Sourcing/               # Nexar/DigiKey/Mouser clients + cart links
    Validation/             # rules engine
    Kb/                     # curated component knowledge base (JSON)
    Sidecar/                # spawn + HTTP client for the Python CAD service
    Security/               # Windows Credential Manager wrapper
  sidecar/                  # Python CAD service (bundled, PyInstaller)
    server.py               # FastAPI on 127.0.0.1
    enclosure.py            # build123d/CadQuery: schema -> STL/3MF/STEP
    requirements.txt
  Foundry.Tests/            # xUnit: validation rules, schema parsing, pin maps
  build/                    # MSIX packaging + PyInstaller spec
  README.md                 # setup, keys, packaging, disclaimers
```

---

## 19. Acceptance criteria (v1 done)

- A single prompt yields architecture, BOM, wiring SVG, and assembly guide,
  refinable by chat — i.e., Blueprint.am parity.
- The same project produces: a printable STL/3MF enclosure with correctly placed
  port cutouts, compilable starter firmware with a netlist-derived pin map, live
  BOM pricing with working distributor cart links, and a validation report that
  catches an injected fault (e.g., a 5V sensor on a 3.3V-only pin).
- All outputs export to disk; projects save/load.
- Removing every API key leaves the app stable with clear "unavailable" states.
- Disclaimers present on generated build instructions.

---