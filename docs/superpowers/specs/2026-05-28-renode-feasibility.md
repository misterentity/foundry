# Renode Feasibility + Integration Recipe — Live In-App Simulation (Track A)

Date: 2026-05-28
Scope: Can Foundry run the REAL compiled firmware in Renode and animate the breadboard
view (`BreadboardControl`) from live GPIO pin levels at ~10–30 Hz, with a separate
one-click Flash path? Concrete recipe + per-chip verdict + fallbacks.

---

## TL;DR verdict

**Renode is the right engine for STM32 and (with the community model) RP2040; ARM
Cortex-M is its sweet spot.** It can stream per-GPIO level to a .NET host cleanly, and
because Renode is itself .NET (run under the WindowsDesktop runtime or its bundled mono),
we have two integration routes: (a) drive a long-lived headless `Renode.exe` over its
**Monitor TCP socket** (mirrors `SidecarHost`), or (b) host Renode's assemblies in-process
and subscribe to C# events directly. Route (a) is the pragmatic first cut.

**The GPIO-streaming mechanism is real and not hand-waved:** attach a `Miscellaneous.LED`
(implements `ILed`, exposes `bool State { get; }` and `event Action<ILed,bool> StateChanged`)
to each GPIO line in the `.repl`, then EITHER poll `sysbus.gpio.led0 State` via the Monitor
with the built-in **`watch "<cmd>" <ms>`** command, OR — better — subscribe to `StateChanged`
from a Python hook in the `.resc` and push the event out a socket the host already owns.
Polling at 50–100 ms (10–20 Hz) is trivially within reach.

**ESP32 is NOT demo-ready** (Xtensa translation exists since 1.14 but no maintained board
.repl/.resc, no GPIO demo). **AVR/ATmega328 is NOT supported by Renode at all** — use
**avr8js (MIT)** for the entire Uno/Nano/Mega path; it is purpose-built for exactly this
(cycle-accurate ATmega328p, GPIO register hooks → pin level), it's what Wokwi uses.

---

## 1. Launching Renode headless and driving it from a .NET process

Renode ships a CLI (`Renode.exe` on Windows). Relevant flags (docs:
renode.readthedocs.io/en/latest/basic/running.html):

- `--console` — Monitor in the same stdio stream (prompt intertwined with log).
- `--disable-gui` (a.k.a. `--disable-xwt` in older builds) — no X/WPF windows; analyzers log instead of opening windows. Use this.
- `-P <port>` — expose the **Monitor over a TCP socket** on `127.0.0.1:<port>` (default 1234). This is the integration channel.
- `-e "<monitor command>"` — execute a Monitor command at startup (repeatable).
- `-e "include @C:/path/to/start.resc"` or pass a `.resc` path directly — run a startup script.
- `--pid-file`, `--port` etc. for lifecycle.
- Exit: send `quit` over the Monitor socket (or kill the process). Disconnecting the socket does NOT stop the sim — matches the reuse-running-instance model in `SidecarHost`.

**Recommended launch (mirrors `SidecarHost.cs` spawn + health-check + reuse + kill-on-exit):**

```
Renode.exe --disable-gui --console -P 3456 -e "logLevel 3"
```

Then the host connects a `TcpClient` to `127.0.0.1:3456`. The Monitor speaks a simple
line protocol: send a command + `\n`, read text back until the `(machine) ` / `(monitor) `
prompt re-appears. (Renode's "telnet mode" is exactly this socket; `telnet 127.0.0.1 3456`
is the manual equivalent — docs confirm `log "hi"`, `uart_connect`, etc. work over it.)

This is structurally identical to `FirmwareBuilder.RunAsync()` (spawn external CLI,
redirect IO) extended to a *persistent* connection like `SidecarClient`.

**On-demand install:** mirror `OpenScadInstaller.cs` — download the Renode Windows
portable zip to `%LocalAppData%/Foundry/tools/renode/`, extract, cache `Renode.exe` path.
Add a `RenodeInstaller.cs` next to `OpenScadInstaller.cs` and a `RenodeHost.cs` next to
`SidecarHost.cs`. (Renode releases ship `renode-<ver>.windows-portable.zip` on GitHub
releases — same pattern as the arduino-cli zip in `FirmwareBuilder.DownloadCliAsync()`.)

---

## 2. Loading compiled firmware (ELF/HEX/BIN) + the .repl per target

A simulation = a machine + a platform description (`.repl`) + a firmware binary, usually
orchestrated by a `.resc` script. Foundry already produces the ELF/HEX via `arduino-cli`
(the same artifact we Flash), so the Run path reuses the build output.

Canonical `.resc` (ARM example, fully generic):

```
using sysbus
mach create "foundry"
machine LoadPlatformDescription @C:/.../foundry.repl
sysbus LoadELF @C:/.../firmware.elf        # or LoadHEX / LoadBinary @addr
showAnalyzer sysbus.uart0                   # optional; --disable-gui sends to log
start
```

Load verbs: `sysbus LoadELF @file`, `sysbus LoadHEX @file`,
`sysbus LoadBinary @file 0x08000000` (BIN needs a load address).

