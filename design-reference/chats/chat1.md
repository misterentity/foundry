# UI Design Brief

_Started 2026-05-20 21:59 UTC_

---

## User

read PRD and design the UI pls. very modern, dark theme, sharp lines ui, good use of colour

## Assistant

_[tool: read_file]_

_[tool: list_files]_

_[tool: list_files]_

_[tool: list_files]_

_[tool: invoke_skill]_

_[tool: questions_v2]_

## User

Questions answered:
- scope: Full app: onboarding, projects list, workspace, every tab
- presentation: Single full-bleed prototype (one window, switchable tabs)
- accent_color: 5, 4
- vibe: Brutalist hardware — raw, sharp, oversized type, blueprint-like
- type: Editorial serif display + mono body (unexpected, hardware-as-craft)
- imagery: Yes — draw realistic-looking SVG diagrams, mock 3D, etc.
- windows_chrome: Yes — wrap in a Windows 11-style window with chrome
- tweaks: Decide for me

## Assistant

_[tool: update_todos]_

_[tool: copy_starter_component]_

_[tool: write_file]_

_[tool: write_file]_

_[tool: write_file]_

Now the main HTML and the app shell:

_[tool: write_file]_

_[tool: write_file]_

_[tool: write_file]_

_[tool: write_file]_

_[tool: write_file]_

_[tool: write_file]_

_[tool: write_file]_

Now the app root that ties it together:

_[tool: write_file]_

_[tool: grep]_

_[tool: read_file]_

_[tool: run_script]_

Let me check it loads:

_[tool: show_html]_

_[tool: done]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: eval_js_user_view]_

_[tool: screenshot_user_view]_

_[tool: multi_screenshot]_

I see a few issues to fix — the pipeline pills are mangled, the hero title overlaps the subtitle. Let me check the other tabs quickly then fix in a batch.

_[tool: str_replace_edit]_

Multi-screenshot isn't triggering tab changes because dispatched message events don't reach React state. Let me first fix the visible layout issues and add a global nav helper for testing.Now fix the visible layout issues:

_[tool: str_replace_edit]_

Now update the PipelinePill component to match the new CSS:

_[tool: str_replace_edit]_

_[tool: screenshot_user_view]_

_[tool: show_to_user]_

_[tool: screenshot_user_view]_

_[tool: grep]_

_[tool: eval_js_user_view]_

_[tool: eval_js_user_view]_

_[tool: screenshot_user_view]_

_[tool: show_html]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: show_to_user]_

_[tool: sleep]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: view_image]_

_[tool: view_image]_

_[tool: view_image]_

_[tool: view_image]_

_[tool: screenshot_user_view]_

_[tool: eval_js_user_view]_

_[tool: screenshot_user_view]_

_[tool: str_replace_edit]_

Looking at this — the user view screenshot might be stale (caching). Let me cache-bust the CSS:

_[tool: snip]_

_[tool: show_to_user]_

_[tool: sleep]_

_[tool: eval_js_user_view]_

_[tool: eval_js_user_view]_

_[tool: str_replace_edit]_

Layout is confirmed correct — earlier screenshot was a stale cache. Let me do final polish for narrower viewports and ship:

_[tool: done]_

_[tool: update_todos]_

_[tool: fork_verifier_agent]_

Foundry's full UI is up — a Windows 11-framed desktop app with three screens (onboarding, project library, workspace) and seven workspace tabs (Overview, BOM, Wiring, Enclosure, Firmware, Validation, Guide).

Visual direction: brutalist hardware studio — sharp 1px lines, editorial **Instrument Serif** for headings, **JetBrains Mono** for everything else. Signal orange dominates with green/yellow/cyan reserved for ok/warn/info and the wiring nets (power=red, ground=gray, signal=cyan).

A few highlights:
- **Wiring tab** has an actual netlist diagram — labeled component blocks with pin headers, orthogonal routing, a title block, drawn at scale (no images).
- **Enclosure tab** has an isometric "viewport" with dimension lines, callouts on cutouts, build123d HUD.
- **Firmware tab** is a real-looking code editor with a generated pin map highlighted.
- **Validation tab** has a power-budget stacked bar, severity-coded findings with auto-fix CTAs.
- **Chat panel** shows the staged pipeline (`Spec → Architecture → Wiring → Firmware → Enclosure → Validation`) per turn.

Tweaks panel lets you switch screen, tab, accent color (5 swatches), display font (3 options) and density.

Open the **Tweaks** toggle in the toolbar to jump between Onboarding / Library / Studio without re-running the flow. Tell me what to adjust — denser, lighter, more color, different vibe per tab, etc.

## User

Verifier agent check completed

## Assistant

_[tool: snip]_

_[tool: str_replace_edit]_

Also bump the cache-buster and tighten a couple more headlines:

_[tool: str_replace_edit]_

_[tool: grep]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

_[tool: done]_

_[tool: fork_verifier_agent]_

Fixed the italic descender overlap across all serif headlines — bumped line-heights on `page__h1`, `rail__project-name`, `proj-card__name`, the projects-list hero, the featured project card title, finding/subsystem/guide/encl titles, and added a small `padding-bottom` on the largest serif headings to keep italic descenders inside their box.

## User

Verifier agent check completed

## Assistant

_[tool: snip]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

_[tool: show_html]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

The issue is the italic descender on `<em>` words in headings extends below the line box. Let me drop italic from the worst offenders (multi-line wrapping contexts) while keeping it where it's safe (single-line hero displays):

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: eval_js]_

_[tool: show_html]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

The inline `marginTop: 12` in PageHead is overriding my CSS. Fix the source and add more padding:

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: eval_js]_

_[tool: eval_js]_

_[tool: show_html]_

_[tool: sleep]_

_[tool: eval_js]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: eval_js]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

_[tool: str_replace_edit]_

The narrow column forces wrapping that causes the overlap. Let me restructure PageHead to stack at narrow widths and tighten the right-column sizing:

_[tool: show_html]_

_[tool: sleep]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: eval_js]_

_[tool: save_screenshot]_

_[tool: view_image]_

_[tool: view_image]_

_[tool: view_image]_

_[tool: done]_

Looks clean. Shipping:

_[tool: fork_verifier_agent]_

Fixed the descender overlap properly by restructuring the page header — at narrow viewports the action buttons now stack below the title instead of being forced into a side column, so the headline doesn't have to wrap into a thin lane. Also dropped italic from `.page__h1 em` (kept the orange accent), bumped padding-bottom on serif headings, and removed `text-wrap: balance`.

