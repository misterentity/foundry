# avr8js Engine — Design + Spike Evidence (AVR live simulation)

Date: 2026-05-28
Scope: Run the REAL compiled Arduino firmware for Uno/Nano/Mega (AVR) inside avr8js,
headless, from the .NET WPF app, and stream per-GPIO levels into the SAME
`SimSession`/`PinStateSnapshot` contract the Renode engine already feeds — so
`BreadboardControl` (its `LivePinState` DP) glows live with no UI render changes.
Decides Option A (ClearScript in-process) vs Option B (Node sidecar) WITH evidence.

---

## TL;DR verdict

**Engine: Option A — IN-PROCESS via `Microsoft.ClearScript.V8`.** Verified by actually
running it: avr8js (npm `avr8js@0.21.0`, MIT) is bundled to a single 82 KB IIFE with
esbuild, loaded into a `V8ScriptEngine` inside Foundry.Core, and the JS calls back into a
C# host object (`host.onPin(gpio, high)`) on every GPIO edge. The real compiled `Blink`
HEX toggles **D13 / PORTB bit 5** and the C# callback observed the toggles in-process —
no socket, no Node at runtime, no external process.

avr8js does **not** ship an `AVRRunner` in the npm package — that class lives only in the
Wokwi web demo. The package exports the primitives (`CPU`, `avrInstruction`, `AVRIOPort`,
`AVRTimer`, port configs). We assemble our own tiny runner: load HEX → `progMem`, construct
`AVRIOPort`s + `AVRTimer`s, `addListener` on each port, then loop `avrInstruction(cpu);
cpu.tick();` in cycle batches. That is exactly what the spike does.

Option B (Node sidecar streaming `pin=level\n` over TCP, mirroring `SidecarHost`/
`RenodeClient.HostPushPort`) remains the documented fallback if ClearScript can't load the
native V8 on a target machine — but the spike shows A works, so A is the primary engine.

---

## Spike evidence (done for real)

Scratch: `C:\temp\avr8js-spike` (npm `avr8js` + `esbuild`) and `C:\temp\cs-probe`
(throwaway .NET 8 console referencing ClearScript). Both deleted after the spike.

1. **Real firmware.** App-local `arduino-cli 1.5.0` (`%LocalAppData%\Foundry\tools\
   arduino-cli.exe`, `arduino:avr` core 1.8.8 already installed) compiled a `Blink`
   (`pinMode(13,OUTPUT); digitalWrite(13,HIGH/LOW); delay(100)`) →
   `blink.ino.hex` (922 bytes program). This is the same HEX `FirmwareBuilder.
   CompileToImageAsync` produces (`HexPath`).

2. **Headless run under Node (primitives + our runner).** `runner.mjs` parsed the Intel
   HEX into `progMem`, built `AVRIOPort(cpu, portBConfig)` + Timer0/1/2, `addListener` on
   PORTB, read `portB.pinState(5)`:

   ```
   pin=13 level=0 cycles=180
   pin=13 level=1 cycles=231
   pin=13 level=0 cycles=1600346     <- 1.60M cycles after prev edge
   pin=13 level=1 cycles=3200498     <- +1.60M cycles
   DONE toggles=6 simCycles=6400932 wallMs=130
   ```

   1.60M cycles @ 16 MHz = **100 ms** — exactly `delay(100)`. Timer0 (millis) is driving
   `delay()` correctly, so this is a faithful run of the real firmware, not a stub.

3. **IIFE bundle is runtime-free.** `esbuild entry.js --bundle --format=iife
   --global-name=Avr8Module --target=es2020 --outfile=avr8.bundle.js` → 82 KB. The only
   `require`-looking token is esbuild's internal `__require` CJS shim name (never invoked;
   avr8js's CJS modules are statically inlined). No `process`, `node:`, `Buffer`,
   `__dirname`. It ran inside a bare Node `vm` context whose sandbox had **only `console`**
   (typed arrays are V8 built-ins) and toggled correctly — a faithful stand-in for the
   ClearScript realm.