Per target:

- **STM32** — first-class. Bundled `.repl`/`.resc` for STM32F4 Discovery, F7, F103
  "Blue Pill", L4, F0, etc. GPIO controllers modeled. Load the matching ELF/HEX from
  arduino-cli's STM32 core. **Best Renode experience.**
- **RP2040 / Pico** — NOT in mainline Renode. Community model `github.com/matgla/Renode_RP2040`
  provides `boards/raspberry_pico.repl`, GPIO with interrupts, runs C/Arduino ELF; ~50% of
  pico-examples pass (blink/timers/multicore good). Pinned to Renode 1.16.1, "WIP/frozen."
  Usable for blink-class demos; bundle the .repl with Foundry.
- **ESP32 (Xtensa)** — Xtensa translation since 1.14 (Antmicro/Google SOF work), but **no
  maintained ESP32 board .repl/.resc and no GPIO demo**. Treat as unsupported for live sim now.
- **AVR / ATmega328 / Uno-Nano-Mega** — **Renode has no AVR core.** Out of scope for Renode.

The authoritative pin→port mapping Foundry already computes in `Firmware/PinMap` (pinmap.h)
is exactly what we use to decide which GPIO lines to wire LEDs onto in the generated `.repl`.

---

## 3. THE critical part — reading per-GPIO LEVEL out to the host at 10–30 Hz

Renode's GPIO output lines are signals; a bare GPIO controller doesn't hand you a pollable
"pin 13 = high." The clean, documented trick is to terminate each line of interest in a
**`Miscellaneous.LED`** peripheral, which gives a host-readable boolean and an event.

`Miscellaneous.LED` (renode-infrastructure `.../Peripherals/Miscellaneous/LED.cs`):

```csharp
public LED(bool invert = false)
public bool State { get; }                       // host-readable level
public event Action<ILed, bool> StateChanged;    // fires on every edge
public void OnGPIO(int number, bool value)       // driven by the connected GPIO line
```

### 3a. Wire LEDs to the pins in the generated `.repl`

Pattern (verbatim from a working Renode example,
blog.y2kbugger.com/baremetal-riscv-renode-1.html — `<gpio> N -> ledX@0`):

```
// foundry.repl — Foundry generates this from Project.Connections + PinMap
gpio: <GpioControllerTypeForThisChip> @ sysbus 0x...
    13 -> led13@0          // connect GPIO line 13 to LED's input pin 0
    12 -> led12@0

led13: Miscellaneous.LED @ gpio 13
led12: Miscellaneous.LED @ gpio 12
```

For STM32, the GPIO ports are `gpioPortA`..`gpioPortK`; connect e.g. `gpioPortC 13 -> ledC13@0`
and add `ledC13: Miscellaneous.LED @ gpioPortC 13`. We only need LEDs on the lines Foundry's
netlist actually uses — `Project.Connections` tells us which.

### 3b. Read it out — two interchangeable mechanisms

**Mechanism A — poll over the Monitor socket (simplest, ship this first).**
Reading a property with no argument returns its value (docs/monitor-syntax):

```
(foundry) sysbus.gpio.led13 State
True
```

Either poll it from the host on a `DispatcherTimer` at 50–100 ms, or use Renode's built-in
periodic command **`watch`** (docs/monitor-syntax — `watch "<cmd>" <ms>`):

```
watch "sysbus.gpio.led13 State" 100      // re-emits the value every 100 ms on the socket
```

The host's socket reader parses `True/False` lines and updates a `Dictionary<int,bool>`
pin-state map. For N pins, either issue N `watch`es or one combined command. At 10–20 Hz
across a handful of LEDs this is comfortably within Renode/Monitor throughput.

**Mechanism B — event-push from a `.resc` Python hook (lower latency, edge-accurate).**
Renode exposes the C# `StateChanged` event to its embedded Python; docs/using-python show
attaching lambdas to peripheral C# events verbatim, e.g.
`externals.switch.FrameProcessed += lambda switch, sender, data: ...`. So in the `.resc`:

```
python "import socket"
python "sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM); sock.connect(('127.0.0.1', 7777))"
python "led = monitor.Machine['sysbus.gpio.led13']"
python "led.StateChanged += lambda l, s: sock.send(('13=%d\n' % (1 if s else 0)).encode())"
```

Foundry opens a `TcpListener` on 7777 before launching the `.resc`; every GPIO edge arrives
as `pin=level\n` with near-zero latency and no polling. This is the preferred production path;
Mechanism A is the no-Python fallback if the bundled Renode build lacks IronPython sockets.

### 3c. Drive `BreadboardControl`

`BreadboardControl` is a `FrameworkElement` with a `Project` DP that already renders chips
and colored jumpers, and pins carry a `Net`. Add a live overlay: a `IReadOnlyDictionary<string,bool>`
(net or "alias.pin" → high/low) DP that the socket reader updates on the UI thread; the control
colors LED glyphs / pin dots from it and `InvalidateVisual()`s. Net/pin identity is the same
`alias.pin` key used in `Project.Connections`, so the host maps `led13 → the net on MCU pin 13`
via `PinMap` and lights the right component. This keeps the "renderer of generated data"
philosophy: the breadboard becomes a renderer of live pin state.

