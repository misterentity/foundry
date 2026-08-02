---
title: Vault Conventions — Local
domain: meta
status: active
last-reviewed: 2026-08-01
max-age: 0
max-doc-lines: 150
---

# Vault Conventions — Local

This file is **project-owned**: wts-ai-docs upgrades never touch it. It carries
the rules that are specific to THIS project. **On any conflict with
[[vault-conventions]], this file wins.**

> If this file approaches 150 lines you have forked the base — upstream the
> generic parts to wts-ai-docs, or restructure.

## Verification source

There is no database and no OpenAPI spec. Ground truth here is, in order:

1. **The C# / Python source itself.** Cite `file:line`. Foundry's source carries
   unusually dense intent-bearing XML doc comments — a class summary often states
   the invariant *and* the reason. Prefer citing the enforcing line over the
   comment that describes it.
2. **The CI lanes**, for anything about live external-tool behaviour:
   - `.github/workflows/ci.yml` → `pcb-live` — real KiCad + FreeRouting, asserts
     pad→net readback equals intent. Authority for [[pcb]] claims.
   - `.github/workflows/ci.yml` → `live-sim` — real arduino-cli + avr8js in V8.
     Authority for simulation claims.
   - `.github/workflows/ci.yml` → `test` — `dotnet test Foundry.sln` + the
     no-KiCad pytest over `test_build_board.py`.
3. **A run of the app**, for anything visual — see below.

Record the method in `verified-against:` as `source-read:`, `ci-lane:`, or
`live-run:` plus the commit the read was taken at.

## Overrides

### Replaces: "Verification (schema / API / live behavior)"

Any claim about **UI appearance or layout** must be verified by rendering, not by
a passing test. Use the self-screenshot hook and look at the PNG:

```powershell
$env:FOUNDRY_START='workspace'; $env:FOUNDRY_TAB='<tab>'
$env:FOUNDRY_SHOT="$env:TEMP\shot.png"
dotnet run --project Foundry.App
```

`dotnet test` compiles XAML and exercises view models; it cannot see a collapsed
row or a dead binding. A layout regression has shipped green in this repo.

## Additions

- **Document the working tree when it differs from `main`, and say so.** This
  repo routinely carries substantial uncommitted work. When a doc describes
  uncommitted code, put a `⚠ working tree` callout in the doc body, note it in
  `verified-against:`, and open a [[_backlog]] entry naming the commit it
  diverges from.
- **Never restate a safety refusal as an intention.** Write "the build fails when
  a logical pin has no pad match (`build_board.py:137`)", not "Foundry tries to
  map pins correctly". The refusals are the product.
- **Branch base:** `main`. Diffs for `scripts/match-docs.mjs` are taken against it.
- **Diagrams:** mermaid only where a flow genuinely crosses process boundaries
  (C# → KiCad python → Java router → kicad-cli). Foundry has many such flows;
  it does not need one per tab.