4. **ClearScript in-process — the decisive test.** `C:\temp\cs-probe` referenced
   `Microsoft.ClearScript.V8` **7.4.5** + `Microsoft.ClearScript.V8.Native.win-x64`
   **7.4.5** (native `ClearScriptV8.win-x64.dll` deployed under `runtimes/win-x64/native/`
   for `net8.0`). It did `engine.Execute(bundle)`, `engine.Script.Avr8.createRunner(hex,
   engine.Script.host, false)`, `runner.runCycles(16_000_000)`:

   ```
   C# host got pin13 = 0
   C# host got pin13 = 1
   C# host got pin13 = 0
   C# host got pin13 = 1
   CLEARSCRIPT toggles=11 cycles=16000000
   ```

   The C# `Host.onPin(int gpio, bool high)` method was invoked by JS on each edge.
   **This is the contract the engine uses.** ~11 toggles per simulated second matches a
   100 ms half-period blink (2 edges / 200 ms = 10 Hz). 1 sim-second ran in well under a
   second wall-clock, so real-time pacing has ample headroom.

---

## avr8js API (grounded in the package, not guessed)

From `node_modules/avr8js/dist/esm/index.d.ts` (v0.21.0):

- `class CPU { constructor(progMem: Uint16Array, sramBytes?); progMem; data; readHooks;
  writeHooks; cycles; pc; tick(); reset(); readData(addr); }` — `gpioPorts: Set<AVRIOPort>`.
- `function avrInstruction(cpu: CPU): void` — executes one instruction.
- `class AVRIOPort { constructor(cpu, portConfig); addListener((value:u8, oldValue:u8) =>
  void); pinState(index: 0..7): PinState; setPin(index, value); }`
- `enum PinState { Low=0, High=1, Input=2, InputPullUp=3 }`
- Port configs: `portBConfig, portCConfig, portDConfig` (328) and
  `portEConfig…portLConfig` (2560). Each is `{ PIN, DDR, PORT, ... }`.
- `class AVRTimer { constructor(cpu, config); }` + `timer0Config, timer1Config,
  timer2Config`. **Timer0 is required** or `delay()`/`millis()` never advance.
- Also available for richer peripherals: `AVRUSART` (`onByteTransmit`), `AVRADC`,
  `AVRSPI`, `AVRTWI`, `AVREEPROM`, `AVRClock`.

There is **no `AVRRunner` / `loadProgram` export** — we own the run loop and HEX parse.

---

## Bundling recipe (exact)

devDeps (build-time only; nothing ships in the app except the produced `.js`):

```
npm i -D avr8js@0.21.0 esbuild
```

`entry.js` exposes one global the host calls (no Node APIs):

```js
import { CPU, avrInstruction, AVRIOPort, PinState,
  portBConfig, portCConfig, portDConfig,
  portEConfig, portFConfig, portGConfig, portHConfig, portJConfig, portKConfig, portLConfig,
  AVRTimer, timer0Config, timer1Config, timer2Config } from 'avr8js';
// parseIntelHex(text,size) -> Uint8Array;  createRunner(hexText, host, mega) -> { runCycles(n), cycles() }
// host = C# object with onPin(gpio:int, high:bool). Listener reads port.pinState(i) and maps (port,bit)->Arduino pin.
globalThis.Avr8 = { createRunner };
```

Build command:

```
esbuild entry.js --bundle --format=iife --global-name=Avr8Module \
  --target=es2020 --outfile=avr8.bundle.js
```

Produces a single ~82 KB `avr8.bundle.js`. Ship it as an embedded resource (or under
`AppContext.BaseDirectory\sim\avr8.bundle.js`) in Foundry.Core. The bundle is committed/
built once; node+npm are **build-time tooling only**, never a runtime dependency (matches
the house pattern of treating node as tooling).

NuGet additions to `Foundry.Core.csproj`:

```
Microsoft.ClearScript.V8            7.4.5
Microsoft.ClearScript.V8.Native.win-x64  7.4.5
```

---

## Headless-drive recipe (how pin edges reach C#)

New `Foundry.Core/Simulation/Avr8jsSimulator.cs : ISimulator` (`Engine => SimEngine.Avr8js`):

