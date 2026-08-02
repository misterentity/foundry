---
title: Firmware — pin map, compile, and one-click flash
domain: firmware
status: active
last-reviewed: 2026-08-02
verified-against:
  - source-read: Foundry.Core/Firmware/** at 813593b + uncommitted working tree (FirmwareBuilder.cs)
  - tool-contract: arduino-cli 1.5.1 (`compile`, `upload`, `board list --format json`)
---

# Firmware — pin map, compile, and one-click flash

> **What's in this doc:** how the pin map is derived from the netlist, FQBN inference and why it is security-sensitive, the compile paths (`CompileAsync` vs `CompileToImageAsync`), board detection, the flash plan and every refusal that guards an irreversible write, and the arduino-cli provisioning constants.
>
> **What's NOT:** how the firmware source is written by the model or repaired after a failed compile (→ [[generation]]); running the compiled image in an emulator (→ `Foundry.Core/Simulation/**`, not yet documented — see [[_backlog]]); the shared download/verify machinery `DownloadCliAsync` relies on (→ [[provisioning]]); the Firmware tab UI (→ [[desktop-ui]]).

## Pin map — derived, never typed

`Foundry.Core/Firmware/PinMap.cs:18` is the "pins always match the wiring" invariant in code. It reads `Project.Connections` and emits one `#define` per signal/I²C net that lands on an MCU GPIO, so it regenerates whenever the wiring changes (`Foundry.Core/Firmware/PinMap.cs:12-17`).

- **MCU detection is by count, not by prefix**: the component with the *most* GPIO-style pins wins (`Foundry.Core/Firmware/PinMap.cs:29-39`). The name regex at `:24` covers `GPIOn`/`GPn` (Pico) / `IOn` / `P0.n` (nRF) / `PA5` (STM32) / `Dn`,`An` (Arduino). Counting avoids mis-picking a small part that happens to carry a `D1`-style pad.
- Only `signal` and `i2c` nets become entries (`:54`).
- Macro names are `PIN_<PERIPH>_<PIN>` (`:63`); MCU-side direction is inferred as i2c / analog / output / input at `:68-71`; strapping-pin status is carried through from the KB (`:66`).
- Output order is stable (sorted by GPIO number) so regeneration is diff-friendly (`:77`).
- **Empty maps are never silent.** Two WARNs cover "no MCU detected" (`:48-49`) and "MCU found but nothing resolved" (`:74-75`) — the moat producing nothing must be visible.

`RenderHeader` (`:81`) writes the `pinmap.h` body, stamped GENERATED / do-not-edit (`:84-85`). The generation pass re-injects this file authoritatively after the model replies — see [[generation#pass-2--firmware]].

## FQBN inference is a security boundary

`FirmwareBuilder.Fqbn` (`Foundry.Core/Firmware/FirmwareBuilder.cs:74`) resolves the board id. **Inference from the actual components comes first** (`:76-85`), because the model often parrots the prompt's example `board` field. The keyword ladder is esp32 → esp8266/nodemcu/wemos → rp2040/pico → mega → nano → leonardo/32u4 → uno/atmega328.

The model-supplied `Firmware.Board` hint is honoured **only** when it is a clean, valid FQBN (`:87-92`). The comment there states the threat directly: the value flows into `arduino-cli compile --fqbn {fqbn}`, so a hint containing spaces or flags (`"...uno --additional-urls http://evil"`) could inject an attacker package index and execute code at compile time. `IsValidFqbn` (`:305-307`) accepts only `vendor:arch:board[:opts]` tokens. Unrecognised input falls back to `arduino:avr:uno` (`:93`).

Both compile entry points re-check the FQBN at the exec site as defence in depth (`:123-124` and `:182-184`) — keep that even though `Fqbn` already sanitises.

`IsValidPort` (`:310-312`) does the same for serial ports (`COMn` or `/dev/…`).

## Compile

| Entry point | Output | Cleans up? | Used by |
|---|---|---|---|
| `CompileAsync` (`Foundry.Core/Firmware/FirmwareBuilder.cs:96`) | diagnostics only | yes — temp sketch deleted (`:144`) | "Verify build" |
| `CompileToImageAsync` (`:152`) | ELF/HEX/BIN under `outputDir` | **no** — the emulator and flasher consume the artefacts (`:149-150`) | simulation, flash |

Shared mechanics: MicroPython short-circuits (no compiler) at `:98-99` / `:156-158`; the primary `.ino` is renamed to match the sketch folder, which arduino-cli requires (`:109-118`, `:171-180`); the process runs under `ProcessRunner.ArduinoTimeout` (`:127-129`).

### Dependencies: cores and libraries

Both compile paths install two things first, and **both used to be missing**, which is why the README's flagship ESP32 device could not be built at all:

- **The board core** — `EnsureCoreAsync` returns a user-facing reason instead of swallowing failures, and passes `--additional-urls` from the `BoardIndexUrls` table for `esp32` / `esp8266` / `rp2040`. Those platforms are **not** in arduino-cli's built-in index, so `core install esp32:esp32` could never resolve without it. The URLs are build-time constants; `Fqbn` still refuses a model-supplied hint containing flags (`IsValidFqbn`) precisely so an attacker-chosen index can't reach that command line.
- **The declared libraries** — `EnsureLibrariesAsync` installs `Project.Firmware.Libraries`, which the AI firmware pass has always populated (`ProjectGenerator.Firmware.cs:171`) and nothing ever consumed. Any sketch doing what its prompt instructs — real Wi-Fi/MQTT/sensor code — previously died on its first `#include`. `LibraryInstallSpec` is the pure decision: core-bundled and `built-in` entries are skipped (they aren't in the library index), names are validated as strictly as the FQBN, and a malformed version degrades to "latest" rather than reaching the shell.

`Parse` (`:462`) reads arduino-cli's JSON when available and falls back to scraping stderr, producing `BuildDiagnostic` rows (`:6`). Artefacts are resolved by suffix — `.ino.elf` / `.ino.hex` / `.ino.bin` (`:192-194`, helper at `:209`).

## Board detection

`ParseBoardList` (`:219`) is pure and handles **both** arduino-cli shapes: a bare array and the newer `{ "detected_ports": [...] }` wrapper (`:225-229`). A port with no `matching_boards` entry yields `Fqbn == null` and the label "Unknown board (…)" (`:248`).

`ListBoardsAsync` (`:257`) shells out to `board list --format json`; `DetectPortsAsync` (`:279`) orders candidates so the picker's default is the most likely target: vendor-matching boards first, then identified boards, then bare ports (`:285-288`).

## Flash — every guard on the irreversible write

`BuildFlashPlan` (`:320`) resolves what will actually be written. **The physical board wins**: when the connected board reports a concrete FQBN it overrides the inferred one, because you cannot safely flash an ESP32 image onto an AVR (`:314-318`, `:333-340`). The inferred FQBN is only a fallback for an unidentified port (`:325-328`).

`FlashPlan` (`:297`) carries the confirm text the UI must show before writing (`:342`) and a `MismatchWarning` when the families differ (`:337-339`). `FqbnSource` (`:292`) records which rule applied — `Inferred`, `ExactMatch`, or `PortPreferredOverInferred`.

`UploadAsync` (`:352`) refuses in six distinct ways before it ever writes:

1. arduino-cli not installed → `NotInstalled` (`:355-356`).
2. MicroPython → copy the `.py` instead (`:358-359`).
3. **No silent first-port auto-flash**: more than one board detected and no explicit target → refuse and ask (`:366-368`).
4. Invalid port or FQBN shape (`:373-375`).
5. Cross-family mismatch without `forceMismatch` (`:376-378`) — the force flag is only set after a user confirm.
6. Firmware didn't compile, **or** the compiled image's FQBN doesn't equal the selected one (`:385-392`).

Only then does it upload, reusing the already-built artefacts via `--input-dir` so nothing is recompiled between the check and the write (`:395-396`).

## Provisioning constants

arduino-cli is pinned and hash-verified (`:49-53`):

| Constant | Value |
|---|---|
| `ArduinoCliVersion` | `1.5.1` |
| `ArduinoCliZipName` | `arduino-cli_1.5.1_Windows_64bit.zip` |
| `ArduinoCliSha256` | `FABE42E0EB04D00E776A66178299FF95A46C623DBC260F997E58FD514853DD40` |

`Locate` (`:58`) prefers the app-local copy at `%LocalAppData%/Foundry/tools/arduino-cli.exe` (`:55-56`) before PATH. `DownloadCliAsync` (`:443`) fetches it on demand; the integrity mechanics it uses are documented in [[provisioning]], and it is exposed to the Settings panel through `ToolchainProvisioner` (`Foundry.Core/Provisioning/ToolchainProvisioner.cs:92-95`).

## Editing this domain safely

- Adding a board family means editing the `Fqbn` ladder (`:76-85`) **and** checking whether the netlist-side pin naming is already covered by `PinMap`'s `McuPinName` regex (`Foundry.Core/Firmware/PinMap.cs:24`) and by [[pcb]]'s footprint/symbol resolution. The three are independent tables and drift apart silently.
- Never relax `IsValidFqbn`/`IsValidPort` — they are the only thing between AI-controlled text and a command line.
- Flash behaviour is covered by `Foundry.Tests/FlashTests.cs` and `Foundry.Tests/FqbnSafetyTests.cs`; both have uncommitted additions in the current working tree.
