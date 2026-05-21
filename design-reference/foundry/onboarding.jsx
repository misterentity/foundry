/* ============================================================
   FOUNDRY — onboarding (first run)
   ============================================================ */

const Onboarding = ({ onContinue }) => {
  const [keys, setKeys] = React.useState({
    anthropic: "",
    nexar: "",
    digikey: "",
    mouser: "",
  });
  const [tab, setTab] = React.useState("anthropic"); // anthropic | sourcing

  return (
    <div className="fullpage onboard" style={{ gridTemplateColumns: "1.15fr 1fr" }}>
      {/* LEFT — display */}
      <div className="onboard__left">
        <div>
          <div className="kicker">FOUNDRY · 0.4.1 · Windows 11 · Local-first</div>
          <h1 className="onboard__display" style={{ marginTop: 36 }}>
            Prompt.<br/>
            <em>Wire.</em><br/>
            Print.<br/>
            <span className="strike">— one prompt → a buildable device.</span>
          </h1>
        </div>

        <div style={{ display: "grid", gap: 16, maxWidth: 460 }}>
          <div className="kicker" style={{ color: "var(--accent)" }}>Pipeline ▸</div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(7, 1fr)", border: "1px solid var(--hairline-2)" }}>
            {["Spec","Arch","BOM","Wiring","Firmware","CAD","Validate"].map((s, i) => (
              <div key={s} style={{
                padding: "10px 8px",
                borderRight: i < 6 ? "1px solid var(--hairline)" : "none",
                fontFamily: "var(--mono)", fontSize: 9.5, letterSpacing: "0.16em",
                textTransform: "uppercase",
                color: i === 0 ? "var(--accent)" : "var(--ink-soft)",
                textAlign: "center",
                background: i === 0 ? "color-mix(in oklab, var(--accent) 12%, transparent)" : "transparent",
              }}>
                {String(i+1).padStart(2,"0")}<br/>{s}
              </div>
            ))}
          </div>
          <div style={{ fontSize: 11.5, color: "var(--ink-mute)", lineHeight: 1.6 }}>
            Architecture, BOM, wiring diagram, starter firmware, a printable enclosure, and rule-based electrical validation — re-rerunnable per stage as you iterate by chat.
          </div>
        </div>
      </div>

      {/* RIGHT — form */}
      <div className="onboard__right">
        <div className="kicker">Setup · Step 1 of 2</div>
        <h2 className="serif" style={{ fontSize: 38, margin: 0, lineHeight: 1 }}>API keys</h2>
        <p style={{ color: "var(--ink-mute)", fontSize: 12, margin: 0, maxWidth: "48ch" }}>
          Keys are stored in Windows Credential Manager (DPAPI-backed). They never touch Project files or logs. The app falls back to "offline" states when keys are missing.
        </p>

        {/* tabs */}
        <div style={{ display: "flex", gap: 0, marginTop: 8, borderBottom: "1px solid var(--hairline)" }}>
          {[
            ["anthropic", "Anthropic", "required"],
            ["sourcing",  "Sourcing",  "optional"],
          ].map(([id, label, hint]) => (
            <button key={id} onClick={() => setTab(id)}
              style={{
                padding: "12px 18px",
                fontFamily: "var(--mono)", fontSize: 11, letterSpacing: "0.16em", textTransform: "uppercase",
                color: tab === id ? "var(--ink)" : "var(--ink-mute)",
                borderBottom: tab === id ? "2px solid var(--accent)" : "2px solid transparent",
                marginBottom: -1,
              }}>
              {label} <span style={{ color: "var(--ink-faint)", marginLeft: 8 }}>· {hint}</span>
            </button>
          ))}
        </div>

        {tab === "anthropic" ? (
          <>
            <div className="field">
              <label className="field__label">
                <span>Anthropic API key</span>
                <span className="req">required</span>
              </label>
              <input className="field__input" placeholder="sk-ant-api03-…"
                value={keys.anthropic} onChange={e => setKeys({...keys, anthropic: e.target.value})}/>
              <div className="field__hint">Used for the staged generation pipeline. Default model: claude-sonnet-4-5 · max 8192 output tokens.</div>
            </div>

            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
              <div className="card" style={{ padding: 14 }}>
                <div className="kicker">Default model</div>
                <div className="serif" style={{ fontSize: 22, marginTop: 4 }}>Sonnet 4.5</div>
                <div style={{ fontSize: 11, color: "var(--ink-mute)", marginTop: 4 }}>Balanced. ~$0.04 / project.</div>
              </div>
              <div className="card" style={{ padding: 14 }}>
                <div className="kicker">Hard mode</div>
                <div className="serif" style={{ fontSize: 22, marginTop: 4 }}>Opus 4</div>
                <div style={{ fontSize: 11, color: "var(--ink-mute)", marginTop: 4 }}>For complex designs. ~$0.40.</div>
              </div>
            </div>
          </>
        ) : (
          <>
            <div className="field">
              <label className="field__label">
                <span>Nexar / Octopart</span>
                <span className="opt">optional · aggregated stock + pricing</span>
              </label>
              <input className="field__input" placeholder="oauth client_id:client_secret"
                value={keys.nexar} onChange={e => setKeys({...keys, nexar: e.target.value})}/>
            </div>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
              <div className="field">
                <label className="field__label"><span>DigiKey</span><span className="opt">opt</span></label>
                <input className="field__input" placeholder="client_id"
                  value={keys.digikey} onChange={e => setKeys({...keys, digikey: e.target.value})}/>
              </div>
              <div className="field">
                <label className="field__label"><span>Mouser</span><span className="opt">opt</span></label>
                <input className="field__input" placeholder="search api key"
                  value={keys.mouser} onChange={e => setKeys({...keys, mouser: e.target.value})}/>
              </div>
            </div>
          </>
        )}

        <div style={{ marginTop: "auto", display: "flex", justifyContent: "space-between", alignItems: "center", gap: 16, paddingTop: 24, borderTop: "1px solid var(--hairline)" }}>
          <button className="btn btn--ghost" onClick={onContinue}>Skip · explore demo</button>
          <div style={{ display: "flex", gap: 8 }}>
            <button className="btn">Test connection</button>
            <button className="btn btn--primary" onClick={onContinue}>
              Continue <Icon name="chev" size={12}/>
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};

window.Onboarding = Onboarding;