**`CanSimulate(project)`** — supported iff `FirmwareBuilder.Fqbn(project)` contains
`avr`/`uno`/`nano`/`mega`/`leonardo` AND `GpioPinMap.Build(project.Connections, kb)` is
non-empty AND it's not MicroPython. (RenodeSimulator already returns `No(...)` for AVR, so
the factory routes AVR here — see "Wiring".) Return a soft-yes with a "first run downloads
arduino-cli/installs core" note if `FirmwareBuilder.Locate()` is null, mirroring Renode's
"not installed" soft-yes.

**`StartAsync(project, ct)`**:
1. `pins = GpioPinMap.Build(...)`; `new SimSession(SimEngine.Avr8js, pins, "compiling firmware…")`.
2. `image = await FirmwareBuilder.CompileToImageAsync(project, buildDir, ct)` — need
   `image.HexPath` (HEX, not ELF; add a `HasHex` check). On failure: set status, `Stop()`,
   clean `buildDir`, return (same shape as RenodeSimulator).
3. Read the bundle text once; `var engine = new V8ScriptEngine();
   engine.Execute(bundleText);`
4. Host bridge object exposed to JS:
   ```csharp
   sealed class PinBridge {
       readonly Action<int,bool> _emit;
       public PinBridge(Action<int,bool> emit) => _emit = emit;
       public void onPin(int gpio, bool high) => _emit(gpio, high);   // JS calls this
   }
   ```
   `engine.AddHostObject("host", new PinBridge(Emit));` where `Emit` resolves net/endpoint
   and pushes — IDENTICAL to RenodeSimulator's `Emit`:
   ```csharp
   void Emit(int gpio, bool high) {
       var sp = GpioPinMap.Resolve(pins, gpio);
       session.Push(new PinLevel(gpio, high, sp?.Net, sp?.Endpoint));
   }
   ```
   So `BreadboardControl` is fed the same `PinStateSnapshot` regardless of engine.
5. `var hex = File.ReadAllText(image.HexPath);`
   `engine.Script.runner = engine.Script.Avr8.createRunner(hex, engine.Script.host, isMega);`
6. **Run loop on a background thread** (a `V8ScriptEngine` is single-threaded — confine it):
   pump cycle batches sized to real time. At 16 MHz, run `16_000_000 * dt * speed` cycles
   per tick, e.g. a 16 ms loop runs ~256k cycles/iteration → smooth ~60 Hz UI updates.
   ```csharp
   var thread = new Thread(() => {
       var sw = Stopwatch.StartNew(); long lastNs = 0;
       while (!cts.IsCancellationRequested) {
           var nowNs = sw.Elapsed.Ticks * 100;
           var dt = (nowNs - lastNs) / 1e9; lastNs = nowNs;
           var n = (long)(16_000_000 * dt * speed);
           if (n > 0) engine.Script.runner.runCycles(n);   // JS fires host.onPin -> Emit -> session.Push
           Thread.Sleep(8);
       }
   }) { IsBackground = true };
   ```
   Edges flow JS `port.addListener` → `host.onPin(gpio,high)` → C# `Emit` → `session.Push`
   → `SimSession.Updated` → UI dispatcher → `BreadboardControl.LivePinState`. **De-dupe**
   in the JS listener (only call `onPin` when `pinState(i)` changed for output pins) so we
   don't flood the host on every port write — the spike already does this per pin.
7. **De-bounce on the C# side too** is optional; `PinStateSnapshot.With` is copy-on-write
   so unchanged pushes are cheap, but skipping no-ops keeps `Updated` quiet.
8. `session.Bind(onSpeed: f => speed = Math.Clamp(f,...), onStop: () => { cts.Cancel();
   thread.Join(500); engine.Dispose(); Directory.Delete(buildDir,true); });`
9. Status `"running · avr8js"`. Done.

**Why in-process beats the socket here:** ClearScript host calls are direct method
invocations — no TCP framing, no `pin=level\n` parsing, no port to bind, no second process
to supervise. The Renode `pin=level\n` contract exists because Renode is a separate
process; avr8js has no such constraint. (If we ever need B, the JS listener simply writes
`gpio=level\n` to a socket instead of calling `host.onPin`, and we reuse RenodeSimulator's
`GpioListener` verbatim.)

