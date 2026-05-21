/* ============================================================
   FOUNDRY — wiring diagram SVG
   Component blocks with pin headers, colored nets between them.
   Blueprint-ish technical aesthetic, ALL drawn (no images).
   ============================================================ */

const WIRING_SVG = () => {
  // canvas
  const W = 1100, H = 600;

  // component block helper
  const Block = ({ x, y, w, h, label, sub, pins = [], accent = "var(--accent)", footprint }) => (
    <g transform={`translate(${x}, ${y})`}>
      {/* shadow */}
      <rect x="3" y="3" width={w} height={h} fill="rgba(0,0,0,0.4)"/>
      {/* body */}
      <rect width={w} height={h} fill="#16161c" stroke="#3a3a46" strokeWidth="1"/>
      {/* corner cut */}
      <path d={`M 0 8 L 8 0`} stroke={accent} strokeWidth="1.5"/>
      {/* label band */}
      <rect y="0" width={w} height="22" fill="#0c0c10" stroke="#3a3a46" strokeWidth="1"/>
      <text x="10" y="15" fontFamily="var(--mono)" fontSize="10" fill="#ededee" letterSpacing="1.4">{label}</text>
      <text x={w - 10} y="15" fontFamily="var(--mono)" fontSize="9" fill="#6a6a72" textAnchor="end" letterSpacing="1">{sub}</text>
      {/* footprint accent line */}
      {footprint && (
        <text x="10" y={h - 8} fontFamily="var(--mono)" fontSize="8.5" fill="#6a6a72" letterSpacing="0.8">{footprint}</text>
      )}
      {/* pins */}
      {pins.map((p, i) => {
        const isLeft = p.side === "L";
        const isRight = p.side === "R";
        const isTop = p.side === "T";
        const isBottom = p.side === "B";
        let cx, cy, tx, ty, anchor;
        if (isLeft)   { cx = 0;     cy = p.at; tx = 8;    ty = p.at + 3; anchor = "start"; }
        if (isRight)  { cx = w;     cy = p.at; tx = w-8;  ty = p.at + 3; anchor = "end"; }
        if (isTop)    { cx = p.at;  cy = 0;    tx = p.at; ty = 14; anchor = "middle"; }
        if (isBottom) { cx = p.at;  cy = h;    tx = p.at; ty = h-8; anchor = "middle"; }
        const c = p.net === "power" ? "#ff4040" : p.net === "ground" ? "#888" : p.net === "i2c" ? "#c084fc" : "#5dd2ff";
        return (
          <g key={i}>
            <rect x={cx - 5} y={cy - 3} width="10" height="6" fill="#0c0c10" stroke={c} strokeWidth="1"/>
            <text x={tx} y={ty} textAnchor={anchor} fontFamily="var(--mono)" fontSize="9" fill="#b6b6bb" letterSpacing="0.6">
              {p.label}
            </text>
          </g>
        );
      })}
    </g>
  );

  // Net path with right-angle turns and labels
  const Net = ({ d, color, label }) => (
    <g>
      <path d={d} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="miter"/>
      <path d={d} fill="none" stroke={color} strokeWidth="6" opacity="0.12"/>
      {label && (
        <g>
          <rect x={label.x - 32} y={label.y - 8} width="64" height="16" fill="#07070a" stroke={color} strokeWidth="0.8"/>
          <text x={label.x} y={label.y + 3} textAnchor="middle" fontFamily="var(--mono)" fontSize="8.5" fill={color} letterSpacing="0.6">
            {label.t}
          </text>
        </g>
      )}
    </g>
  );

  return (
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: "100%", height: "auto" }}>
      <defs>
        <pattern id="grid-fine" width="20" height="20" patternUnits="userSpaceOnUse">
          <path d="M 20 0 L 0 0 0 20" fill="none" stroke="rgba(255,255,255,0.025)" strokeWidth="1"/>
        </pattern>
        <pattern id="grid-coarse" width="100" height="100" patternUnits="userSpaceOnUse">
          <path d="M 100 0 L 0 0 0 100" fill="none" stroke="rgba(255,255,255,0.05)" strokeWidth="1"/>
        </pattern>
      </defs>

      {/* backdrop */}
      <rect width={W} height={H} fill="url(#grid-fine)"/>
      <rect width={W} height={H} fill="url(#grid-coarse)"/>

      {/* margin marks */}
      <g fill="#3a3a46" fontFamily="var(--mono)" fontSize="9" letterSpacing="0.8">
        {[0, 200, 400, 600, 800, 1000].map(x => (
          <g key={`xm-${x}`}>
            <line x1={x} y1="0" x2={x} y2="8" stroke="#3a3a46"/>
            <text x={x + 4} y="14">{x}</text>
          </g>
        ))}
        {[0, 150, 300, 450, 600].map(y => (
          <g key={`ym-${y}`}>
            <line x1="0" y1={y} x2="8" y2={y} stroke="#3a3a46"/>
            <text x="12" y={y + 8}>{y}</text>
          </g>
        ))}
      </g>

      {/* title block — bottom right */}
      <g transform={`translate(${W - 260}, ${H - 90})`}>
        <rect width="252" height="82" fill="#0c0c10" stroke="#3a3a46"/>
        <line x1="0" y1="18" x2="252" y2="18" stroke="#3a3a46"/>
        <line x1="0" y1="48" x2="252" y2="48" stroke="#3a3a46"/>
        <line x1="170" y1="18" x2="170" y2="82" stroke="#3a3a46"/>
        <text x="8" y="13" fontFamily="var(--mono)" fontSize="9" fill="#ededee" letterSpacing="1.4">FOUNDRY · NETLIST</text>
        <text x="8" y="32" fontFamily="var(--serif)" fontSize="14" fill="#ededee">Cap. Soil Moisture Sentinel</text>
        <text x="8" y="44" fontFamily="var(--mono)" fontSize="8" fill="#6a6a72" letterSpacing="0.8">rev 03 · 9 nets · 8 components</text>
        <text x="8" y="63" fontFamily="var(--mono)" fontSize="8" fill="#6a6a72" letterSpacing="0.8">SHEET</text>
        <text x="8" y="76" fontFamily="var(--mono)" fontSize="11" fill="#ff5a1f">01 / 01</text>
        <text x="178" y="63" fontFamily="var(--mono)" fontSize="8" fill="#6a6a72" letterSpacing="0.8">SCALE</text>
        <text x="178" y="76" fontFamily="var(--mono)" fontSize="11" fill="#ededee">1:1</text>
      </g>

      {/* ===== COMPONENT BLOCKS ===== */}
      {/* Battery cell */}
      <Block x={60} y={420} w={180} h={120} label="BAT · 18650 Li-ion"
        sub="3.7V · 3000mAh" footprint="HLD-18650-1S"
        pins={[
          { side: "R", at: 40, label: "BAT+", net: "power" },
          { side: "R", at: 80, label: "BAT−", net: "ground" },
        ]}/>

      {/* TP4056 charger */}
      <Block x={60} y={250} w={180} h={120} label="CHG · TP4056"
        sub="USB-C 1A" footprint="TP4056-USB-C"
        pins={[
          { side: "T", at: 40, label: "VBUS", net: "power" },
          { side: "B", at: 90, label: "BAT+", net: "power" },
          { side: "B", at: 140, label: "GND", net: "ground" },
        ]}/>

      {/* Regulator */}
      <Block x={310} y={350} w={170} h={110} label="REG · MCP1700"
        sub="LDO · 3.3V" footprint="TO-92"
        pins={[
          { side: "L", at: 30, label: "VIN",  net: "power" },
          { side: "L", at: 60, label: "GND",  net: "ground" },
          { side: "L", at: 90, label: "VOUT", net: "power" },
          { side: "R", at: 55, label: "→3V3", net: "power" },
        ]}/>

      {/* MCU */}
      <Block x={540} y={140} w={310} h={330} label="MCU · ESP32 DevKit v1"
        sub="240MHz · WiFi+BLE" footprint="ESP32-DEVKITC-32E · 30 pins"
        accent="var(--accent)"
        pins={[
          { side: "L", at: 50,  label: "3V3",     net: "power" },
          { side: "L", at: 90,  label: "GND",     net: "ground" },
          { side: "L", at: 130, label: "5V",      net: "power" },
          { side: "L", at: 200, label: "GPIO34",  net: "signal" },
          { side: "L", at: 240, label: "GPIO0",   net: "signal" },
          { side: "L", at: 280, label: "GPIO13",  net: "signal" },
          { side: "R", at: 70,  label: "WIFI/ANT",net: "signal" },
          { side: "R", at: 130, label: "TX0",     net: "signal" },
          { side: "R", at: 170, label: "RX0",     net: "signal" },
          { side: "R", at: 230, label: "EN",      net: "signal" },
          { side: "R", at: 280, label: "USB",     net: "power" },
        ]}/>

      {/* Capacitive sensor */}
      <Block x={920} y={260} w={150} h={130} label="SEN · CAP v1.2"
        sub="0–3V analog" footprint="SEN-CAP-01"
        pins={[
          { side: "L", at: 40, label: "VCC",  net: "power" },
          { side: "L", at: 70, label: "GND",  net: "ground" },
          { side: "L", at: 100, label: "AOUT", net: "signal" },
        ]}/>

      {/* Button */}
      <Block x={310} y={100} w={150} h={80} label="BTN1 · TACT"
        sub="6×6mm" footprint="TL3301AF260QG"
        pins={[
          { side: "R", at: 30, label: "A", net: "signal" },
          { side: "R", at: 55, label: "B", net: "ground" },
        ]}/>

      {/* ===== NETS ===== */}
      {/* BAT+ → CHG.BAT+ (vertical) */}
      <Net d="M 240 460 L 270 460 L 270 388 L 200 388 L 200 370" color="#ff4040" label={{ x: 260, y: 430, t: "BAT+" }}/>
      {/* BAT- → CHG.GND */}
      <Net d="M 240 500 L 285 500 L 285 392 L 250 392 L 250 370" color="#888"/>

      {/* CHG.BAT+ via REG.VIN — NOTE this is conceptual: regulator gets battery+ */}
      <Net d="M 200 370 L 200 405 L 310 405 L 310 380" color="#ff4040" label={{ x: 250, y: 396, t: "VBAT" }}/>
      <Net d="M 250 370 L 250 410 L 310 410 L 310 410" color="#888"/>

      {/* REG.VOUT → MCU.5V (3.3V rail) */}
      <Net d="M 480 405 L 510 405 L 510 270 L 540 270" color="#ff4040" label={{ x: 510, y: 332, t: "3V3" }}/>

      {/* MCU.3V3 → SEN.VCC */}
      <Net d="M 540 190 L 510 190 L 510 60 L 900 60 L 900 300 L 920 300" color="#ff4040" label={{ x: 712, y: 56, t: "3V3" }}/>
      {/* MCU.GND → SEN.GND */}
      <Net d="M 540 230 L 500 230 L 500 80 L 880 80 L 880 330 L 920 330" color="#888"/>
      {/* MCU.GPIO34 → SEN.AOUT */}
      <Net d="M 540 340 L 870 340 L 870 360 L 920 360" color="#5dd2ff" label={{ x: 720, y: 336, t: "SIG · GPIO34" }}/>

      {/* MCU.GPIO0 → BTN1.A */}
      <Net d="M 540 380 L 480 380 L 480 130 L 460 130" color="#5dd2ff" label={{ x: 490, y: 256, t: "GPIO0" }}/>
      {/* BTN1.B → GND */}
      <Net d="M 460 155 L 475 155 L 475 90 L 700 90 L 700 140" color="#888"/>

      {/* USB-C VBUS (note) */}
      <Net d="M 100 250 L 100 220 L 145 220" color="#ff4040" label={{ x: 70, y: 230, t: "USB-C IN" }}/>
      <g>
        <rect x="30" y="200" width="60" height="40" fill="#0c0c10" stroke="#3a3a46"/>
        <text x="60" y="215" textAnchor="middle" fontFamily="var(--mono)" fontSize="8" fill="#6a6a72" letterSpacing="0.8">USB-C</text>
        <text x="60" y="232" textAnchor="middle" fontFamily="var(--mono)" fontSize="11" fill="#ededee">⟳ 5V</text>
      </g>

      {/* Antenna squiggle on MCU right side */}
      <g transform="translate(870, 200)">
        <line x1="0" y1="0" x2="20" y2="0" stroke="#5dd2ff" strokeWidth="2"/>
        <path d="M 20 0 q 4 -8 8 0 q 4 -8 8 0 q 4 -8 8 0" stroke="#5dd2ff" strokeWidth="1.6" fill="none"/>
        <text x="58" y="3" fontFamily="var(--mono)" fontSize="9" fill="#5dd2ff">WIFI</text>
      </g>

      {/* highlight nets connecting MCU to sensor (cluster annotation) */}
      <g transform={`translate(${W - 260}, 30)`}>
        <text x="0" y="0" fontFamily="var(--mono)" fontSize="9" fill="#ff5a1f" letterSpacing="1.4">SIGNAL CLUSTER · A</text>
        <line x1="0" y1="6" x2="240" y2="6" stroke="#ff5a1f" strokeWidth="0.8"/>
        <text x="0" y="22" fontFamily="var(--mono)" fontSize="8.5" fill="#b6b6bb">3 nets · soil → MCU</text>
        <text x="0" y="36" fontFamily="var(--mono)" fontSize="8.5" fill="#6a6a72">power · ground · signal</text>
      </g>
    </svg>
  );
};

window.WIRING_SVG = WIRING_SVG;
