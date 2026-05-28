# Track A — Live Renode Simulation + One-Click Flash · Implementation Design Spec

Date: 2026-05-28
Status: design (ready for build agents)
Grounded in: `docs/superpowers/specs/2026-05-28-renode-feasibility.md`

## 0. Premise & scope

Track A makes the breadboard a **live renderer of emulated pin state** (design philosophy:
wiring/breadboard are renderers of generated data; live sim extends this), and makes Flash a
**one-click arduino-cli upload** independent of sim.

Feasibility verdict (binding):

- **STM32 F1/F4/F7/L4** — first-class in Renode, GPIO modeled. **Demo path, lead with this.**
- **RP2040 / Pico** — community `matgla/Renode_RP2040` model, blink-class only, version-pinned to Renode 1.16.1, must bundle.
- **ESP32 (Xtensa)** — CPU translates but **no maintained GPIO board model** → degrade to "flash to run".
- **AVR (Uno/Nano/Mega)** — **no AVR core in Renode at all** → second engine `avr8js` behind the SAME socket contract.

The unifying contract is a one-line text protocol, `pin=level\n`, so `BreadboardControl` is
engine-agnostic and both Renode and the avr8js fallback feed identical data.

Two independent flows (never coupled):

```
generate → compile to ELF (FirmwareBuilder) → RenodeSimulator loads+runs → pin updates stream → BreadboardControl animates
generate → compile               → FirmwareBuilder.UploadAsync → physical board
```

---

## 1. Firmware tooling additions — `Foundry.Core/Firmware/FirmwareBuilder.cs`

`FirmwareBuilder` is already a static class that locates/downloads arduino-cli, infers an FQBN, and
compiles to a temp folder it deletes. Track A needs the compiled image to **survive** the build (so
the emulator/flasher can consume it) and adds upload + board-list parsing.

### 1a. Emit a compiled image (ELF/HEX)

New record + method (same file, same namespace `Foundry.Core.Firmware`):

```csharp
public sealed record CompiledImage(
    bool Ok,
    string Fqbn,
    string? ElfPath,      // <build>/<sketch>.ino.elf — what Renode LoadELF consumes
    string? HexPath,      // <build>/<sketch>.ino.hex — avr8js / LoadHEX fallback
    string? BinPath,      // <build>/<sketch>.ino.bin — LoadBinary @addr fallback
    string BuildDir,      // persisted output dir (caller owns cleanup)
    IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    public bool HasElf => Ok && ElfPath is not null && System.IO.File.Exists(ElfPath);
}

/// Compiles like CompileAsync but passes --output-dir so artifacts persist; returns their paths.
/// Caller (RenodeSimulator) owns the BuildDir lifetime and deletes it on session stop.
public static async Task<CompiledImage> CompileToImageAsync(
    Project.Project project, string outputDir, CancellationToken ct = default);
```

Implementation notes for the build agent:
- Reuse the existing sketch-staging block from `CompileAsync` (verbatim: pick main file via
  `Generation.ProjectGenerator.PickMainFile`, write `.ino`/`.h`, skip `.py`).
- Add `--output-dir "{outputDir}"` to the arduino-cli `compile` args. arduino-cli writes
  `<sketch>.ino.elf`, `<sketch>.ino.hex`, `<sketch>.ino.bin` there. Sketch folder is `foundrybuild`,
  so artifacts are `foundrybuild.ino.elf` etc. — resolve by glob `*.ino.elf` / `*.ino.hex` /
  `*.ino.bin` to stay name-agnostic.