---

## ATmega328 / ATmega2560 pin <-> PORTx-bit map

avr8js exposes ports by their I/O register addresses (from `gpio.js`): for the 328,
PORTB=0x25, PORTC=0x28, PORTD=0x2B (DDRx and PINx are PORT-1 and PORT-2). A `SimPin.Gpio`
is the **Arduino digital pin number** (`GpioPinMap` extracts the trailing number from the
MCU endpoint, e.g. `mcu.D13` → 13, `mcu.A0` → analog). The JS listener maps `(portLetter,
bit)` → Arduino pin so `host.onPin(gpio, …)` matches the `SimPin.Gpio` keys, and
`PinStateSnapshot.ByGpio` / `ByEndpoint` resolve on the breadboard.

### Uno / Nano (ATmega328P)
| Arduino pin | Port.bit | avr8js |
|---|---|---|
| D0  | PD0 | portD bit 0 |
| D1  | PD1 | portD bit 1 |
| D2  | PD2 | portD bit 2 |
| D3  | PD3 | portD bit 3 |
| D4  | PD4 | portD bit 4 |
| D5  | PD5 | portD bit 5 |
| D6  | PD6 | portD bit 6 |
| D7  | PD7 | portD bit 7 |
| D8  | PB0 | portB bit 0 |
| D9  | PB1 | portB bit 1 |
| D10 | PB2 | portB bit 2 |
| D11 | PB3 | portB bit 3 |
| D12 | PB4 | portB bit 4 |
| **D13 (LED_BUILTIN)** | **PB5** | **portB bit 5** |
| A0 (D14) | PC0 | portC bit 0 |
| A1 (D15) | PC1 | portC bit 1 |
| A2 (D16) | PC2 | portC bit 2 |
| A3 (D17) | PC3 | portC bit 3 |
| A4 (D18, SDA) | PC4 | portC bit 4 |
| A5 (D19, SCL) | PC5 | portC bit 5 |

Forward map for the JS listener (Uno/Nano): `D` bit b → b; `B` bit b(0..5) → 8+b;
`C` bit b(0..5) → 14+b. (PB6/PB7 = crystal, PC6 = reset — not exposed; return -1.)

### Mega (ATmega2560)
The 2560 has ports A–L. The digital-pin → port.bit table (Arduino Mega variant):
| Arduino | Port.bit |  | Arduino | Port.bit |
|---|---|---|---|---|
| D0 | PE0 | | D22 | PA0 |
| D1 | PE1 | | D23 | PA1 |
| D2 | PE4 | | D24 | PA2 |
| D3 | PE5 | | D25 | PA3 |
| D4 | PG5 | | D26 | PA4 |
| D5 | PE3 | | D27 | PA5 |
| D6 | PH3 | | D28 | PA6 |
| D7 | PH4 | | D29 | PA7 |
| D8 | PH5 | | D30 | PC7 |
| D9 | PH6 | | D31 | PC6 |
| D10 | PB4 | | D32 | PC5 |
| D11 | PB5 | | D33 | PC4 |
| D12 | PB6 | | D34 | PC3 |
| **D13 (LED)** | **PB7** | | D35 | PC2 |
| D14 | PJ1 | | D36 | PC1 |
| D15 | PJ0 | | D37 | PC0 |
| D16 | PH1 | | D38 | PD7 |
| D17 | PH0 | | D39 | PG2 |
| D18 | PD3 | | D40 | PG1 |
| D19 | PD2 | | D41 | PG0 |
| D20 (SDA) | PD1 | | D42 | PL7 |
| D21 (SCL) | PD0 | | D43 | PL6 |
| | | | D44 | PL5 |
| A0 (D54) | PF0 | | D45 | PL4 |
| A1 (D55) | PF1 | | D46 | PL3 |
| A2..A7 (D56..61) | PF2..PF7 | | D47 | PL2 |
| A8 (D62) | PK0 | | D48 | PL1 |
| A9..A15 (D63..69) | PK1..PK7 | | D49 | PL0 |
| | | | D50 | PB3 |
| | | | D51 | PB2 |
| | | | D52 | PB1 |
| | | | D53 | PB0 |

