# Foundry — Claude Code CLI kickoff

This folder is the handoff package for **Foundry**, an AI hardware design desktop
app (Windows 11, C#/.NET 8, WPF). Build it with Claude Code from here.

## What's in this folder
- `PRD.md` — **the authoritative spec.** Read this in full first. Sections of note:
  §5 stack, §6 Project data model, §7 AI pipeline, §8 feature specs (incl. §8.9
  Settings), §11 technical constraints, §15 phased build plan, §18 repo structure,
  **§20 the UI/visual design spec.**
- `design-reference/` — the Claude Design handoff (the pixel-perfect UI source):
  - `design-reference/README.md` — the design tool's own handoff notes.
  - `design-reference/chats/chat1.md` — the design conversation (intent + decisions).
  - `design-reference/foundry/` — the HTML/CSS/JSX prototype + `screenshots/`.
    `styles.css` holds every design token; `Foundry.html` is the entry; `*.jsx`
    are the screens/tabs. **Recreate the visual output in WPF — do not port React.**
  - `design-reference/uploads/…original-input.md` — the earlier PRD draft the
    designer worked from. Historical only; `PRD.md` supersedes it.

## How to run the build (recommended two phases)

Open a terminal in this folder and start Claude Code:
```
cd "C:\Users\davem\OneDrive\Documents\CursorProjects\fidg.ai"
claude
```

### Phase 1 — Plan (use plan mode)
Press **Shift+Tab** to enter plan mode, then paste the **Planning prompt** below.
Review the plan it produces and save it (e.g. to `PLAN.md`) before building.

### Phase 2 — Implement
Once you're happy with the plan, paste the **Implementation prompt** below to start
Phase 0/1 of the build. Work phase by phase; commit after each.

---

## Planning prompt (paste in plan mode)

> You are building **Foundry**, a native Windows 11 desktop app (C# / .NET 8, WPF).
> Read `PRD.md` in full — it is the authoritative spec. Then read
> `design-reference/README.md`, `design-reference/chats/chat1.md`, and the prototype
> in `design-reference/foundry/` (read `styles.css`, `Foundry.html`, and every `.jsx`
> top to bottom; the screenshots are in `design-reference/foundry/screenshots/`).
> The prototype is the pixel-perfect UI source of truth — recreate its visual output
> in WPF/XAML, do not port its React structure.
>
> Produce an implementation plan that follows the phased build plan in PRD §15
> (Phase 0 shell → 1 core clone → 2 validation → 3 firmware → 4 enclosure CAD → 5
> sourcing → 6 packaging). For each phase: the .NET projects/files to create (per the
> repo structure in §18), the key types (start from the Project model in §6), the
> third-party packages (CommunityToolkit.Mvvm, HelixToolkit.Wpf, the Anthropic client,
> sourcing clients), and how the Python CAD sidecar is spawned and bundled. Map the
> §20 design tokens to a WPF ResourceDictionary and confirm the two fonts (Instrument
> Serif, JetBrains Mono) are bundled. Call out open questions and risks (PRD §16)
> before writing any code. Do not write code yet — output the plan only.

## Implementation prompt (paste after approving the plan)

> Implement **Phase 0 then Phase 1** from the approved plan, following `PRD.md`
> (esp. §6 data model, §8 features, §20 UI spec) and matching the
> `design-reference/foundry/` prototype pixel-for-pixel. Scaffold the .NET 8 solution
> per PRD §18 (`Foundry.App` WPF + `Foundry.Core` + `Foundry.Tests` + `sidecar/`), set
> up the design-token ResourceDictionary and bundled fonts, build the Windows 11 frame
> (titlebar + status bar per §20.4), the three screens (onboarding, project library,
> workspace shell with rail/main/chat) and the seven tabs as views bound to the
> Project model. Stub the Claude calls behind an interface so the UI runs without a key
> first; wire the real Anthropic Messages API (§7) once the shell is solid. Keep the
> API key in Windows Credential Manager (§8.9, §14). After each phase: build, run, and
> show me what works before moving on.

---

### Notes
- Default Claude model for the app's own AI calls: `claude-sonnet-4-6` (PRD §8.9
  gives the full selectable list + live `/v1/models` behavior).
- The app needs the user's own Anthropic API key at runtime — that's a Settings/
  onboarding field, not something to hardcode.
- This is a design aid: keep the "verify before building" disclaimers (PRD §10/§13).
