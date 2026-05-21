# Foundry — Implementation Plan

> **For agentic workers:** This plan follows the phased build in PRD §15. Phases 0–1
> are specified in build-level detail; Phases 2–6 are structured outlines that get
> promoted to full detail when their turn comes. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A native Windows 11 desktop app (C# / .NET 8 / WPF) that turns one
plain-language prompt into a complete, buildable hardware project — architecture,
BOM, wiring, assembly guide, plus printable CAD enclosure, starter firmware, live
sourcing, and rule-based validation — all derived from one canonical Project document.

**Architecture:** WPF + MVVM (CommunityToolkit.Mvvm) shell over a UI-agnostic
`Foundry.Core` library. Core owns the Project model, a staged Claude pipeline (behind
an interface, stubbed first), the wiring/firmware/validation/sourcing generators, a
curated component KB, and a Windows Credential Manager wrapper. A bundled Python
sidecar (build123d, FastAPI on 127.0.0.1) does CAD; the app spawns it and talks HTTP.
The AI fills JSON schemas; geometry, pin maps, and validation verdicts are computed
deterministically from the Project so outputs never disagree.

**Tech stack:** .NET 8 (`net8.0-windows` / `net8.0`), WPF, XAML, CommunityToolkit.Mvvm,
HelixToolkit.Wpf (3D STL preview), System.Text.Json, xUnit. Python 3.13 sidecar with
build123d + FastAPI + uvicorn, bundled via PyInstaller. Fonts: Instrument Serif +
JetBrains Mono (bundled). Anthropic Messages API via `HttpClient`.

---

## Target toolchain (verified on this machine)

- **dotnet SDK** 9.0.314 / 10.0.101 present (no 8.0 SDK — fine, SDK builds down-level).
- **WindowsDesktop runtime** 8.0.22 / 8.0.27 present → `net8.0-windows` WPF apps run.
- **Python** 3.13.11 present (sidecar dev; PyInstaller bundling deferred to Phase 4).
- **git** 2.52 present. Repo is **not** yet initialized (`git init` in Phase 0).

If a `net8.0` targeting-pack restore ever fails, fall back to `net9.0-windows`
(all packages support it) and record the swap — but default is `net8.0` per PRD §11.

---

## Repository / solution structure (PRD §18)

```
Foundry.sln
  Foundry.App/                      # WPF UI — net8.0-windows
    App.xaml(.cs)                   # app bootstrap, DI composition root
    MainWindow.xaml(.cs)            # custom-chrome window, hosts ShellView
    Themes/
      Tokens.xaml                   # design tokens (colors, metrics) ← styles.css :root
      Controls.xaml                 # btn/tag/kicker/card/table styles
      Fonts/                        # InstrumentSerif*.ttf, JetBrainsMono*.ttf
    Controls/
      WindowChromeView.xaml         # titlebar (brand, crumb, min/max/close) + statusbar
      IconControl.cs                # 16x16 stroke icons (port of shared.jsx Icon)
      PipelinePill.xaml             # chat per-stage pipeline indicator
    Views/
      OnboardingView.xaml           # first-run / API keys (2 tabs: anthropic, sourcing)
      ProjectsView.xaml             # library: hero, toolbar, featured card, grid
      ShellView.xaml                # workspace: rail | main(tabbar+body) | chat
      Tabs/
        OverviewView.xaml  BomView.xaml  WiringView.xaml  EnclosureView.xaml
        FirmwareView.xaml  ValidationView.xaml  GuideView.xaml
      SettingsView.xaml             # Phase 0 stub → full in Phase 6 (reuses onboarding controls)
    Rendering/
      WiringDiagram.cs              # netlist → WPF DrawingVisual/Canvas (port wiring-svg.jsx)
      EnclosureIso.cs               # iso "3D" preview vector (port enclosure-svg.jsx) until Helix
      MiniDiagram.cs                # project-card mini netlist preview
    ViewModels/
      MainViewModel.cs ShellViewModel.cs OnboardingViewModel.cs ProjectsViewModel.cs
      Tabs/*ViewModel.cs  SettingsViewModel.cs
    Converters/                     # bool→vis, net→brush, severity→brush, etc.
  Foundry.Core/                     # net8.0 — all logic, UI-agnostic
    Project/
      Project.cs                    # canonical model (§6) + nested records
      ProjectStore.cs               # load/save single JSON, diffing later
      DemoData.cs                   # the soil-moisture sample project (port data.jsx)
    Ai/
      IAnthropicClient.cs           # interface (Messages API + /v1/models)
      AnthropicClient.cs            # real HttpClient impl
      StubAnthropicClient.cs        # offline stub returning canned stages
      IPipeline.cs / Pipeline.cs    # staged orchestrator (spec→arch→wiring→…)
      Prompts/                      # per-stage system prompts + JSON schemas (Phase 1+)
      ModelCatalog.cs               # curated fallback model list (§8.9)
    Wiring/    NetlistLayout.cs      # netlist → positioned blocks + routed nets (data only)
    Firmware/  PinMap.cs            # connections → pin-map header (Phase 3)
    Sourcing/  *.cs                 # Nexar/DigiKey/Mouser clients + cart links (Phase 5)
    Validation/ Rules.cs RulesEngine.cs   # deterministic checks (Phase 2)
    Kb/        ComponentKb.cs kb.json     # curated parts (specs/pins/footprint)
    Sidecar/   SidecarClient.cs SidecarHost.cs  # spawn + HTTP (Phase 4)
    Security/  CredentialStore.cs   # Windows Credential Manager (DPAPI) wrapper
  Foundry.Tests/                    # xUnit
    ProjectStoreTests.cs ValidationTests.cs PinMapTests.cs SchemaParseTests.cs
  sidecar/                          # Python CAD service (Phase 4)
    server.py enclosure.py requirements.txt
  build/                            # MSIX + PyInstaller (Phase 6)
  PLAN.md  PRD.md  README.md  design-reference/
```

---

## Design-token mapping (styles.css `:root` → `Themes/Tokens.xaml`)

Every CSS custom property becomes a WPF resource. Colors → `SolidColorBrush` (keyed
`Brush.*`) **and** a raw `Color` (keyed `Color.*`) where gradients/animation need it.
Metrics → `System.Double` resources. The accent is overridable at runtime (the
prototype's tweak) via a dynamic resource.

| CSS token | Value | WPF resource key |
|---|---|---|
| `--bg` | `#07070a` | `Brush.Bg` |
| `--surface-0..3` | `#0c0c10 #111116 #16161c #1d1d24` | `Brush.Surface0..3` |
| `--hairline/-2/-3` | `#22222b #2c2c36 #3a3a46` | `Brush.Hairline/2/3` |
| `--ink/-soft/-mute/-faint` | `#ededee #b6b6bb #6a6a72 #44444c` | `Brush.Ink/Soft/Mute/Faint` |
| `--accent/-2` | `#ff5a1f #ff8a5a` | `Brush.Accent/Accent2` (DynamicResource) |
| `--ok/warn/fail/info` | `#4ade80 #fbbf24 #ef4444 #5dd2ff` | `Brush.Ok/Warn/Fail/Info` |
| `--power/ground/signal/signal-2` | `#ff4040 #888 #5dd2ff #c084fc` | `Brush.Power/Ground/Signal/I2c` |
| `--rail-w/chat-w` | `212 / 360` | `Metric.RailW / Metric.ChatW` |
| `--titlebar-h/tabbar-h/statusbar-h` | `36 / 38 / 28` | `Metric.TitlebarH / TabbarH / StatusbarH` |
| `--serif` | Instrument Serif | `Font.Serif` (FontFamily) |
| `--mono` | JetBrains Mono | `Font.Mono` (FontFamily) |

`color-mix(in oklab, X n%, transparent)` (used for tag/finding tints) has no XAML
equivalent → precompute the resulting `#AARRGGBB` and store as a keyed brush
(e.g. `Brush.TagOkBg`, `Brush.TagOkBorder`). The blueprint grid background
(`.win::before`, 24px) → a tiled `DrawingBrush` keyed `Brush.BlueprintGrid`.

Base body type: JetBrains Mono 13px, `--ink` on `#000`, antialiased. Headings:
Instrument Serif. These become the default `TextElement.FontFamily/FontSize` on the
window and the `Serif` style.

### Fonts (bundled, PRD §20 / chat intent)

Download the two families (TTF) into `Foundry.App/Themes/Fonts/`, mark
`<Resource>`, reference as `pack://application:,,,/Themes/Fonts/#Instrument Serif`
and `#JetBrains Mono`. Verify glyphs render (the serif italic `<em>` accent words and
mono tabular-nums in tables). No network font fetch at runtime.

---

## Determinism boundary & data flow (PRD §6/§7/§11)

`connections` + `components[].specs.pins` are authoritative. Wiring diagram, firmware
pin map, validation findings, and enclosure cutouts are **derived** views over them.
The AI returns structured JSON per stage (tool input schema); Core validates against a
schema, then mutates the Project; generators recompute. Phase 0/1 ship the model +
derived renderers + a **stub** pipeline that loads the demo Project so the whole UI is
exercised without an API key. The real Anthropic client drops in behind `IAnthropicClient`.

---

## Risks & open questions (PRD §16 — resolve before/at the relevant phase)

1. **3D preview (Phase 4).** HelixToolkit.Wpf renders a real STL mesh, but Phases 0–1
   ship the *iso SVG-style vector* (port of `enclosure-svg.jsx`) as the placeholder so
   the Enclosure tab looks right before the sidecar exists. Swap to a `HelixViewport3D`
   once the sidecar returns a mesh. Risk: HelixToolkit.Wpf version compatibility with
   net8.0 — pin a known-good version at add time.
2. **Python sidecar packaging (Phase 4/6).** build123d wheels are large and
   OpenCASCADE-heavy; PyInstaller single-folder bundle + spawn over localhost is the
   plan. Health-check on startup; degrade the Enclosure tab to "sidecar offline" if it
   fails. Dev mode runs the system Python; packaged mode runs the frozen exe.
3. **`/v1/models` shape (Phase 0 Settings / Phase 1).** Populate the model dropdown
   live; if the call fails or no key, use the curated fallback (Opus 4.6 / Sonnet 4.6 /
   Haiku 4.5 per §8.9). Keep the curated list as the single source for offline.
4. **Custom window chrome.** Win11 look needs `WindowStyle=None` + `WindowChrome`
   (caption buttons drawn by us, per `shared.jsx`). Risk: snap-layouts/resize affordance
   — use `WindowChrome.ResizeBorderThickness` and handle min/max/close + drag in
   `MainWindow`. Acceptable for v1.
5. **Sourcing keys/cost (Phase 5).** All optional; cached; graceful "offline / no key"
   states. Aggregator (Nexar) first to minimize integrations.
6. **PRD has no §20.** The kickoff references a "§20 UI spec" that isn't in `PRD.md`;
   the `design-reference/foundry/` prototype is the actual visual source of truth and is
   what we match pixel-for-pixel. (Noted, not blocking.)
7. **Liability framing (all phases).** Every generated build screen carries the
   "design aid · verify before you build" disclaimer (status bar + Guide banner).

---

## Phase 0 — Shell

**Outcome:** Solution builds and runs; a Win11-chromed window shows the design tokens,
fonts, status bar, and an empty screen router (onboarding ↔ projects ↔ workspace) with
placeholder content. Project model + JSON save/load + Credential Manager wrapper exist
and are unit-tested. No AI, no real tabs yet.

**Files & key types**
- Create: `Foundry.sln`, the three `.csproj` (App/Core/Tests), `.gitignore`.
- Create `Foundry.Core/Project/Project.cs` — records mirroring §6:
  `Project { Id, Title, Spec, Architecture, Components[], Connections[], Enclosure,
  Firmware, Assembly[], Validation, Chat[] }` plus the demo-shaped fields the prototype
  uses (`Kpis`, `Subsystems`, `Bom`, `Findings`) so Phase 1 binds directly. Use
  `System.Text.Json` with source-gen context.
- Create `Foundry.Core/Project/ProjectStore.cs` — `Save(Project,path)` / `Load(path)`
  round-trip; never serializes secrets.
- Create `Foundry.Core/Security/CredentialStore.cs` — P/Invoke `CredWrite`/`CredRead`/
  `CredDelete` (DPAPI-backed), target names `Foundry:Anthropic`, `Foundry:Nexar`, etc.;
  read returns masked summary helper (`sk-ant-…AB12`).
- Create `Foundry.App/Themes/Tokens.xaml` + `Controls.xaml`; bundle fonts.
- Create `Foundry.App/Controls/WindowChromeView` + `MainWindow` custom chrome.
- Create `Foundry.App/Views` placeholders + `MainViewModel` screen router.

**Tasks**
- [ ] Scaffold three projects, add to solution, add packages (CommunityToolkit.Mvvm).
- [ ] `git init` + `.gitignore` (bin/obj, *.user). Commit scaffold.
- [ ] Write `Project` records + `ProjectStore`; xUnit round-trip test (save→load equal).
- [ ] Write `CredentialStore` P/Invoke; test write→read→delete (skip on non-Windows CI).
- [ ] Author `Tokens.xaml` from the mapping table; bundle the two fonts; verify render.
- [ ] Author `Controls.xaml`: `Btn`/`BtnPrimary`/`BtnGhost`, `Tag`+severity, `Kicker`,
      `Serif`, `Card`, `Section`, `BomTable` styles — straight from `styles.css`.
- [ ] Build `MainWindow` custom chrome + `WindowChromeView` (brand mark, crumb, caption
      buttons, drag, min/max/close) + status bar (4 chips + disclaimer + version).
- [ ] `MainViewModel` with `CurrentScreen` enum + nav commands; placeholder views.
- [ ] `dotnet build` clean; `dotnet run` shows the chromed empty shell. Commit.

**Verify:** `dotnet build Foundry.sln` 0 errors; app launches, window draggable,
min/max/close work, tokens/fonts visible, screen switch works; `dotnet test` green.

---

## Phase 1 — Core clone (parity)

**Outcome:** The full prototype, recreated in WPF and bound to the canonical Project
(demo data). Onboarding, Projects library, and Workspace (rail/main/chat) with all 7
tabs render pixel-faithfully. Chat shows the staged pipeline. A **stub** pipeline lets
"new project / iterate" load and mutate the demo Project with no API key; the real
`AnthropicClient` is wired behind `IAnthropicClient` and used when a key is present.
Curated component KB ships. This alone matches Blueprint.am.

**Files & key types**
- `Foundry.Core/Project/DemoData.cs` — port `data.jsx` (PROJECT, RECENT_PROJECTS,
  CHAT_HISTORY) into typed objects.
- `Foundry.Core/Kb/ComponentKb.cs` + `kb.json` — curated parts (ESP32, cap sensor,
  TP4056, MCP1700, 18650, tact switch, gland) with `specs`/`pins`/`footprint`.
- `Foundry.Core/Ai/IAnthropicClient.cs`, `AnthropicClient.cs`, `StubAnthropicClient.cs`,
  `IPipeline.cs`, `Pipeline.cs`, `ModelCatalog.cs`, per-stage prompt/schema files.
- `Foundry.Core/Wiring/NetlistLayout.cs` — produces positioned blocks + orthogonal net
  paths (data) consumed by the renderer.
- `Foundry.App/Rendering/WiringDiagram.cs`, `EnclosureIso.cs`, `MiniDiagram.cs`.
- `Foundry.App/Views/OnboardingView`, `ProjectsView`, `ShellView`, `Tabs/*`, and their
  view models.

**Tasks (build-order, each ends in a clean build + commit)**
- [ ] Port `DemoData` + KB; bind a `ProjectsViewModel` to `RECENT_PROJECTS`.
- [ ] `OnboardingView`: split hero (display type, pipeline strip) + API-keys form with
      Anthropic/Sourcing tabs, Test-connection + Continue/Skip. Keys → CredentialStore.
- [ ] `ProjectsView`: library hero, search/sort toolbar, featured "Continue" card with
      `MiniDiagram`, and the auto-fill project grid. Open → workspace.
- [ ] `ShellView`: 3-column grid (rail / main / chat). Rail = project header + Design
      nav (7 items, numbers, icons, validation badge) + Stages list + footer. Chat =
      history (serif user msgs) + `PipelinePill` + composer.
- [ ] `OverviewView`: KPI strip (4), Architecture subsystems grid, 2-up Validation +
      Sourcing summary.
- [ ] `BomView`: live-pricing table (qty/name/mpn/unit/ext/stock/dist/lead) + serif
      subtotal + substitutions strip.
- [ ] `WiringView`: `WiringDiagram` render (blueprint grid, component blocks with pin
      headers, colored orthogonal nets, title block) + legend + connection ledger table.
- [ ] `EnclosureView`: `EnclosureIso` preview (iso box, dimension lines, cutout
      call-outs, standoffs, HUD) + dimensions/cutouts/print-estimate controls + view
      buttons. (Placeholder for HelixToolkit until Phase 4.)
- [ ] `FirmwareView`: file list + libraries sidebar + syntax-highlighted code view +
      "pinmap.h is a derived artifact" callout.
- [ ] `ValidationView`: summary strip (status/fail/warn/pass), severity-coded findings
      with refs + suggested-fix CTA, power-budget stacked-bar chart.
- [ ] `GuideView`: design-aid disclaimer banner + numbered serif assembly steps + chips.
- [ ] `Pipeline` + `StubAnthropicClient`: chat send → staged progress in the
      `PipelinePill`, mutate demo Project, re-render affected tabs. Real `AnthropicClient`
      behind interface, selected when a valid key exists.
- [ ] xUnit: schema-parse defensiveness (malformed AI JSON → readable error, no crash);
      `NetlistLayout` produces a net per connection.
- [ ] Full `dotnet build` + run; walk all 7 tabs + 3 screens; commit per tab.

**Verify:** App runs with no API key (stub); onboarding → projects → workspace flow
works; all 7 tabs match the prototype screenshots; chat pipeline animates; `dotnet test`
green. Compare against `design-reference/foundry/screenshots/*`.

---

## Phase 2 — Validation (PRD §8.8)

Rules engine over the assembled Project producing `findings[]` (`info|warn|fail`):
power budget + battery-life estimate, voltage/logic-level mismatch, pin conflicts
(input-only/strapping pins, e.g. ESP32 GPIO34–39, GPIO0), connector/qty sanity, I²C
address collisions. Each finding links to the offending connection/component and, where
possible, an auto-fix that re-runs the relevant stage. xUnit must catch an injected
fault (5V sensor on a 3.3V-only pin). Wire findings into the existing ValidationView and
the rail badge.

## Phase 3 — Firmware (PRD §8.6)

`PinMap` generates `pinmap.h` directly from `connections` (no hand-typed pins) +
`main.ino`/`main.py` per platform (Arduino C++ default, MicroPython) + `libraries[]`
with versions + `platformio.ini`. Export a ready-to-open project folder. xUnit: pin map
matches netlist; regenerates on wiring change. FirmwareView shows real generated files.

## Phase 4 — Enclosure CAD (PRD §8.5)

Python sidecar (`sidecar/server.py` FastAPI + `enclosure.py` build123d) builds the
closed enclosure schema → STL/3MF/STEP deterministically (Claude fills the schema, never
writes CAD). `SidecarHost` spawns it; `SidecarClient` posts the schema, gets a mesh;
swap EnclosureView's iso placeholder for `HelixViewport3D`. Cutouts/standoffs derived
from `components[].footprint`. Health-check + "sidecar offline" degradation.

## Phase 5 — Live sourcing + cart (PRD §8.7)

Nexar/Octopart aggregator client (primary) + optional DigiKey/Mouser; per-BOM-line
price/stock/lead/datasheet, project total, per-distributor cart/BOM-upload links. Cache;
graceful "pricing unavailable / no key" badges. Keys via CredentialStore. Wire into the
BOM tab + Overview sourcing summary.

## Phase 6 — Polish + packaging

Exports (BOM CSV, wiring SVG/PNG, guide MD/PDF, enclosure STL/3MF/STEP, firmware folder);
full Settings view (model dropdown live from `/v1/models` + curated fallback, per-stage
override, generation/export/sourcing groups); MSIX (or Inno/WiX) installer with the
PyInstaller-frozen sidecar bundled; sample projects; disclaimers; first-run onboarding.

---

## Self-review notes

- **Spec coverage:** F1–F12 map to Phases 1 (F1,F2,F8), 2 (F6), 3 (F4), 4 (F3), 5 (F5),
  6 (F7,F11,F12); F9/F10 are cross-cutting (stub/offline states + disclaimers) and
  enforced from Phase 0. Acceptance criteria (§19) are covered by Phases 1–6 + the
  injected-fault validation test in Phase 2.
- **Determinism:** wiring/pinmap/validation/cutouts are all derived from
  `connections`+`pins`; no AI verdicts. Encoded in the Phase 1 renderer + Phase 2/3 gens.
- **Stub-first:** every AI touchpoint sits behind `IAnthropicClient`/`IPipeline` so the
  UI runs key-less from Phase 1.
