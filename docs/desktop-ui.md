---
title: Desktop UI — WPF shell, MVVM, and visual verification
domain: desktop-ui
status: active
last-reviewed: 2026-08-01
verified-against:
  - source-read: Foundry.App/** at 813593b + uncommitted working tree (App.xaml.cs, BreadboardControl.cs, MainViewModel.cs, app.manifest, csproj)
  - env-hooks: FOUNDRY_START / FOUNDRY_TAB / FOUNDRY_SHOT / FOUNDRY_GEN / FOUNDRY_EXPORT_DIR as read in App.xaml.cs + MainWindow.xaml.cs
# Fonts/icons carry no behaviour. Per-tab view models are described by their own
# domain doc (pcb/firmware/generation), not here — this doc owns the shell,
# navigation, rendering, and theme, so full coverage of Foundry.App/** is not
# the goal. Residual gap tracked in _backlog.md.
coverage-extensions: [.cs, .xaml]
---

# Desktop UI — WPF shell, MVVM, and visual verification

> **What's in this doc:** the app/window lifecycle (tray-resident, custom chrome), how a view model becomes a view, the two-level navigation (`MainViewModel` shell → `ShellViewModel` workspace tabs), the code-drawn rendering controls, the design-token theme, the dev env-var hooks including the self-screenshot, and what "verified" means for a UI change.
>
> **What's NOT:** anything a tab *does* to the project — those live with their domain (→ [[pcb]], [[firmware]], [[generation]]); the tray updater's trust decision (→ [[provisioning#updater-trust-policy]]).

## The rule that matters most here

**A UI change is not verified by `dotnet test`.** The unit suite compiles XAML and exercises view models; it cannot see a control rendered off-screen, a collapsed row, or a binding that silently resolves to nothing. A layout regression has shipped green in this repo before. Use the self-screenshot hook (`FOUNDRY_SHOT`, below) and look at the PNG before claiming a visual change works.

## Project shape

`Foundry.App/Foundry.App.csproj` — `net8.0-windows`, `UseWPF`, `AssemblyName` **Foundry**, `PerMonitorV2` DPI, packages `CommunityToolkit.Mvvm` 8.4.2 and `HelixToolkit.Wpf` 3.1.2 (for the enclosure 3D view).

WinForms is referenced **only** for the tray `NotifyIcon`, and its implicit global usings are removed so WPF's `Brush`/`Color`/`Application`/`UserControl` stay unambiguous — the csproj comment explains this, and `App.xaml.cs:11` uses the `Forms.` alias instead. Don't re-add those usings to "fix" a type error; alias the WinForms type.

## Lifecycle

`Foundry.App/App.xaml.cs:21` — `OnStartup`:

1. Global crash handlers for both the dispatcher and the app domain; nothing crashes silently (`:25-27`, handler at `:248`). Crashes append to `%LocalAppData%\Foundry\crash.log`; the dialog is suppressible with `FOUNDRY_NODIALOG=1` (`:257`).
2. `ShutdownMode.OnExplicitShutdown` — **the app lives in the tray**; closing the window hides it (`:31-32`, enforced in `MainWindow.OnClosing` at `Foundry.App/MainWindow.xaml.cs:59-68`). Tray Quit calls `ForceClose` (`Foundry.App/MainWindow.xaml.cs:53`).
3. `MainViewModel` is constructed with a `CredentialStore` and the window is shown (`:34-47`).
4. Tray menu: Open / Check for updates / Quit (`:88-106`).

`Quit` and `OnExit` both dispose the CAD sidecar and Renode hosts (`:239-246`, `:263-269`) — a new long-lived external process needs a line in **both**.

The window is custom-chrome: the title bar is a draggable region with double-click maximize and hand-rolled min/max/close handlers (`Foundry.App/MainWindow.xaml.cs:70-81`).

## Navigation and view resolution

There is no navigation service; there are two levels of `CurrentView`, resolved by implicit `DataTemplate`s.

**Level 1 — `MainViewModel`** (`Foundry.App/ViewModels/MainViewModel.cs:19`) owns the top-level screen via `CurrentView` (`:25`) plus breadcrumbs (`:33`) and status-bar state (model label, key label, AI-busy indicator — `:36-42`). Screens: `ShowOnboarding` (`:114`), `ShowProjects` (`:120`), `ShowNewProject` (`:127`), `OpenSample` (`:143`), `OpenSaved` (`:152`), `OpenGenerated` (`:163`), `ShowWorkspace` (`:177`), `ShowSettings` (`:196`), `ShowLogs` (`:204`). Leaving the workspace persists edits first (`:121`).

**Level 2 — `ShellViewModel`** (`Foundry.App/ViewModels/ShellViewModel.cs:33`) is the workspace: rail + tabbar + body + chat. The seven tabs are declared once, as a table of `TabDescriptor` with a factory per tab (`:81-90`):

| Id | Label | View model |
|---|---|---|
| `overview` | Overview | `OverviewViewModel` |
| `bom` | BOM | `BomViewModel` |
| `wiring` | Wiring | `WiringViewModel` (+ `WiringViewModel.Pcb.cs` partial) |
| `enclosure` | Enclosure | `EnclosureViewModel` |
| `firmware` | Firmware | `FirmwareViewModel` |
| `validation` | Validation | `ValidationViewModel` |
| `guide` | Assembly guide | `GuideViewModel` |

**Resolution** happens through implicit templates in `Foundry.App/App.xaml:30-56` — `<DataTemplate DataType="{x:Type vm:XViewModel}">` → the matching `View`. A `ContentControl` bound to `CurrentView` (`Foundry.App/MainWindow.xaml:102`) does the rest. Adding a screen = view model + view + one `DataTemplate` line; forgetting the template renders the type name as text.

Every tab view model derives from `TabViewModelBase` (`Foundry.App/ViewModels/Tabs/TabViewModelBase.cs`), which holds the canonical `Project` — **all tabs read and write the same document**.

### The memory-leak trap

`ShellViewModel` subscribes to the **static** `AppLog.Logged` event to surface WARN/ERROR as an inline banner (`:104-117`). The handler is kept in a field (`:120`) and unsubscribed in `Dispose` (`:124`) precisely because an inline lambda on a static event would root every dead `ShellViewModel` — one per project open — forever. Keep that shape. The handler also marshals back to the dispatcher, since `AppLog` raises from any thread (`:111`).

## Rendering and theme

Diagrams are **code-drawn**, not images:

- `Foundry.App/Rendering/WiringDiagramControl.cs:15` — a `FrameworkElement` that lays components into three columns (power · controller · peripherals) and routes each connection as a coloured orthogonal net, derived entirely from `Project.Connections`/`Components` (`:9-13`). Nothing hard-coded.
- `Foundry.App/Rendering/BreadboardControl.cs` — the breadboard view (uncommitted changes in the working tree).
- `Foundry.App/Rendering/WiringImage.cs` — off-screen PNG rendering, used by the PDF exporter and the diag hook.
- `Foundry.App/Controls/IconControl.cs:12` — a `Shape` subclass drawing a 1.4 px stroke icon on a 16×16 grid from path data, used as `<c:Icon Glyph="cart" .../>`.

Theme tokens live in `Foundry.App/Themes/Tokens.xaml`, a direct port of `design-reference/foundry/styles.css` `:root` (`:3-4`). Each token is exposed as **both** `Color.*` and `Brush.*` — colours for gradients/animation, brushes for fills/strokes. The palette is dark-first: `Color.Bg` `#07070A` (`:12`), surfaces `:13-16`, hairlines `:17-19`, ink ramp `:22-25`, and semantic accents at `:28-33` (`Accent` `#FF5A1F`, `Ok` `#4ADE80`, `Warn` `#FBBF24`, `Fail` `#EF4444`, `Info` `#5DD2FF`). Use a token; don't inline a hex.

Value converters are centralised in `Foundry.App/Converters/Converters.cs` — severity→brush (`:31`, `:47`, `:64`, `:81`), net→brush (`:16`), stock→brush (`:168`), and the usual visibility/equality helpers (`:97-166`).

## Dev hooks (env vars)

| Variable | Read at | Effect |
|---|---|---|
| `FOUNDRY_START` | `App.xaml.cs:35-44` | jump straight to `projects` / `newproject` / `workspace` / `settings` / `logs` / `gen` / `export` |
| `FOUNDRY_TAB` | `ShellViewModel.cs:101-102` | open the workspace on a given tab id |
| `FOUNDRY_SHOT` | `MainWindow.xaml.cs:21-31` | render the window to a PNG once settled, then exit |
| `FOUNDRY_SHOT_DELAY_MS` | `MainWindow.xaml.cs:24` | settle delay, default **1200 ms** |
| `FOUNDRY_GEN` | `App.xaml.cs:76` | generate a real project from a prompt, then open it (needs a stored key) |
| `FOUNDRY_EXPORT_DIR` | `App.xaml.cs:61` | where `FOUNDRY_START=export` writes `_diag_wiring.png`, `_diag_breadboard.png`, `_diag_spec.pdf` |
| `FOUNDRY_NODIALOG` | `App.xaml.cs:257` | suppress the crash dialog |

### Verifying a visual change

`FOUNDRY_SHOT` renders the live visual tree with `RenderTargetBitmap` (`MainWindow.xaml.cs:34-50`) — a reliable self-capture with no screen race — and `OnClosing` deliberately skips the hide-to-tray behaviour while it is set (`:62`) so the process actually exits.

```powershell
$env:FOUNDRY_START='workspace'; $env:FOUNDRY_TAB='wiring'
$env:FOUNDRY_SHOT="$env:TEMP\shot.png"
dotnet run --project Foundry.App
```

Then open the PNG and look at it. `FOUNDRY_START=export` is the equivalent for the PNG/PDF export path.

## Tests

`Foundry.App.Tests/BomViewModelTests.cs` is currently the only view-model test project; it runs as part of `dotnet test Foundry.sln` in CI (`.github/workflows/ci.yml:36`). View-model logic that can be tested headlessly belongs there — but see the rule at the top: it does not substitute for looking at the rendered window.