Note D13 on Mega is **PB7** (not PB5). In practice the build is table-driven in C#
(`Dictionary<(char port,int bit), int gpio>` per board) passed once into JS at
`createRunner`, rather than hard-coding the map in the bundle — keeps the bundle board-
agnostic. `isMega` selects which table.

---

## Wiring into the app (engine-agnostic UI)

Add `Foundry.Core/Simulation/SimulatorFactory.cs`:

```csharp
public static class SimulatorFactory {
    public static ISimulator For(Project.Project p, ComponentKb? kb = null) {
        var fqbn = FirmwareBuilder.Fqbn(p).ToLowerInvariant();
        bool avr = fqbn.Contains("avr") || fqbn.Contains("uno") || fqbn.Contains("nano")
                || fqbn.Contains("mega") || fqbn.Contains("leonardo");
        return avr ? new Avr8jsSimulator(kb) : new RenodeSimulator(kb);
    }
}
```

`SimulationViewModel` ctor changes `_simulator = simulator ?? new RenodeSimulator();` to
`_simulator = simulator ?? SimulatorFactory.For(project);`. Tests can still inject a fake
`ISimulator`. No `BreadboardControl` change — it already binds `PinStateSnapshot`.

RenodeSimulator's AVR `No(...)` message should be softened (it currently says avr8js is "a
future engine"); once Avr8jsSimulator exists the factory never sends AVR to Renode, but
update the copy for honesty.

---

## Risks / mitigations

- **Native V8 deploy on end-user machines.** `Microsoft.ClearScript.V8.Native.win-x64`
  carries the native DLL; it must land in `runtimes/win-x64/native/` and the app must be
  win-x64 (self-contained or framework-dependent). Verified to load on this dev box under
  net8.0. Mitigation: keep Option B (node sidecar) as a fallback `ISimulator` selected when
  `V8ScriptEngine` construction throws.
- **Bundle drift.** The IIFE is a build artifact pinned to `avr8js@0.21.0`. Pin the version
  and check the bundle in (or generate it in CI) so a future avr8js refactor can't silently
  change exports. The runner depends only on stable primitives (`CPU`, `avrInstruction`,
  `AVRIOPort`, `AVRTimer`).
- **Thread confinement.** `V8ScriptEngine` is not thread-safe; the run loop and all
  `engine.Script.*` access must stay on one background thread. `session.Push` is already
  lock-guarded, and `SimSession.Updated` is marshalled to the dispatcher by the VM.
- **Real-time pacing / runaway loops.** Firmware with tight busy-loops still advances
  `cycles`; cap cycles-per-tick and honor `SetSpeed`. A `while(1){}` sketch just spins the
  CPU model fast — bounded by our per-tick budget, so the UI thread is never blocked.
- **No-LED firmware.** If `GpioPinMap.Build` returns empty (no signal/i2c nets to MCU),
  `CanSimulate` returns `No("no MCU GPIO outputs…")` exactly like Renode.
- **delay() correctness depends on Timer0.** Must construct `AVRTimer(cpu, timer0Config)`
  (and 1/2) or `millis()`/`delay()` never progress — proven by the 1.60M-cycle spacing in
  the spike. Forgetting timers is the classic avr8js mistake.
- **Mega flash size.** ATmega2560 is 256 KB; size the HEX buffer per board (`0x40000` vs
  `0x8000`) — the `mega` flag in `createRunner` selects it.
- **PWM / analogWrite.** Timer compare output uses `timerOverridePin`; avr8js drives those
  pins via the same port listener, so PWM pins still report level edges (the breadboard
  shows them as on/off, not duty — acceptable for the glow UI; analog duty is future work).

---

## Cleanup

Spike scratch (`C:\temp\avr8js-spike`, `C:\temp\cs-probe`) is removed after writing this
doc. Nothing from the spike is committed; the only durable outputs are this design and the
recipe to (re)produce the bundle.