---

## 4. Demo-ready vs flaky (per chip)

| Target | Renode support | GPIO stream | Verdict |
|---|---|---|---|
| STM32 (F1/F4/F7/L4) | First-class, bundled .repl/.resc | LED+watch / StateChanged works | **Demo-ready.** Lead with this. |
| RP2040 / Pico | Community model (matgla), Renode 1.16.1, ~50% examples | GPIO + interrupts modeled, blink works | **Demo-ready for blink-class**, bundle the model, pin the version. |
| ESP32 (Xtensa) | CPU translation only, no board/GPIO demo | none maintained | **Flaky / not ready.** Don't promise it via Renode. |
| AVR Uno/Nano/Mega | **None** (no AVR core) | n/a | **Not Renode.** Use fallback. |

---

## 5. Fallback per chip

- **AVR (Uno/Nano/Mega):** **avr8js** (MIT, github.com/wokwi/avr8js). Cycle-accurate ATmega328p
  (+2560/ATtiny), emulates GPIO/timers/USART/SPI/I2C/ADC. Pin level via a write hook on the
  PORTx register (e.g. PORTB @ 0x25) — exactly the per-pin stream we need. It's JS/Node, so run
  it as a tiny sidecar (`node avr8js-runner.js firmware.hex`) that emits the same `pin=level\n`
  socket protocol as the Renode Python hook — `BreadboardControl` stays agnostic to the engine.
  This is the SAME socket contract, so the WPF side is written once. Wokwi proves the model.
- **ESP32:** until Renode's ESP32 board model matures, fall back to (a) Wokwi's online ESP32 sim
  for reference, or (b) a "simulation unavailable for ESP32 — flash to run" state, still offering
  the one-click Flash. Do not block Flash on sim availability.
- **RP2040:** if the community model misbehaves on a given sketch, degrade to "flash to run."
- **STM32:** primary path; no fallback needed for blink/GPIO demos.

The unifying design: define ONE host-side line protocol `pin=level\n` over a TCP socket.
Renode (Monitor `watch` or Python `StateChanged` push) and avr8js (register hook) both feed it.
`BreadboardControl` consumes a `Dictionary<pinKey,bool>` and animates. Engine is swappable per chip.

---

## 6. Concrete integration checklist for Foundry

1. `Foundry.Core/Sim/RenodeInstaller.cs` — clone `OpenScadInstaller` pattern; download Renode
   windows-portable zip to `%LocalAppData%/Foundry/tools/renode/`.
2. `Foundry.Core/Sim/RenodeHost.cs` + `RenodeClient.cs` — clone `SidecarHost`/`SidecarClient`;
   spawn `Renode.exe --disable-gui --console -P <port>`, health-check, reuse, kill on exit.
3. `Foundry.Core/Sim/ReplGenerator.cs` — emit `foundry.repl` from `Project.Components` +
   `Project.Connections` + `PinMap`: pick the chip's GPIO controller, wire a `Miscellaneous.LED`
   onto each used line.
4. `Foundry.Core/Sim/Resc` — emit `foundry.resc`: `mach create`, `LoadPlatformDescription`,
   `sysbus LoadELF <arduino-cli output>`, the Python `StateChanged → socket` hooks, `start`.
5. Host opens `TcpListener` (port 7777); parses `pin=level`; raises pin-state changes.
6. `BreadboardControl` gains a `LivePinState` DP (net/pin → bool) + glyph coloring.
7. AVR path: `Foundry.Core/Sim/Avr8jsRunner` — node sidecar emitting the same protocol.
8. Flash stays a separate one-click action reusing the arduino-cli upload, independent of sim.

---

## Sources

- Running modes / headless / -P / --console: https://renode.readthedocs.io/en/latest/basic/running.html
- Monitor & script syntax (property read, watch, include, using sysbus): https://renode.readthedocs.io/en/latest/basic/monitor-syntax.html
- Using Python in Renode (event subscription, AddUserStateHook): https://renode.readthedocs.io/en/latest/basic/using-python.html
- Describing platforms (.repl, `->` GPIO connections): https://renode.readthedocs.io/en/latest/basic/describing_platforms.html
- LED.cs (State / StateChanged / OnGPIO): https://github.com/renode/renode-infrastructure/blob/master/src/Emulator/Peripherals/Peripherals/Miscellaneous/LED.cs
- Working LED-on-GPIO + watch example: https://blog.y2kbugger.com/baremetal-riscv-renode-1.html
- Supported boards: https://renode.readthedocs.io/en/latest/introduction/supported-boards.html
- ESP32/Xtensa status: https://antmicro.com/blog/2023/09/renode-1-14-release and https://github.com/renode/renode/issues/704
- RP2040 community model: https://github.com/matgla/Renode_RP2040
- avr8js (AVR fallback, MIT): https://github.com/wokwi/avr8js
- pyrenode3 (alt automation route): https://renode.io/news/python-driven-automation-and-scripting-in-renode-with-pyrenode3/
