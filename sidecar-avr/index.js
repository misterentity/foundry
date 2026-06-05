// Foundry AVR8 runtime entry — bundled by esbuild into dist/avr8js-runtime.js as a runtime-free IIFE.
//
// This file owns the run loop and the Intel HEX parser; avr8js@0.21.0 exposes no AVRRunner/loadProgram
// (that class lives only in the Wokwi web demo). It exposes the primitives we drive directly:
// CPU, avrInstruction, AVRIOPort, PinState, portB/C/D...Config, AVRTimer, timer0/1/2Config.
//
// The C# ClearScript host loads the bundle, then calls globalThis.Avr8.createRunner(...).
// The bundle is board-agnostic: the host passes the (portLetter,bit)->Arduino-pin table in, so adding
// a new AVR board never requires rebuilding the JS.

import {
  CPU,
  avrInstruction,
  AVRIOPort,
  PinState,
  portBConfig,
  portCConfig,
  portDConfig,
  portEConfig,
  portFConfig,
  portGConfig,
  portHConfig,
  portJConfig,
  portKConfig,
  portLConfig,
  AVRTimer,
  timer0Config,
  timer1Config,
  timer2Config,
} from 'avr8js';

// Port-letter -> avr8js port config. Letters A..L; ATmega328P only populates B/C/D, the rest are Mega.
const PORT_CONFIGS = {
  B: portBConfig,
  C: portCConfig,
  D: portDConfig,
  E: portEConfig,
  F: portFConfig,
  G: portGConfig,
  H: portHConfig,
  J: portJConfig,
  K: portKConfig,
  L: portLConfig,
};

// Parse an Intel HEX file into a flash image of `size` bytes (zero-filled).
// Honors record types: 00 data, 01 EOF, 02 extended segment addr, 04 extended linear addr.
function parseIntelHex(text, size) {
  const out = new Uint8Array(size);
  let upper = 0; // upper 16 bits of address from type 02 (<<4) or type 04 (<<16)
  const lines = text.split(/\r?\n/);
  for (const raw of lines) {
    const line = raw.trim();
    if (!line || line[0] !== ':') continue;
    const bytes = [];
    for (let i = 1; i + 1 < line.length; i += 2) {
      bytes.push(parseInt(line.substr(i, 2), 16));
    }
    if (bytes.length < 5) continue;
    // Intel HEX record checksum: the sum of ALL record bytes (incl. the trailing checksum byte) is 0 mod 256.
    // A mismatch (or a non-hex pair → NaN) means corrupt data — refuse it rather than simulating garbage.
    const sum = bytes.reduce((a, b) => (a + b) & 0xff, 0);
    if (sum !== 0) throw new Error("invalid Intel HEX: record checksum mismatch");
    const len = bytes[0];
    const addr = (bytes[1] << 8) | bytes[2];
    const type = bytes[3];
    if (type === 0x00) {
      const base = upper + addr;
      for (let i = 0; i < len; i++) {
        const a = base + i;
        if (a < size) out[a] = bytes[4 + i];
      }
    } else if (type === 0x01) {
      break; // EOF
    } else if (type === 0x02) {
      upper = ((bytes[4] << 8) | bytes[5]) << 4;
    } else if (type === 0x04) {
      upper = ((bytes[4] << 8) | bytes[5]) << 16;
    }
    // type 03/05 (start address) ignored — not relevant to flash contents
  }
  return out;
}

// createRunner(hexText, host, mega, portMap)
//   hexText  : string         Intel HEX program image (FirmwareBuilder image.HexPath contents)
//   host     : object         must expose onPin(gpio:int, high:bool) — called only on level CHANGE
//   mega     : bool           true => ATmega2560 (256KB flash, ports A..L); false => ATmega328P (32KB, B/C/D)
//   portMap  : object         { "<LETTER><BIT>": arduinoPin } e.g. { "B5": 13, "D2": 2, ... }; -1 / absent => ignore
// returns { runCycles(n), cycles() }
function createRunner(hexText, host, mega, portMap) {
  const flashSize = mega ? 0x40000 : 0x8000; // 256KB Mega vs 32KB Uno/Nano
  const progBytes = parseIntelHex(hexText, flashSize);

  // CPU wants program memory as a Uint16Array view over the flash bytes.
  const cpu = new CPU(new Uint16Array(progBytes.buffer));

  // Timers MUST exist or millis()/delay() never advance (Timer0 drives the Arduino millis tick).
  new AVRTimer(cpu, timer0Config);
  new AVRTimer(cpu, timer1Config);
  new AVRTimer(cpu, timer2Config);

  // Resolve which port letters this board has, normalize portMap keys to upper-case.
  const map = {};
  if (portMap) {
    for (const k in portMap) map[String(k).toUpperCase()] = portMap[k];
  }

  // Distinct port letters referenced by the map (so we only build the ports we map).
  const letters = new Set();
  for (const k in map) {
    const letter = k[0];
    if (PORT_CONFIGS[letter]) letters.add(letter);
  }
  // ATmega328P: ensure B/C/D exist even if map is sparse (harmless if unused).
  if (!mega) { letters.add('B'); letters.add('C'); letters.add('D'); }

  // Per-(letter,bit) last reported level, so we only fire host.onPin on an actual edge.
  const lastLevel = {}; // key "<LETTER><BIT>" -> bool

  for (const letter of letters) {
    const cfg = PORT_CONFIGS[letter];
    if (!cfg) continue;
    const port = new AVRIOPort(cpu, cfg);
    port.addListener(() => {
      for (let bit = 0; bit < 8; bit++) {
        const key = letter + bit;
        const gpio = map[key];
        if (gpio === undefined || gpio === null || gpio < 0) continue; // unmapped (crystal/reset/etc.)
        const high = port.pinState(bit) === PinState.High;
        if (lastLevel[key] !== high) {
          lastLevel[key] = high;
          host.onPin(gpio, high);
        }
      }
    });
  }

  return {
    runCycles(n) {
      const target = cpu.cycles + n;
      while (cpu.cycles < target) {
        avrInstruction(cpu);
        cpu.tick();
      }
    },
    cycles() {
      return cpu.cycles;
    },
  };
}

globalThis.Avr8 = { createRunner, parseIntelHex };
