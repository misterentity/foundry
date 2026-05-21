/* ============================================================
   FOUNDRY — workspace shell: rail + main + chat
   ============================================================ */

const TABS = [
  { id: "overview",  label: "Overview",       icon: "spark",   group: "design", badge: null },
  { id: "bom",       label: "BOM",            icon: "cart",    group: "design", badge: null },
  { id: "wiring",    label: "Wiring",         icon: "wire",    group: "design", badge: null },
  { id: "enclosure", label: "Enclosure",      icon: "cube",    group: "design", badge: null },
  { id: "firmware",  label: "Firmware",       icon: "code",    group: "design", badge: null },
  { id: "validation",label: "Validation",     icon: "shield",  group: "design", badge: { text: "2W", kind: "warn" } },
  { id: "guide",     label: "Assembly guide", icon: "book",    group: "design", badge: null },
];

const TAB_COMPONENTS = {
  overview:   () => <OverviewTab/>,
  bom:        () => <BomTab/>,
  wiring:     () => <WiringTab/>,
  enclosure:  () => <EnclosureTab/>,
  firmware:   () => <FirmwareTab/>,
  validation: () => <ValidationTab/>,
  guide:      () => <GuideTab/>,
};

const Workspace = ({ tab, onTab, onProjectsView }) => {
  const P = window.PROJECT;
  const [input, setInput] = React.useState("");
  const TabBody = TAB_COMPONENTS[tab] || TAB_COMPONENTS.overview;
  const activeTab = TABS.find(t => t.id === tab) || TABS[0];

  return (
    <div className="shell">
      {/* ===== LEFT RAIL ===== */}
      <aside className="rail">
        <div className="rail__header">
          <button onClick={onProjectsView}
            style={{
              fontFamily: "var(--mono)", fontSize: 10, color: "var(--ink-mute)",
              letterSpacing: "0.18em", textTransform: "uppercase",
              display: "flex", alignItems: "center", gap: 6,
            }}>
            <Icon name="chev" size={10} /> ALL PROJECTS
          </button>
          <div className="rail__project-name">{P.title}</div>
          <div className="rail__meta">
            <span>{P.id}</span>
            <span style={{ color: "var(--accent)" }}>● {P.status}</span>
          </div>
        </div>

        <nav className="rail__nav">
          <div className="rail__group-label">Design</div>
          {TABS.map((t, i) => (
            <button key={t.id}
              className={`rail__item ${tab === t.id ? "active" : ""}`}
              onClick={() => onTab(t.id)}>
              <span className="num">{String(i + 1).padStart(2, "0")}</span>
              <span style={{ display: "inline-flex", alignItems: "center", gap: 10 }}>
                <Icon name={t.icon} size={13}/> {t.label}
              </span>
              {t.badge && <span className={`rail__badge ${t.badge.kind}`}>{t.badge.text}</span>}
            </button>
          ))}

          <div className="rail__group-label">Stages</div>
          {[
            ["Spec",         "done"],
            ["Architecture", "done"],
            ["Wiring",       "done"],
            ["Firmware",     "done"],
            ["Enclosure",    "live"],
            ["Validation",   "live"],
          ].map(([s, st], i) => (
            <div key={s} style={{
              padding: "5px 14px 5px 14px",
              fontFamily: "var(--mono)", fontSize: 11,
              color: st === "live" ? "var(--accent)" : "var(--ink-soft)",
              display: "grid", gridTemplateColumns: "22px 1fr auto", alignItems: "center", gap: 8,
            }}>
              <span style={{ fontSize: 10, color: "var(--ink-faint)" }}>{i + 1}</span>
              <span>{s}</span>
              <span style={{ fontSize: 9, letterSpacing: "0.14em", textTransform: "uppercase", color: st === "live" ? "var(--accent)" : "var(--ok)" }}>
                {st === "live" ? "·· running" : "✓"}
              </span>
            </div>
          ))}
        </nav>

        <div className="rail__footer">
          <span>Saved · auto</span>
          <span>{P.updated.split(" ")[1]}</span>
        </div>
      </aside>

      {/* ===== MAIN ===== */}
      <main className="main">
        <div className="main__tabbar">
          <div className="main__crumb">
            <span>{P.title}</span>
            <span style={{ color: "var(--hairline-3)" }}>/</span>
            <b>{activeTab.label}</b>
          </div>
          <div className="main__actions">
            <button className="main__action"><Icon name="eye" size={11}/> Preview</button>
            <button className="main__action"><Icon name="download" size={11}/> Export</button>
            <button className="main__action"><Icon name="settings" size={11}/></button>
          </div>
        </div>
        <div className="main__body">
          <TabBody/>
        </div>
      </main>

      {/* ===== CHAT ===== */}
      <aside className="chat">
        <div className="chat__head">
          <h3>Iteration · chat</h3>
          <span className="kicker">turn 3</span>
        </div>
        <div className="chat__list">
          {window.CHAT_HISTORY.map((m, i) => (
            <div key={i} className={`msg ${m.role === "user" ? "msg--user" : "msg--asst"}`}>
              <div className="msg__role">
                <span>{m.role === "user" ? "DAVE" : "FOUNDRY"}</span>
                <span style={{ color: "var(--ink-faint)", letterSpacing: "0.1em" }}>· {m.time}</span>
              </div>
              <div className="msg__body" style={m.role === "user" ? { fontFamily: "var(--serif)", fontSize: 17, lineHeight: 1.35, color: "var(--ink)" } : {}}>
                {m.text}
              </div>
              {m.pipeline && <PipelinePill pipeline={m.pipeline}/>}
            </div>
          ))}
        </div>
        <div className="chat__composer">
          <textarea
            className="chat__textarea"
            placeholder="Iterate by chat — 'make it solar powered', 'shrink the enclosure 10%', 'swap to STM32'…"
            value={input} onChange={e => setInput(e.target.value)}
          />
          <div className="chat__composer-row">
            <span>↩ to send · ⇧↩ newline</span>
            <button className="btn btn--primary" style={{ padding: "6px 12px", fontSize: 10 }}>
              <Icon name="send" size={11}/> SEND
            </button>
          </div>
        </div>
      </aside>
    </div>
  );
};

window.Workspace = Workspace;
