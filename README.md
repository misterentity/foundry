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

- **Phase 0 (shell)** and **Phase 1 (core clone, parity)** are complete: the full UI —
  onboarding, project library, and the workspace (rail / main / chat) with all seven tabs
  (Overview, BOM, Wiring, Enclosure, Firmware, Validation, Guide) — bound to the canonical
  Project. Wiring and enclosure are drawn as vector diagrams; firmware shows a syntax-highlighted
  code view.
- AI calls sit behind `IAnthropicClient` / `IPipeline` with **offline stubs**, so the app runs
  with no API key. Phases 2–6 (validation engine, firmware gen, build123d CAD sidecar + 3D
  preview, live sourcing, packaging) are planned in `PLAN.md`.

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