- Reuse `EnsureCoreAsync`, `Parse`, `Fqbn`, `Locate`. Do **not** delete `outputDir` in a `finally`
  (that's the difference from `CompileAsync`); only delete the temp **sketch** root.
- MicroPython short-circuits to `new CompiledImage(false, …, Diagnostics: [])` with a skip note —
  there is no ELF and Renode cannot run it.

### 1b. One-click flash — `UploadAsync` + board/port detection

```csharp
/// One detected board: the COM/serial port plus the FQBN arduino-cli matched (may be null if unknown).
public sealed record DetectedBoard(string Port, string? Fqbn, string Label);

public sealed record UploadResult(bool Installed, bool Ok, string Summary, string Detail)
{
    public static UploadResult NotInstalled() =>
        new(false, false, "arduino-cli isn't installed — install it to flash.", "");
    public static UploadResult NoBoard() =>
        new(true, false, "No board detected. Plug in your board over USB and try again.", "");
}

/// Parse `arduino-cli board list --format json` into connected boards (PURE — unit-testable).
public static IReadOnlyList<DetectedBoard> ParseBoardList(string json);

/// Enumerate connected boards via `arduino-cli board list --format json`.
public static async Task<IReadOnlyList<DetectedBoard>> ListBoardsAsync(CancellationToken ct = default);

/// Compile (CompileToImageAsync) then `arduino-cli upload -p <port> --fqbn <fqbn> <sketch>`.
/// Port/FQBN: prefer an arduino-cli-detected match; fall back to the single connected port +
/// Fqbn(project). Surfaces a clear NoBoard()/NotInstalled() when preconditions fail.
public static async Task<UploadResult> UploadAsync(
    Project.Project project, DetectedBoard? target = null, CancellationToken ct = default);
```

`ParseBoardList` parses arduino-cli's `detected_ports[].port.address` (+ `.protocol_label`) and the
first `matching_boards[].fqbn`/`.name` when present. Keep it a pure function over the JSON string so
it is testable without hardware (see §5).

---

## 2. Simulation engine boundary — `Foundry.Core/Simulation/`

New namespace `Foundry.Core.Simulation`. Small single-purpose files. No UI references.

### 2a. Pin-state model — `PinState.cs`

```csharp
namespace Foundry.Core.Simulation;

/// One observed output line from the emulator. Engine-agnostic: identified by the netlist net
/// (e.g. "signal: MCU.GPIO13 ↔ LED1.A") AND the raw GPIO number so BreadboardControl can match
/// either way.
public sealed record PinLevel(int Gpio, bool High, string? Net = null, string? Endpoint = null);

/// Immutable snapshot of all watched lines at a moment. BreadboardControl binds the whole map.
public sealed class PinStateSnapshot
{
    public IReadOnlyDictionary<int, bool> ByGpio { get; }       // 13 -> true
    public IReadOnlyDictionary<string, bool> ByEndpoint { get; } // "LED1.A" -> true (case-insensitive)
    public PinStateSnapshot With(PinLevel level);               // returns a new snapshot (copy-on-write)
    public static PinStateSnapshot Empty { get; }
}
```

### 2b. Pin mapping — `GpioPinMap.cs`

Bridges netlist GPIO numbers ↔ emulator LED indices ↔ breadboard endpoints. Pure, fully
unit-testable, reuses the authoritative `Foundry.Core.Firmware.PinMap`.

```csharp
namespace Foundry.Core.Simulation;

/// One emulated GPIO line of interest: which MCU GPIO, which .repl LED peripheral name, and the
/// netlist endpoint/net it animates on the breadboard.
public sealed record SimPin(int Gpio, string LedName, string Endpoint, string Net);

public static class GpioPinMap
{
    /// Watched output lines = MCU-side OUTPUT signal nets from the derived PinMap (Dir=="output").
    /// LedName is conventionally "led{Gpio}" (matches the .repl generator). Deterministic order.
    public static IReadOnlyList<SimPin> Build(
        IReadOnlyList<Project.Connection> connections, Kb.ComponentKb kb);

    /// Map a raw "led13"/"13"/gpio int coming off the socket back to its SimPin.
    public static SimPin? Resolve(IReadOnlyList<SimPin> pins, int gpio);
}
```

### 2c. Engine interface + session — `ISimulator.cs`, `SimSession.cs`

```csharp
namespace Foundry.Core.Simulation;

public enum SimEngine { Renode, Avr8js }

public sealed record SimCapability(bool Supported, SimEngine? Engine, string Reason);

/// The engine boundary. RenodeSimulator and (future) Avr8jsSimulator implement this.
public interface ISimulator
{
    SimEngine Engine { get; }
    /// Decide up-front whether this project can be simulated by this engine (chip family check).
    SimCapability CanSimulate(Project.Project project);
    /// Compile (if needed), boot the engine, begin streaming. Returns a live session or throws.
    Task<SimSession> StartAsync(Project.Project project, CancellationToken ct = default);
}

/// A running simulation. Disposing stops the engine. Pin updates surface via the event AND a
/// cached snapshot (so a late UI subscriber sees current state immediately).
public sealed class SimSession : IDisposable
{
    public SimEngine Engine { get; }
    public PinStateSnapshot Current { get; private set; }
    public bool IsRunning { get; private set; }
    public string StatusMessage { get; private set; }

    /// Raised on every pin edge, already coalesced into a fresh snapshot. May fire on a worker
    /// thread — UI subscribers must marshal to the dispatcher.
    public event Action<PinStateSnapshot>? Updated;

    public void SetSpeed(double factor);   // 0.25–4×; Renode: `machine SetGlobalQuantum`/perf knob
    public void Stop();                    // graceful: `quit` over the socket
    public void Dispose();                 // Stop() + kill process if still alive

    // Engines push edges through this (internal):
    internal void Push(PinLevel level);
}
```

`SimSession` is the seam that lets tests use a **fake `ISimulator`** that returns a session and
calls `Push(...)` on a timer — no Renode required (see §5).

### 2d. Renode install — `RenodeInstaller.cs` (mirror `OpenScadInstaller`)

```csharp
namespace Foundry.Core.Simulation;

/// Locate a PATH or app-local Renode, or download the pinned portable zip on demand to
/// %LocalAppData%/Foundry/tools/renode/. Mirrors Cad.OpenScadInstaller exactly.
public static class RenodeInstaller
{
    // PIN a known-good version (feasibility risk: version drift breaks .repl/.resc + matgla RP2040).
    private const string PinnedVersion = "1.16.1";
    private const string PortableUrl =
        "https://github.com/renode/renode/releases/download/v1.16.1/renode-1.16.1.windows-portable.zip";

    public static string ToolsDir { get; }       // …/Foundry/tools/renode
    public static string? Locate();              // Renode.exe in PATH or under ToolsDir (recurse 1 level)
    public static bool IsInstalled { get; }
    public static async Task<string> DownloadAsync(CancellationToken ct = default); // ~hundreds of MB
}
```

Same shape as `OpenScadInstaller`: `Directory.CreateDirectory`, `HttpClient.GetByteArrayAsync`,
`ZipFile.ExtractToDirectory(overwriteFiles:true)`, delete zip, `Locate() ?? throw`,
`AppLog.Info("sim", …)`. The bundled `matgla` RP2040 `.repl` ships alongside (committed under the
app's `renode-models/` and copied next to the generated `.resc`).

### 2e. Renode process lifecycle — `RenodeHost.cs` + `RenodeClient.cs` (mirror `SidecarHost`/`SidecarClient`)

`RenodeHost` is a per-process singleton (`RenodeHost.Shared`) that spawns ONE long-lived headless
Renode and reuses it, exactly like `SidecarHost`:

```csharp
namespace Foundry.Core.Simulation;

public sealed class RenodeHost : IDisposable
{
    private const int MonitorPort = 3456;   // Renode Monitor TCP
    public static RenodeHost Shared { get; }
    public string StatusMessage { get; private set; }
    public bool IsRunning { get; }

    /// Idempotent: reuse a running Monitor, else spawn
    ///   Renode.exe --disable-gui --console -P 3456 -e "logLevel 3"
    /// then connect a RenodeClient and health-check. Returns null on failure (graceful degrade).
    public async Task<RenodeClient?> StartAsync(CancellationToken ct = default);
    public void Dispose();   // kill entire process tree (mirror SidecarHost.Dispose)
}

/// TcpClient to 127.0.0.1:3456 speaking the Monitor line protocol: send "<cmd>\n",
/// read until the prompt returns. Careful framing (feasibility risk: brittle prompt detection,
/// interleaved log lines) — model on SidecarClient's request/response discipline.
public sealed class RenodeClient
{
    public RenodeClient(int monitorPort);
    public async Task<bool> HealthAsync(CancellationToken ct = default);     // e.g. `version`
    public async Task<string> CommandAsync(string cmd, CancellationToken ct = default);
    public async Task LoadScriptAsync(string rescPath, CancellationToken ct = default); // `include @…`
    public async Task QuitAsync(CancellationToken ct = default);            // `quit`
}
```

`RenodeHost.Dispose()` must be wired into app shutdown next to `SidecarHost` disposal (see §3/§6).

### 2f. The Renode engine — `RenodeSimulator.cs` (implements `ISimulator` per the feasibility recipe)

```csharp
namespace Foundry.Core.Simulation;

public sealed class RenodeSimulator : ISimulator
{
    public SimEngine Engine => SimEngine.Renode;

    public SimCapability CanSimulate(Project.Project project);
    public async Task<SimSession> StartAsync(Project.Project project, CancellationToken ct = default);
}
```

`CanSimulate` keys off `FirmwareBuilder.Fqbn(project)` family:
- `stm32*` → Supported (Renode).
- `rp2040/pico` → Supported (Renode, bundled matgla model).
- `esp32/esp8266` → not Supported, reason "ESP32 GPIO not modeled in Renode — flash to run".
- `avr` → not Supported by **this** engine; reason routes the caller to the avr8js fallback.

`StartAsync` pipeline (the recipe):
1. `CompiledImage img = await FirmwareBuilder.CompileToImageAsync(project, sessionDir, ct);`
   require `img.HasElf`.
2. `var pins = GpioPinMap.Build(project.Connections, kb);` (watched OUTPUT lines).
3. Generate **`foundry.repl`** via `RenodeReplGenerator` (§2g) into `sessionDir`.
4. Generate **`foundry.resc`** via `RenodeRescGenerator` (§2g) into `sessionDir`.
5. `var client = await RenodeHost.Shared.StartAsync(ct);` then `client.LoadScriptAsync("foundry.resc")`.
6. Open the **host-owned `TcpListener` on 127.0.0.1:7777** (Mechanism B) BEFORE the `.resc` runs its
   Python connect; spawn a reader task that parses `pin=level\n` and calls `session.Push(...)`.
7. If the bundled Renode lacks Python/socket (feasibility risk), fall back to **Mechanism A**: emit
   `watch "sysbus.gpio.led{N} State" 100` per pin and parse the Monitor stream for `True/False`.
8. `client.CommandAsync("start")`; return the live `SimSession`.

The protocol parser maps `led{N}`/`{N}` → `SimPin` via `GpioPinMap.Resolve`, builds a `PinLevel`
(with `Net`/`Endpoint` filled), and pushes it.

### 2g. Generators — `RenodeReplGenerator.cs`, `RenodeRescGenerator.cs`

Pure string generators (unit-testable), mirroring `PinMap.RenderHeader`'s deterministic style.

```csharp
namespace Foundry.Core.Simulation;

public static class RenodeReplGenerator
{
    /// Emit foundry.repl: pick the chip GPIO controller for the FQBN family and wire a
    /// Miscellaneous.LED onto each watched line. STM32 pattern per port:
    ///   gpioPortC: ... ; 13 -> ledC13@0 ; ledC13: Miscellaneous.LED @ gpioPortC 13
    /// (feasibility risk: wrong port/base silently shows no animation — table-drive per family.)
    public static string Build(string fqbn, IReadOnlyList<SimPin> pins);
}

public static class RenodeRescGenerator
{
    /// Emit foundry.resc:
    ///   using sysbus; mach create "foundry"
    ///   machine LoadPlatformDescription @foundry.repl
    ///   sysbus LoadELF @<elf>
    ///   + Mechanism B python hook (import socket; connect 127.0.0.1:7777; led.StateChanged += …)
    ///     OR Mechanism A `watch "sysbus.gpio.ledN State" 100` lines
    ///   start
    public static string Build(string elfPath, IReadOnlyList<SimPin> pins, int hostPort, bool usepython);
}
```

---

## 3. UI

### 3a. `BreadboardControl` live overlay — `Foundry.App/Rendering/BreadboardControl.cs`

Add a **second DependencyProperty** beside `Project` — do not touch the static `Build()`/`OnRender`
geometry, just overlay glow when live state is present.

```csharp
public static readonly DependencyProperty LivePinStateProperty =
    DependencyProperty.Register(nameof(LivePinState), typeof(Foundry.Core.Simulation.PinStateSnapshot),
        typeof(BreadboardControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender)); // render-only

public Foundry.Core.Simulation.PinStateSnapshot? LivePinState
{ get => (…)GetValue(LivePinStateProperty); set => SetValue(LivePinStateProperty, value); }
```

Render rules (additive, inside `OnRender`/`DrawChip`):
- When `LivePinState is null` → behaves exactly as today (static render preserved).
- Each chip pin already knows its endpoint (`$"{alias}.{pin}"`) and net. Look it up in
  `LivePinState.ByEndpoint` (fallback `ByGpio` via the pin-name GPIO suffix).
- HIGH → draw an additive glow: a soft amber/white radial under the pin dot + brighten the leg.
  Drive the dot color toward `CSignal`/white at HIGH, dim at LOW. A pin tied to an LED-class
  component endpoint gets a larger "lit LED" glow halo.
- `AffectsRender` only (not `AffectsMeasure`) so setting live state never re-lays-out the board.

The control stays engine-agnostic: it only reads `PinStateSnapshot`, never touches Renode.

### 3b. `SimulationViewModel` + Run control on the Wiring tab

The Wiring tab already has a `SCHEMATIC | BREADBOARD` toggle (`WiringViewModel.Breadboard`). Add a
third **SIMULATE** affordance: a `RUN ▶ / STOP ■` button visible in BREADBOARD mode.

New `Foundry.App/ViewModels/SimulationViewModel.cs` (mirror `EnclosureViewModel`'s async + status
+ install idiom):

```csharp
public sealed partial class SimulationViewModel : TabViewModelBase
{
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _severity = "info";   // info | pass | fail
    [ObservableProperty] private double _speed = 1.0;          // 0.25–4×
    [ObservableProperty] private Foundry.Core.Simulation.PinStateSnapshot? _livePinState; // → DP binding
    public bool RenodeInstalled => Foundry.Core.Simulation.RenodeInstaller.IsInstalled;
    public bool CanSimulate { get; }   // from ISimulator.CanSimulate(project)
    public string UnsupportedReason { get; }

    [RelayCommand] private async Task Run();          // pick engine, StartAsync, subscribe Updated
    [RelayCommand] private void Stop();               // session.Dispose()
    [RelayCommand] private async Task InstallRenode(); // RenodeInstaller.DownloadAsync (~hundreds MB)
    partial void OnSpeedChanged(double v);            // session?.SetSpeed(v)
}
```

`Run()` subscribes to `SimSession.Updated` and assigns `LivePinState` on the **dispatcher**
(`Application.Current.Dispatcher.BeginInvoke`), mirroring `ShellViewModel`'s AppLog marshaling. The
WiringView binds `BreadboardControl.LivePinState` to this VM. Engine selection: try
`RenodeSimulator.CanSimulate`; if AVR, route to the avr8js fallback engine when present, else show
the "flash to run" state. Never block — unsupported chips show `UnsupportedReason` and the static
breadboard.

WiringView.xaml changes (additive, next to the existing toggle StackPanel at lines 31–34):
- A `RUN`/`STOP` `BtnPrimary` bound to `RunCommand`/`StopCommand`, an `INSTALL RENODE` `BtnGhost`
  shown when `!RenodeInstalled`, a speed slider, and a status line bound to `Status`/`Severity`.
- Bind `LivePinState="{Binding Sim.LivePinState}"` on the existing `<r:BreadboardControl>`.
- The `SimulationViewModel` is exposed off `WiringViewModel` (a `Sim` property) so one tab DataContext
  drives both — or registered as its own tab factory in `ShellViewModel.Tabs` if a dedicated
  "Simulate" tab is preferred. **Recommendation: nest under Wiring** (the breadboard already lives
  there; no new tab).

### 3c. Flash button + `FlashViewModel` on the Firmware tab

The Firmware tab already has VERIFY BUILD / FIX BUILD / INSTALL TOOLCHAIN (`FirmwareViewModel`). Add
flash as sibling commands. Either extend `FirmwareViewModel` or add `FlashViewModel`. **Recommendation:
extend `FirmwareViewModel`** (it already owns the toolchain install + build status surface):

```csharp
// additions to FirmwareViewModel
[ObservableProperty] private bool _isFlashing;
[ObservableProperty] private string _flashStatus = "";
[ObservableProperty] private string _flashSeverity = "info";
public ObservableCollection<Foundry.Core.Firmware.DetectedBoard> Boards { get; } = new();
[ObservableProperty] private Foundry.Core.Firmware.DetectedBoard? _selectedBoard;

[RelayCommand] private async Task DetectBoards();  // FirmwareBuilder.ListBoardsAsync → Boards
[RelayCommand] private async Task Flash();         // FirmwareBuilder.UploadAsync(Project, SelectedBoard)
```

FirmwareView.xaml: a `DETECT BOARDS` ghost button + a board dropdown + a `FLASH ▶` primary button
near the existing build controls, with a status line bound to `FlashStatus`/`FlashSeverity`. Flash is
**independent of sim** — it compiles and uploads regardless of whether Renode is installed.

---

## 4. Data flow (concrete)

Simulation:
```
ProjectGenerator → Project (Connections, Firmware.Files, derived PinMap)
SimulationViewModel.Run
  → RenodeSimulator.StartAsync(project)
      → FirmwareBuilder.CompileToImageAsync(project, sessionDir)  // → foundrybuild.ino.elf
      → GpioPinMap.Build(connections, kb)                          // watched OUTPUT lines
      → RenodeReplGenerator.Build / RenodeRescGenerator.Build      // foundry.repl + foundry.resc
      → RenodeHost.Shared.StartAsync → RenodeClient.LoadScriptAsync("foundry.resc")
      → host TcpListener :7777 reader → parse "pin=level\n" → SimSession.Push(PinLevel)
  → SimSession.Updated (dispatcher) → SimulationViewModel.LivePinState
  → BreadboardControl.LivePinState (AffectsRender) → OnRender glows lit pins
```

Flash:
```
FirmwareViewModel.Flash
  → FirmwareBuilder.UploadAsync(project, selectedBoard)
      → CompileToImageAsync(project, tmp)        // reuse the same compile path
      → arduino-cli upload -p <port> --fqbn <fqbn> <sketch>
  → UploadResult → FlashStatus / FlashSeverity
```

Both flows reuse the **same compile path**; the ELF the emulator loads is the same image lineage the
flasher uploads (HEX/ELF), satisfying "reuse the SAME ELF/HEX the arduino-cli build produced".

---

## 5. Test plan — `Foundry.Tests` (xUnit, mirror `FirmwareTests`)

All deterministic pieces are pure and need no Renode/hardware. New file
`Foundry.Tests/SimulationTests.cs`; flash-parsing additions in `FirmwareTests`/`BuildTests`.

Pin mapping (`GpioPinMap`) — uses `DemoData.SoilMoistureConnections()` + `ComponentKb.Demo()`:
- `Build` returns only MCU-side OUTPUT lines (`Dir=="output"`), with `LedName=="led{Gpio}"`, in
  deterministic GPIO order.
- `Resolve(pins, 13)` round-trips a `led13`/`13` socket token back to its `SimPin` (net + endpoint).
- Regenerates when wiring changes (rewire GPIO → assert new SimPin), mirroring the existing
  `PinMap_Regenerates_WhenWiringChanges` test.

Generators (pure string asserts, like `Header_IsGeneratedAndMarkedDerived`):
- `RenodeReplGenerator.Build("stm32:...:f4", pins)` contains `Miscellaneous.LED`, the right
  `gpioPort*`, and a `-> led{N}@0` line per pin.
- `RenodeRescGenerator.Build(elf, pins, 7777, usepython:true)` contains `mach create`,
  `LoadPlatformDescription @foundry.repl`, `LoadELF @`, and the `StateChanged` python hook; with
  `usepython:false` it contains `watch "sysbus.gpio.led` … ` 100`.

Board/port parsing (pure):
- `FirmwareBuilder.ParseBoardList(sampleJson)` extracts port address + fqbn for a single board, an
  empty list for `{}`/no `detected_ports`, and handles a port with no `matching_boards` (Fqbn null).
- `ParseBoardList` with two ports returns two `DetectedBoard`s in order.

Sim-session lifecycle with a **fake `ISimulator`** (no Renode):
- A `FakeSimulator : ISimulator` returns a `SimSession` and pushes a scripted sequence of
  `PinLevel`s via `session.Push`. Assert: `Updated` fires per edge, `Current` reflects the latest
  snapshot, `ByGpio`/`ByEndpoint` are consistent, and `Dispose()`/`Stop()` flips `IsRunning=false`
  and stops further events.
- `CanSimulate` capability matrix: STM32 → Supported(Renode); ESP32/AVR → not Supported with the
  expected reason string (drive `Project.Firmware.Board`/components through `Fqbn`).

`CompiledImage` (integration-guarded): a test that calls `CompileToImageAsync` is **skipped when
`FirmwareBuilder.Locate() is null`** (mirror how `BuildTests` already guards on arduino-cli
presence), asserting `*.ino.elf` exists and `BuildDir` is not deleted.

---

## 6. Honest callouts — what needs hardware / needs Renode installed

- **Needs Renode installed** (~hundreds of MB portable zip + bundled mono): live STM32/RP2040 sim.
  Gated behind `RenodeInstaller.IsInstalled` and an `INSTALL RENODE` button; absent → static
  breadboard, never a crash. Pinned to 1.16.1 (version drift breaks `.repl/.resc` and the matgla
  RP2040 model).
- **Needs arduino-cli installed**: both compile-to-ELF (for sim) and flash. Already handled by the
  existing `DownloadCliAsync` / INSTALL TOOLCHAIN flow.
- **Needs a physical board over USB**: Flash (`UploadAsync`) and board detection (`ListBoardsAsync`).
  No board → `UploadResult.NoBoard()`, a clear message, no hang. Untestable without hardware — the
  parsing is unit-tested from captured JSON instead.
- **ESP32 / ESP8266**: NO live Renode sim (no maintained GPIO model). UI shows
  `UnsupportedReason` and a "flash to run" state; Flash still works.
- **AVR (Uno/Nano/Mega)**: NO Renode core. Live sim requires the **avr8js** second engine
  (`Avr8jsSimulator : ISimulator`, a tiny node sidecar emitting the SAME `pin=level\n` protocol) —
  scoped as a follow-on; until it lands, AVR projects flash but don't animate.
- **Mechanism B (python socket push)** depends on the bundled Renode shipping IronPython+socket. If
  absent at runtime, `RenodeSimulator` auto-falls-back to **Mechanism A** (`watch` polling at
  100 ms / 10 Hz). Both feed the identical `pin=level` contract.
- **Process lifetime**: `RenodeHost.Shared.Dispose()` must be invoked on app exit alongside
  `SidecarHost` disposal, or a headless Renode.exe leaks. Wire it where `SidecarHost` is disposed.
- **Monitor text protocol is brittle** (prompt detection, interleaved logs) — `RenodeClient` must
  frame requests/responses as carefully as `SidecarClient`.

---

## 7. File manifest (new / changed)

New — `Foundry.Core/Simulation/`:
`PinState.cs`, `GpioPinMap.cs`, `ISimulator.cs`, `SimSession.cs`, `RenodeSimulator.cs`,
`RenodeInstaller.cs`, `RenodeHost.cs`, `RenodeClient.cs`, `RenodeReplGenerator.cs`,
`RenodeRescGenerator.cs`. (Future: `Avr8jsSimulator.cs`.)

Changed — `Foundry.Core/Firmware/FirmwareBuilder.cs`: `CompiledImage`, `CompileToImageAsync`,
`DetectedBoard`, `UploadResult`, `ParseBoardList`, `ListBoardsAsync`, `UploadAsync`.

New/changed — UI: `Foundry.App/ViewModels/SimulationViewModel.cs` (new);
`Foundry.App/Rendering/BreadboardControl.cs` (`LivePinState` DP + glow overlay);
`Foundry.App/Views/Tabs/WiringView.xaml` (RUN/STOP/INSTALL/speed + `LivePinState` binding);
`Foundry.App/ViewModels/TabViewModels.cs` (`FirmwareViewModel` flash members; `WiringViewModel.Sim`);
`Foundry.App/Views/Tabs/FirmwareView.xaml` (DETECT/FLASH controls).

New — tests: `Foundry.Tests/SimulationTests.cs`; flash-parse cases in `FirmwareTests`/`BuildTests`.
