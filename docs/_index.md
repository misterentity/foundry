---
title: Documentation Vault — Home
domain: meta
status: active
last-reviewed: 2026-08-01
# docs/superpowers/specs/ holds per-feature design history (brainstorm + design
# docs from the superpowers skill) — deliberately out of vault scope, not linted.
vault-doctor-ignore-dirs: [superpowers, screenshots]
---

# Documentation Vault

**For AI agents and humans.** This is the root of the docs vault. Start here.

> If you are an agent working in this repo, read this note first, then the specific domain doc for your task. Do not grep the whole vault — domain docs are designed to be read directly.

---

## Scope

This vault documents **Foundry** — a .NET 8 / WPF Windows desktop app that turns one plain-language prompt into a complete hardware project: architecture, BOM, netlist, AI-written firmware, a 3D-printable enclosure, a deterministic validation report, and an auto-routed PCB with a fab-order handoff.

Solution layout: `Foundry.App` (WPF UI) · `Foundry.Core` (all logic) · `Foundry.Tests` + `Foundry.App.Tests` · `sidecar/` (Python CAD service) · `sidecar-avr/` (avr8js runtime bundle).

Per-feature design history — `PRD.md`, `PRD-v2.md`, `PLAN.md`, `p0-plan.md`, `GO_LIVE.md` — lives at the repo root and is **out of scope here**.

---

## Domain docs

| Task context | Doc |
|---|---|
| Auto-PCB: footprints, pin→pad resolution, placement, routing, DRC loop, Gerber/fab handoff | [[pcb]] |
| Prompt → `Project` via Claude: JSON contract, retries, truncation, revise/Q&A modes, model ids | [[generation]] |
| Pin map derivation, arduino-cli compile, board detection, one-click flash guards | [[firmware]] |
| Downloading/verifying external toolchains, publisher pinning, self-update trust, API-key storage | [[provisioning]] |
| WPF shell, MVVM navigation, theme tokens, code-drawn diagrams, dev env hooks, visual verification | [[desktop-ui]] |
| Enclosure CSG, component heights from KiCad STEP models, provable mechanical fit | [[enclosure]] |

Five further seams are mapped in `_meta/doc-ownership.yml` but **not yet documented** — `simulation`, `project-model`, `validation`, `sourcing`, `build-release`. See [[_backlog]] before assuming anything about them.

### Two invariants that cut across docs

1. **The AI proposes; deterministic engines dispose.** Validation findings, KPIs, the firmware pin map, and every PCB coordinate are computed locally, never taken from the model — [[generation#the-determinism-boundary]], [[pcb#the-invariant-this-domain-exists-to-protect]].
2. **Unverifiable connectivity fails the build.** A net pin that can't be proven to sit on the pad it names refuses the board rather than guessing — [[pcb#the-fail-closed-gate]].
3. **An unfinished check never reads as a pass.** `unproven` is a first-class severity and outranks `pass` in the rollup — [[enclosure#unproven-is-a-real-severity]].

---

## Cheatsheets

`docs/_cheatsheets/` holds ≤50-line quick-references for high-frequency lookups whose answers are buried in 300+ line domain docs. **The lookup loads the cheatsheet (small) instead of the full doc (large).** That's the context budget you save by maintaining this layer. See `_meta/vault-conventions.md` → Cheatsheets for the trigger rule.

| Lookup | Cheatsheet | Parent doc |
|---|---|---|
| _none yet — add one when a lookup proves repetitive_ | | |

## Machinery

- [[vault-conventions]] — the playbook (naming, frontmatter, verification rules)
- [[vault-conventions-local]] — this project's rules (yours; upgrades never touch it; wins on conflict)
- [docs/_meta/doc-ownership.yml](_meta/doc-ownership.yml) — code-path → doc map consumed by `scripts/match-docs.mjs`
- [[docs-sync-prompt]] — the workflow followed when you say "update docs"
- [[docs-sync-prompt-local]] — project-specific sync addenda (yours; wins on conflict)
- [[_backlog]] — gaps, TODO-verify flags, undocumented behavior

---

## Conventions (see [[vault-conventions]] for full detail)

- Filenames: `kebab-case.md`
- Wikilinks: `[[filename]]` or `[[filename#section]]`
- Every doc: frontmatter with `title`, `domain`, `status`, `last-reviewed`, and (for verification-citing docs) `verified-against`
- Code citations include `file:line` anchors
- Schema/API/live-behavior content verified against ground truth at write time
- Archive, don't delete
