# Foundry

AI Hardware Design Studio — a native **Windows 11** desktop app (C# / .NET 8 / WPF) that turns
one plain-language prompt into a buildable hardware project: architecture, BOM, wiring diagram,
assembly guide, plus a printable CAD enclosure, starter firmware, live sourcing, and rule-based
electrical validation. Everything derives from one canonical **Project** document so the outputs
stay mutually consistent as you iterate by chat.

> **Design aid — verify before you build.** Foundry's outputs are a starting point, not a
> manufacturable spec. Verify polarity, voltage, and power before applying power.

See [`PRD.md`](PRD.md) for the full spec and [`PLAN.md`](PLAN.md) for the phased build plan.

## Status

All phased milestones (PRD §15) are implemented:

- **Phase 0–1 — shell + core clone:** onboarding, project library, and the workspace
  (rail / main / chat) with all seven tabs, bound to the canonical Project. Real Anthropic
  Messages API (`/v1/models` + Messages with prompt caching) behind `IAnthropicClient`, with
  offline stubs so the app runs with no key.
- **Phase 2 — validation:** deterministic rules engine (power budget, voltage/logic levels,
  pin conflicts, strapping/input-only pins, power-ground sanity, I²C collisions); demo findings
  are engine-generated.
- **Phase 3 — firmware:** `pinmap.h` derived from the netlist + Arduino C++/MicroPython project,
  exportable to a folder.
- **Phase 4 — enclosure CAD:** Python sidecar (FastAPI on 127.0.0.1) turns the schema into an STL;
  HelixToolkit 3D preview with graceful offline fallback.
- **Phase 5 — sourcing:** Nexar/Octopart provider (+ offline fallback), per-MPN caching, cart
  links + DigiKey BOM CSV, live/offline BOM tab.
- **Phase 6 — settings + exports + packaging:** full Settings view (keys, model dropdown,
  generation/export/sourcing), BOM/guide/firmware/STL exports, and `build/` packaging scaffolding.

30 unit tests pass. Open polish items: staged structured generation (PRD §7), finding auto-fix,
image/PDF exports, and building a signed installer.

## Build & run

Requires the .NET SDK (8/9/10) and the .NET 8 Windows Desktop runtime.

```powershell
dotnet build Foundry.sln
dotnet test  Foundry.Tests
dotnet run   --project Foundry.App
```

## API keys

The app needs your own **Anthropic API key** at runtime (entered in onboarding / Settings).
Keys are stored in **Windows Credential Manager** (DPAPI-backed) — never in Project files or
logs. Optional distributor keys (Nexar/Octopart, DigiKey, Mouser) enable live sourcing; the app
degrades gracefully to offline states when keys are missing.

## Solution layout

```
Foundry.App/    WPF UI (net8.0-windows) — views, view models, themes, vector renderers
Foundry.Core/   UI-agnostic logic — Project model, AI pipeline, KB, wiring, validation, security
Foundry.Tests/  xUnit tests
sidecar/        Python CAD service (build123d) — Phase 4
```
