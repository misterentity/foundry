/* ============================================================
   FOUNDRY — tab contents
   Overview · BOM · Wiring · Enclosure · Firmware · Validation · Guide
   ============================================================ */

/* ------------------------------------------------------------
   OVERVIEW
   ------------------------------------------------------------ */
const OverviewTab = () => {
  const P = window.PROJECT;
  return (
    <div className="page">
      <PageHead
        kicker={`${P.id} · UPDATED ${P.updated}`}
        h1="Cap. Soil Moisture"
        h1em="Sentinel."
        sub="Battery-powered, IP65 capacitive sensor that texts you when soil is dry. Compiled from a single prompt — every output below is derived from the same canonical Project document, so it stays mutually consistent as you iterate."
        right={
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn btn--ghost"><Icon name="download" size={12}/> EXPORT BUNDLE</button>
            <button className="btn btn--primary"><Icon name="play" size={11}/> BUILD PLAN</button>
          </div>
        }
      />

      {/* KPI strip */}
      <div className="kpi-strip">
        <div className="kpi">
          <div className="kpi__label">Components</div>
          <div className="kpi__value"><em>{P.kpis.parts}</em></div>
          <div className="kpi__delta">+0 from rev 02</div>
        </div>
        <div className="kpi">
          <div className="kpi__label">Project cost</div>
          <div className="kpi__value">$<em>{P.kpis.cost.toFixed(2)}</em></div>
          <div className="kpi__delta">–$1.85 (gland swap)</div>
        </div>
        <div className="kpi">
          <div className="kpi__label">Battery life</div>
          <div className="kpi__value warn"><em>{P.kpis.battery_days}</em><span className="kpi__unit"> d</span></div>
          <div className="kpi__delta bad">Below 60-day goal</div>
        </div>
        <div className="kpi">
          <div className="kpi__label">Print mass</div>
          <div className="kpi__value"><em>{P.kpis.print_g}</em><span className="kpi__unit"> g</span></div>
          <div className="kpi__delta">2h 14m @ 0.2mm</div>
        </div>
      </div>

      {/* Architecture summary */}
      <div className="section">
        <div className="section__head">
          <h2 className="section__title">Architecture</h2>
          <span className="section__sub">4 subsystems · 9 nets</span>
        </div>
        <div className="subsystems">
          {P.subsystems.map((s, i) => (
            <div key={s.id} className="subsystem">
              <div style={{ display: "flex", justifyContent: "space-between" }}>
                <div className="subsystem__role">{String(i + 1).padStart(2, "0")} · {s.role}</div>
                <div className="kicker">{s.mpn}</div>
              </div>
              <div className="subsystem__name">{s.name}</div>
              <div className="subsystem__line"></div>
              <div className="subsystem__specs">
                {s.specs.map(([k, v]) => (
                  <React.Fragment key={k}>
                    <span style={{ color: "var(--ink-faint)" }}>{k}</span>
                    <span style={{ textAlign: "right", color: "var(--ink-soft)" }}>{v}</span>
                  </React.Fragment>
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* 2-up: validation + sourcing */}
      <div style={{ display: "grid", gridTemplateColumns: "1.2fr 1fr", gap: 0, border: "1px solid var(--hairline-2)" }}>
        <div style={{ padding: 22, borderRight: "1px solid var(--hairline)" }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
            <h3 className="serif" style={{ fontSize: 24, margin: 0 }}>Validation status</h3>
            <span className="tag tag--warn">2 warn · 1 info · 2 pass</span>
          </div>
          <div style={{ marginTop: 16, display: "grid", gap: 10 }}>
            {P.findings.slice(0, 3).map((f, i) => (
              <div key={i} style={{
                display: "grid", gridTemplateColumns: "auto 1fr auto", gap: 14, alignItems: "baseline",
                paddingBottom: 10, borderBottom: i < 2 ? "1px solid var(--hairline)" : "none",
              }}>
                <span className={`tag tag--${f.sev}`}>{f.num}</span>
                <div>
                  <div style={{ color: "var(--ink)", fontSize: 13 }}>{f.title}</div>
                  <div style={{ color: "var(--ink-mute)", fontSize: 11, marginTop: 2 }}>{f.code}</div>
                </div>
                {f.fix && <button className="btn btn--ghost" style={{ padding: "5px 10px", fontSize: 10 }}>{f.fix}</button>}
              </div>
            ))}
          </div>
        </div>

        <div style={{ padding: 22 }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
            <h3 className="serif" style={{ fontSize: 24, margin: 0 }}>Sourcing</h3>
            <span className="tag tag--ok">All in stock</span>
          </div>
          <div style={{ marginTop: 16, display: "grid", gap: 0 }}>
            {[
              ["DigiKey", 4, 18.13, "ok"],
              ["Mouser",  3, 8.61,  "ok"],
              ["Amazon",  2, 11.68, "warn"],
            ].map(([d, c, $, st], i) => (
              <div key={d} style={{
                display: "grid", gridTemplateColumns: "1fr auto auto auto",
                gap: 16, alignItems: "center",
                padding: "12px 0",
                borderBottom: i < 2 ? "1px solid var(--hairline)" : "none",
                fontFamily: "var(--mono)", fontSize: 12,
              }}>
                <span style={{ color: "var(--info)", letterSpacing: "0.06em" }}>{d}</span>
                <span style={{ color: "var(--ink-mute)" }}>{c} lines</span>
                <span style={{ color: "var(--ink)" }}>${$.toFixed(2)}</span>
                <span className={`tag tag--${st}`} style={{ fontSize: 9 }}>{st === "ok" ? "ready" : "low stock"}</span>
              </div>
            ))}
          </div>
          <button className="btn btn--primary" style={{ marginTop: 18, width: "100%" }}>
            <Icon name="cart" size={12}/> ADD ALL TO DIGIKEY CART
          </button>
        </div>
      </div>
    </div>
  );
};

/* ------------------------------------------------------------
   BOM
   ------------------------------------------------------------ */
const BomTab = () => {
  const P = window.PROJECT;
  const total = P.bom.reduce((s, l) => s + l.qty * l.price, 0);
  return (
    <div className="page">
      <PageHead
        kicker={`BILL OF MATERIALS · ${P.bom.length} LINES · LIVE PRICING via NEXAR`}
        h1="What you'll"
        h1em="buy."
        sub="Real-time pricing and availability per MPN, with one-click cart links to DigiKey, Mouser, or Amazon. Substitute parts with chat ('swap the OLED for e-paper') and downstream stages re-run."
        right={
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn btn--ghost"><Icon name="refresh" size={12}/> REFRESH PRICES</button>
            <button className="btn btn--primary"><Icon name="cart" size={12}/> CART · ${total.toFixed(2)}</button>
          </div>
        }
      />

      <div style={{ border: "1px solid var(--hairline-2)" }}>
        <table className="bom-table">
          <thead>
            <tr>
              <th style={{ width: 60 }} className="num">Qty</th>
              <th>Component</th>
              <th style={{ width: 200 }}>MPN</th>
              <th style={{ width: 90 }} className="num">Unit</th>
              <th style={{ width: 90 }} className="num">Ext.</th>
              <th style={{ width: 100 }}>Stock</th>
              <th style={{ width: 110 }}>Distributor</th>
              <th style={{ width: 90 }}>Lead</th>
              <th style={{ width: 30 }}></th>
            </tr>
          </thead>
          <tbody>
            {P.bom.map((l, i) => {
              const low = l.stock < 100;
              return (
                <tr key={i}>
                  <td className="num">{l.qty}×</td>
                  <td>
                    <div className="name">{l.name}</div>
                    <div style={{ color: "var(--ink-faint)", fontSize: 10.5, marginTop: 2, letterSpacing: "0.06em" }}>{l.note}</div>
                  </td>
                  <td className="mpn">{l.mpn}</td>
                  <td className="num price">${l.price.toFixed(2)}</td>
                  <td className="num price">${(l.qty * l.price).toFixed(2)}</td>
                  <td className={low ? "stock-low" : "stock-ok"}>
                    <span style={{ display: "inline-block", width: 7, height: 7, background: "currentColor", marginRight: 8 }}/>
                    {l.stock.toLocaleString()}
                  </td>
                  <td className="distributor">{l.dist}</td>
                  <td>{l.lead}</td>
                  <td>
                    <button className="btn btn--ghost" style={{ padding: "4px 8px", fontSize: 10, borderColor: "var(--hairline-2)" }}>
                      <Icon name="chev" size={10}/>
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
          <tfoot>
            <tr>
              <td colSpan="3" style={{ borderBottom: "none", color: "var(--ink-mute)", fontSize: 11, letterSpacing: "0.16em", textTransform: "uppercase", padding: "16px 14px" }}>
                Subtotal · 8 lines · 10 units
              </td>
              <td colSpan="2" className="num" style={{ borderBottom: "none", fontFamily: "var(--serif)", fontSize: 28, color: "var(--accent)", letterSpacing: "-0.01em" }}>
                ${total.toFixed(2)}
              </td>
              <td colSpan="4" style={{ borderBottom: "none", color: "var(--ink-mute)", fontSize: 11, padding: "16px 14px", textAlign: "right" }}>
                + shipping est. $4.99 · taxes excluded
              </td>
            </tr>
          </tfoot>
        </table>
      </div>

      {/* substitutions strip */}
      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr 1fr", gap: 0, border: "1px solid var(--hairline-2)" }}>
        {[
          ["Substitute available", "M12-GLAND → PG7-GLD", "+0.07 · in stock", "warn"],
          ["Bulk discount", "TL3301AF260QG @ 25+", "−18% from Mouser", "info"],
          ["Newer revision", "TP4056 → IP5306 1A", "USB-C + boost", "info"],
        ].map(([title, body, hint, sev], i) => (
          <div key={i} style={{
            padding: 16, borderRight: i < 2 ? "1px solid var(--hairline)" : "none",
            display: "grid", gap: 6,
          }}>
            <div className="kicker" style={{ color: sev === "warn" ? "var(--warn)" : "var(--info)" }}>{title}</div>
            <div className="serif" style={{ fontSize: 18, lineHeight: 1.1 }}>{body}</div>
            <div style={{ fontSize: 11, color: "var(--ink-mute)" }}>{hint}</div>
          </div>
        ))}
      </div>
    </div>
  );
};

/* ------------------------------------------------------------
   WIRING
   ------------------------------------------------------------ */
const WiringTab = () => {
  const P = window.PROJECT;
  return (
    <div className="page">
      <PageHead
        kicker="NETLIST → DIAGRAM · ORTHOGONAL LAYOUT · v3"
        h1="How it"
        h1em="connects."
        sub="The netlist is the source of truth — pin map, firmware, and enclosure cutouts all derive from these connections. Power runs red, ground gray, signal cyan."
        right={
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn btn--ghost">SVG</button>
            <button className="btn btn--ghost">PNG</button>
            <button className="btn">KICAD .NET</button>
          </div>
        }
      />

      <div className="wiring-stage">
        <WIRING_SVG/>
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 0, border: "1px solid var(--hairline-2)" }}>
        {/* legend */}
        <div style={{ padding: 18, borderRight: "1px solid var(--hairline)" }}>
          <div className="kicker">Legend</div>
          <div className="wiring-legend" style={{ marginTop: 12 }}>
            <span><i style={{ background: "var(--power)" }}/>Power</span>
            <span><i style={{ background: "var(--ground)" }}/>Ground</span>
            <span><i style={{ background: "var(--signal)" }}/>Signal</span>
            <span><i style={{ background: "var(--signal-2)" }}/>I²C / Bus</span>
          </div>

          <div className="kicker" style={{ marginTop: 22 }}>Conventions</div>
          <ul style={{ marginTop: 10, paddingLeft: 18, color: "var(--ink-soft)", fontSize: 12, lineHeight: 1.7 }}>
            <li>Orthogonal routing · 4 mm pitch grid</li>
            <li>Pin labels are silkscreen-exact (MCU's silkscreen names)</li>
            <li>USB-C and antenna are off-board · drawn as call-outs</li>
            <li>Junction = filled square · 5 × 6 px</li>
          </ul>
        </div>

        {/* nets table */}
        <div style={{ padding: 0 }}>
          <div style={{ padding: "16px 18px", borderBottom: "1px solid var(--hairline)" }}>
            <div className="kicker">Connection ledger · {P.connections.length} nets</div>
          </div>
          <table className="bom-table">
            <thead>
              <tr>
                <th style={{ width: 32 }}>#</th>
                <th>From</th>
                <th>To</th>
                <th style={{ width: 80 }}>Net</th>
              </tr>
            </thead>
            <tbody>
              {P.connections.map((c, i) => {
                const colors = { power: "var(--power)", ground: "var(--ground)", signal: "var(--signal)", i2c: "var(--signal-2)" };
                return (
                  <tr key={i}>
                    <td style={{ color: "var(--ink-faint)" }}>{String(i + 1).padStart(2, "0")}</td>
                    <td className="mpn">{c.from}</td>
                    <td className="mpn">{c.to}</td>
                    <td>
                      <span style={{ display: "inline-flex", gap: 8, alignItems: "center" }}>
                        <i style={{ width: 16, height: 2, background: colors[c.net] }}/>
                        <span style={{ color: colors[c.net], fontSize: 11, letterSpacing: "0.08em", textTransform: "uppercase" }}>{c.net}</span>
                      </span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
};

/* ------------------------------------------------------------
   ENCLOSURE
   ------------------------------------------------------------ */
const EnclosureTab = () => {
  const P = window.PROJECT;
  const E = P.enclosure;
  const [view, setView] = React.useState("ISO");

  return (
    <div className="page">
      <PageHead
        kicker="PARAMETRIC CAD · build123d · OpenCASCADE 7.7.2"
        h1="What you'll"
        h1em="print."
        sub="A snap-lid enclosure sized from your component footprints. Cutouts and standoffs are derived directly from the components — ports always line up. Export as STL/3MF for slicing or STEP for manual editing."
        right={
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn btn--ghost">STEP</button>
            <button className="btn btn--ghost">3MF</button>
            <button className="btn btn--primary"><Icon name="download" size={12}/> STL · 1.42 MB</button>
          </div>
        }
      />

      <div className="encl-grid">
        <div className="encl-preview">
          <ENCLOSURE_SVG/>
          {/* view buttons */}
          <div style={{ position: "absolute", left: 14, bottom: 14, display: "flex", gap: 0, border: "1px solid var(--hairline-2)" }}>
            {["ISO", "FRONT", "TOP", "EXPLODED"].map((v, i) => (
              <button key={v} onClick={() => setView(v)} style={{
                padding: "8px 12px",
                fontFamily: "var(--mono)", fontSize: 10, letterSpacing: "0.14em", textTransform: "uppercase",
                background: view === v ? "var(--accent)" : "var(--surface-1)",
                color: view === v ? "#160600" : "var(--ink-soft)",
                borderLeft: i > 0 ? "1px solid var(--hairline-2)" : "none",
                fontWeight: view === v ? 600 : 400,
              }}>{v}</button>
            ))}
          </div>
          {/* viewport toolbar */}
          <div style={{ position: "absolute", right: 14, bottom: 14, display: "flex", flexDirection: "column", gap: 4 }}>
            <button className="btn btn--ghost" style={{ padding: "5px 8px", fontSize: 10 }}>RESET CAM</button>
            <button className="btn btn--ghost" style={{ padding: "5px 8px", fontSize: 10 }}>ORTHO</button>
            <button className="btn btn--ghost" style={{ padding: "5px 8px", fontSize: 10 }}>MEASURE</button>
          </div>
        </div>

        <div className="encl-controls">
          <div>
            <div className="kicker">Dimensions · inner</div>
            <div className="encl-readout" style={{ marginTop: 8 }}>
              <div className="cell"><div className="l">Length</div><div className="v">{E.inner[0]}</div><div className="u">mm</div></div>
              <div className="cell"><div className="l">Width</div><div className="v">{E.inner[1]}</div><div className="u">mm</div></div>
              <div className="cell"><div className="l">Height</div><div className="v">{E.inner[2]}</div><div className="u">mm</div></div>
              <div className="cell"><div className="l">Wall</div><div className="v">{E.wall.toFixed(1)}</div><div className="u">mm</div></div>
            </div>
          </div>

          <div>
            <div className="kicker">Cutouts · derived from footprints</div>
            <div style={{ marginTop: 8, border: "1px solid var(--hairline-2)" }}>
              {E.cutouts.map((c, i) => (
                <div key={i} style={{
                  display: "grid", gridTemplateColumns: "auto 1fr auto",
                  padding: "10px 12px",
                  borderBottom: i < E.cutouts.length - 1 ? "1px solid var(--hairline)" : "none",
                  fontFamily: "var(--mono)", fontSize: 11,
                  alignItems: "center",
                }}>
                  <span style={{ color: "var(--accent)", letterSpacing: "0.16em", textTransform: "uppercase", width: 60 }}>{c.face}</span>
                  <span style={{ color: "var(--ink)" }}>{c.label}</span>
                  <span style={{ color: "var(--ink-mute)" }}>
                    {c.shape === "rect" ? `${c.size[0]} × ${c.size[1]} mm` : `⌀ ${c.d} mm`}
                  </span>
                </div>
              ))}
            </div>
          </div>

          <div>
            <div className="kicker">Print estimate · 0.2 mm PETG</div>
            <div style={{ marginTop: 8, display: "grid", gridTemplateColumns: "1fr 1fr", gap: 0, border: "1px solid var(--hairline-2)" }}>
              <div style={{ padding: "12px 14px", borderRight: "1px solid var(--hairline)" }}>
                <div className="kicker">Mass</div>
                <div className="serif" style={{ fontSize: 32, lineHeight: 1, marginTop: 4 }}>{E.mass_g}<span style={{ fontSize: 14, color: "var(--ink-mute)", marginLeft: 4 }}>g</span></div>
              </div>
              <div style={{ padding: "12px 14px" }}>
                <div className="kicker">Time</div>
                <div className="serif" style={{ fontSize: 32, lineHeight: 1, marginTop: 4 }}>{E.print_h}</div>
              </div>
            </div>
          </div>

          <button className="btn btn--primary" style={{ width: "100%" }}>
            <Icon name="download" size={12}/> EXPORT STL + 3MF + STEP
          </button>
        </div>
      </div>
    </div>
  );
};

/* ------------------------------------------------------------
   FIRMWARE
   ------------------------------------------------------------ */
const FirmwareTab = () => {
  const P = window.PROJECT;
  const [active, setActive] = React.useState(0);

  // simple syntax-highlighted code lines
  const FILE_CODE = [
    // main.ino
    [
      ["// FOUNDRY · Cap. Soil Moisture Sentinel", "co"],
      ["// Pin map is GENERATED from the netlist — do not edit by hand.", "co"],
      ["// See pinmap.h", "co"],
      [""],
      ["#include <WiFi.h>", "pp"],
      ["#include <HTTPClient.h>", "pp"],
      ["#include <ArduinoJson.h>", "pp"],
      ["#include \"pinmap.h\"", "pp"],
      ["#include \"wifi.h\"", "pp"],
      [""],
      ["constexpr uint64_t SLEEP_US = ", "kw", "6ULL * 60ULL * 60ULL * 1000000ULL", "nm", ";  // 6 h", "co"],
      ["constexpr float DRY_THRESHOLD = ", "kw", "0.32f", "nm", ";", ""],
      [""],
      ["void ", "kw", "setup", "fn", "() {", ""],
      ["  ", "", "Serial", "fn", ".begin(", "", "115200", "nm", ");", ""],
      ["  ", "", "pinMode", "fn", "(PIN_SENSOR_AOUT, INPUT);", ""],
      ["  ", "", "analogReadResolution(12);", ""],
      [""],
      ["  ", "kw", "float", "", " moisture = ", "fn", "readMoisture", "", "();", ""],
      ["  ", "kw", "if", "", " (moisture < DRY_THRESHOLD) {", ""],
      ["    ", "fn", "alertTwilio", "", "(moisture);", ""],
      ["  }", ""],
      [""],
      ["  ", "fn", "esp_deep_sleep", "", "(SLEEP_US);", ""],
      ["}", ""],
      [""],
      ["float ", "kw", "readMoisture", "fn", "() {", ""],
      ["  ", "kw", "int", "", " raw = ", "fn", "analogRead", "", "(PIN_SENSOR_AOUT);", ""],
      ["  ", "kw", "return", "", " 1.0f - ((float)raw / 4095.0f);  ", "co", "// dry→0, wet→1", ""],
      ["}", ""],
    ],
    // pinmap.h
    [
      ["// GENERATED — derived from Project.connections", "co"],
      ["// Do not edit; re-runs on every wiring change.", "co"],
      [""],
      ["#pragma once", "pp"],
      [""],
      ["// from net: SIGNAL · MCU.GPIO34 ↔ SENSOR.AOUT", "co"],
      ["#define ", "kw", "PIN_SENSOR_AOUT", "fn", "  ", "", "34", "nm", "", ""],
      [""],
      ["// from net: SIGNAL · MCU.GPIO0 ↔ BTN1.A    [strapping pin — see W·04]", "co"],
      ["#define ", "kw", "PIN_BUTTON_RST", "fn", "   ", "", "0", "nm", "", ""],
      [""],
      ["// Power · Ground rails — informational", "co"],
      ["#define ", "kw", "RAIL_3V3_MV", "fn", "       ", "", "3300", "nm", "", ""],
      ["#define ", "kw", "RAIL_GND_MV", "fn", "       ", "", "0", "nm", "", ""],
      [""],
      ["// ADC reference (ESP32 default attenuation 11dB → ~3.3V)", "co"],
      ["#define ", "kw", "ADC_REF_MV", "fn", "        ", "", "3300", "nm", "", ""],
    ],
    // wifi.h
    [
      ["#pragma once", "pp"],
      ["", ""],
      ["// TODO: fill in your secrets — these are NEVER written to the Project file.", "co"],
      ["#define ", "kw", "WIFI_SSID", "fn", "          ", "st", "\"YOUR_SSID\"", ""],
      ["#define ", "kw", "WIFI_PASS", "fn", "          ", "st", "\"YOUR_PASSWORD\"", ""],
      ["", ""],
      ["// Twilio HTTPS webhook", "co"],
      ["#define ", "kw", "TWILIO_SID", "fn", "         ", "st", "\"ACxxxxxxxxxxxxxxxxxx\"", ""],
      ["#define ", "kw", "TWILIO_TOKEN", "fn", "       ", "st", "\"xxxxxxxxxxxxxxxxxxxx\"", ""],
      ["#define ", "kw", "TWILIO_FROM", "fn", "        ", "st", "\"+15555550100\"", ""],
      ["#define ", "kw", "ALERT_TO", "fn", "           ", "st", "\"+15555550199\"", ""],
    ],
    // platformio.ini
    [
      ["[env:esp32dev]", "kw"],
      ["platform   = ", "fn", "espressif32@^6.5.0", "st"],
      ["board      = ", "fn", "esp32dev", "st"],
      ["framework  = ", "fn", "arduino", "st"],
      ["monitor_speed = ", "fn", "115200", "nm"],
      ["upload_speed  = ", "fn", "460800", "nm"],
      ["lib_deps = ", "fn"],
      ["  bblanchon/ArduinoJson@^7.1.0", "st"],
      ["build_flags = ", "fn"],
      ["  -D CONFIG_DEEP_SLEEP", "co"],
    ],
  ];

  const renderLine = (tokens, idx) => {
    // tokens is an array of alternating [text, klass]
    const out = [];
    for (let i = 0; i < tokens.length; i += 2) {
      const text = tokens[i] ?? "";
      const klass = tokens[i + 1];
      if (!text) continue;
      out.push(klass ? <span key={i} className={`tk-${klass}`}>{text}</span> : <span key={i}>{text}</span>);
    }
    return out;
  };

  return (
    <div className="page">
      <PageHead
        kicker={`${P.firmware.platform} · ${P.firmware.board}`}
        h1="Code"
        h1em="ready."
        sub={<>Starter sketch with a pin map <em style={{ color: "var(--accent)", fontStyle: "normal" }}>generated from the netlist</em> — there are no hand-typed pins. Open the exported folder in PlatformIO or Arduino IDE 2.x and flash.</>}
        right={
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn btn--ghost"><Icon name="folder" size={12}/> OPEN FOLDER</button>
            <button className="btn btn--primary"><Icon name="bolt" size={12}/> FLASH @ 460800</button>
          </div>
        }
      />

      <div className="code-pane">
        <div className="code-files">
          <div className="kicker" style={{ padding: "8px 14px" }}>Project files</div>
          {P.firmware.files.map((f, i) => (
            <div key={i} className={`code-file ${active === i ? "active" : ""}`} onClick={() => setActive(i)}>
              <span>{f.name}</span>
              <span className="code-file__path">{f.path}</span>
            </div>
          ))}
          <div className="kicker" style={{ padding: "16px 14px 8px" }}>Libraries</div>
          {P.firmware.libs.map(([n, v], i) => (
            <div key={i} className="code-file" style={{ paddingTop: 4, paddingBottom: 4 }}>
              <span style={{ fontSize: 11 }}>{n}</span>
              <span className="code-file__path">{v}</span>
            </div>
          ))}
        </div>

        <div className="code-view">
          {FILE_CODE[active].map((line, i) => (
            <div key={i} className="code-line">
              <span className="ln">{i + 1}</span>
              <span className="ct">{renderLine(line, i)}</span>
            </div>
          ))}
        </div>
      </div>

      {/* generated-from-netlist callout */}
      <div style={{
        border: "1px solid var(--hairline-2)",
        background: "color-mix(in oklab, var(--accent) 6%, transparent)",
        padding: "16px 20px",
        display: "grid", gridTemplateColumns: "auto 1fr auto", gap: 18, alignItems: "center",
      }}>
        <Icon name="bolt" size={20}/>
        <div>
          <div className="serif" style={{ fontSize: 20, lineHeight: 1.2 }}>pinmap.h is a <em style={{ color: "var(--accent)" }}>derived artifact</em></div>
          <div style={{ color: "var(--ink-mute)", fontSize: 12, marginTop: 4 }}>
            It re-generates on every wiring change. Edit the netlist, not the header.
          </div>
        </div>
        <button className="btn btn--ghost">VIEW NETLIST</button>
      </div>
    </div>
  );
};

/* ------------------------------------------------------------
   VALIDATION
   ------------------------------------------------------------ */
const ValidationTab = () => {
  const P = window.PROJECT;
  const summary = P.findings.reduce((a, f) => { a[f.sev] = (a[f.sev] || 0) + 1; return a; }, {});
  return (
    <div className="page">
      <PageHead
        kicker="DETERMINISTIC RULES ENGINE · 27 CHECKS · 0.4 s"
        h1="Sanity"
        h1em="check."
        sub="Power budget, voltage/logic level mismatches, pin conflicts, I²C collisions — all evaluated deterministically against the assembled Project. AI never decides validation verdicts."
        right={
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn btn--ghost"><Icon name="refresh" size={12}/> RE-RUN</button>
            <button className="btn"><Icon name="download" size={12}/> REPORT.PDF</button>
          </div>
        }
      />

      {/* summary */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", border: "1px solid var(--hairline-2)" }}>
        {[
          ["Status",    "WARN",       "warn"],
          ["Failures",  summary.fail || 0, "fail"],
          ["Warnings",  summary.warn || 0, "warn"],
          ["Passing",   (summary.pass || 0) + " / 27", "ok"],
        ].map(([l, v, c], i) => (
          <div key={l} style={{
            padding: 22, borderRight: i < 3 ? "1px solid var(--hairline)" : "none",
            background: i === 0 ? `color-mix(in oklab, var(--${c}) 10%, transparent)` : "transparent",
          }}>
            <div className="kicker">{l}</div>
            <div className="serif" style={{ fontSize: 42, lineHeight: 1, marginTop: 6, color: `var(--${c})` }}>{v}</div>
          </div>
        ))}
      </div>

      {/* findings list */}
      <div>
        {P.findings.map((f, i) => (
          <div key={i} className="finding">
            <div className={`finding__severity ${f.sev}`}>
              <span>{f.sev === "warn" ? "WARN" : f.sev === "fail" ? "FAIL" : f.sev === "info" ? "INFO" : "PASS"}</span>
              <span className="num">{f.num}</span>
              <span style={{ color: "var(--ink-faint)", fontSize: 9 }}>{f.code}</span>
            </div>
            <div className="finding__body">
              <h3 className="finding__title">{f.title}</h3>
              <p className="finding__desc">{f.desc}</p>
              {f.refs.length > 0 && (
                <div className="finding__refs">
                  {f.refs.map((r, j) => <span key={j} className="tag">{r}</span>)}
                </div>
              )}
            </div>
            <div className="finding__fix">
              {f.fix ? (
                <>
                  <div className="kicker" style={{ color: "var(--accent)" }}>Suggested fix</div>
                  <div style={{ fontFamily: "var(--serif)", fontSize: 18, lineHeight: 1.1 }}>{f.fix}</div>
                  <button className="btn btn--primary" style={{ marginTop: 8, padding: "6px 10px", fontSize: 10 }}>APPLY & RE-RUN</button>
                </>
              ) : (
                <div style={{ color: "var(--ok)", fontFamily: "var(--mono)", fontSize: 11, letterSpacing: "0.12em", textTransform: "uppercase" }}>
                  ✓ no action needed
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      {/* power budget chart */}
      <div className="card">
        <div className="card__head">
          <span className="card__title">Power budget · estimated battery life</span>
          <span className="kicker">3000 mAh · single 18650</span>
        </div>
        {(() => {
          const W = 900, H = 110;
          const total = 84;
          const slices = [
            { l: "Wi-Fi TX",    ma: 48, c: "var(--accent)" },
            { l: "MCU active",  ma: 18, c: "var(--info)" },
            { l: "Sensor read", ma: 12, c: "var(--ok)" },
            { l: "ADC + boost", ma: 4,  c: "var(--warn)" },
            { l: "Quiescent",   ma: 2,  c: "var(--ink-mute)" },
          ];
          let acc = 0;
          return (
            <>
              <svg viewBox={`0 0 ${W} ${H}`} style={{ width: "100%", height: "auto" }}>
                <rect x="0" y="36" width={W} height="36" fill="#0c0c10" stroke="var(--hairline-2)"/>
                {slices.map((s, i) => {
                  const x = (acc / total) * W;
                  const w = (s.ma / total) * W;
                  acc += s.ma;
                  return (
                    <g key={i}>
                      <rect x={x} y="36" width={w} height="36" fill={s.c} opacity="0.85"/>
                      <text x={x + 10} y="58" fontFamily="var(--mono)" fontSize="10" fill="#06060a" fontWeight="600" letterSpacing="0.6">
                        {s.l.toUpperCase()}
                      </text>
                      <text x={x + 10} y="92" fontFamily="var(--mono)" fontSize="10" fill={s.c} letterSpacing="0.6">
                        {s.ma} mA · {Math.round((s.ma / total) * 100)}%
                      </text>
                    </g>
                  );
                })}
                <text x="0" y="24" fontFamily="var(--mono)" fontSize="10" fill="var(--ink-mute)" letterSpacing="0.16em">PEAK · {total} mA @ active</text>
                <text x={W} y="24" fontFamily="var(--mono)" fontSize="10" fill="var(--ink-mute)" letterSpacing="0.16em" textAnchor="end">SLEEP · 12 µA</text>
              </svg>
            </>
          );
        })()}
      </div>
    </div>
  );
};

/* ------------------------------------------------------------
   GUIDE
   ------------------------------------------------------------ */
const GuideTab = () => {
  const P = window.PROJECT;
  return (
    <div className="page">
      <PageHead
        kicker={`ASSEMBLY GUIDE · ${P.assembly.length} STEPS · ESTIMATED 45 MIN`}
        h1="How to"
        h1em="build."
        sub="Each step references the components and nets it touches. Export to Markdown or PDF for the workshop bench."
        right={
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn btn--ghost">MARKDOWN</button>
            <button className="btn btn--primary"><Icon name="download" size={12}/> PDF · 6 pages</button>
          </div>
        }
      />

      {/* big disclaimer */}
      <div style={{
        border: "1px solid color-mix(in oklab, var(--warn) 40%, transparent)",
        background: "color-mix(in oklab, var(--warn) 6%, transparent)",
        padding: "14px 18px",
        display: "grid", gridTemplateColumns: "auto 1fr", gap: 16, alignItems: "center",
      }}>
        <Icon name="shield" size={20}/>
        <div style={{ color: "var(--ink)", fontSize: 12.5, lineHeight: 1.5 }}>
          <b style={{ color: "var(--warn)", letterSpacing: "0.16em", textTransform: "uppercase", fontSize: 10, marginRight: 8 }}>DESIGN AID</b>
          Foundry's outputs are a starting point — not a manufacturable spec. Verify polarity, voltage, and your power supply before applying power. Always wear safety glasses when soldering.
        </div>
      </div>

      <div>
        {P.assembly.map((s) => (
          <div key={s.n} className="guide-step">
            <div className="guide-step__num">
              <em>{String(s.n).padStart(2, "0")}</em>
            </div>
            <div>
              <h3 className="guide-step__title">{s.title}</h3>
              <p className="guide-step__body">{s.body}</p>
              <div className="guide-step__chips">
                {s.chips.map((c, i) => <span key={i} className="tag">{c}</span>)}
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

Object.assign(window, { OverviewTab, BomTab, WiringTab, EnclosureTab, FirmwareTab, ValidationTab, GuideTab });
