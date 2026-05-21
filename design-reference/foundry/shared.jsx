/* ============================================================
   FOUNDRY — shared primitives, icons, window chrome
   ============================================================ */

// ---------- Icons (sharp, 1.5px stroke) ----------
const Icon = ({ name, size = 14 }) => {
  const s = size;
  const paths = {
    spark:   <><path d="M8 1 L9.5 6.5 L15 8 L9.5 9.5 L8 15 L6.5 9.5 L1 8 L6.5 6.5 Z"/></>,
    chip:    <><rect x="3" y="3" width="10" height="10"/><path d="M3 6 H1 M3 10 H1 M15 6 H13 M15 10 H13 M6 3 V1 M10 3 V1 M6 15 V13 M10 15 V13"/><rect x="6" y="6" width="4" height="4"/></>,
    cart:    <><path d="M1 2 H3 L5 11 H13 L15 5 H4"/><circle cx="6" cy="14" r="1"/><circle cx="12" cy="14" r="1"/></>,
    wire:    <><circle cx="3" cy="3" r="1.5"/><circle cx="13" cy="13" r="1.5"/><path d="M3 4.5 V8 H8 V13"/></>,
    cube:    <><path d="M8 1 L14 4.5 V11.5 L8 15 L2 11.5 V4.5 Z"/><path d="M2 4.5 L8 8 L14 4.5 M8 8 V15"/></>,
    code:    <><path d="M5 4 L1 8 L5 12 M11 4 L15 8 L11 12 M9 3 L7 13"/></>,
    shield:  <><path d="M8 1 L14 3 V8 C14 11 11 14 8 15 C5 14 2 11 2 8 V3 Z"/><path d="M5.5 8 L7.5 10 L11 6.5"/></>,
    book:    <><path d="M2 2 H7 C8 2 8 3 8 3 V14 C8 14 8 13 7 13 H2 Z M14 2 H9 C8 2 8 3 8 3 V14 C8 14 8 13 9 13 H14 Z"/></>,
    bolt:    <><path d="M9 1 L3 9 H8 L7 15 L13 7 H8 Z"/></>,
    play:    <><path d="M4 2 L13 8 L4 14 Z"/></>,
    plus:    <><path d="M8 2 V14 M2 8 H14"/></>,
    search:  <><circle cx="7" cy="7" r="5"/><path d="M11 11 L15 15"/></>,
    minimize:<><path d="M3 8 H13"/></>,
    maximize:<><rect x="3" y="3" width="10" height="10"/></>,
    close:   <><path d="M3 3 L13 13 M13 3 L3 13"/></>,
    send:    <><path d="M1 8 L15 1 L11 15 L8 9 Z"/></>,
    download:<><path d="M8 1 V11 M3 7 L8 12 L13 7 M2 14 H14"/></>,
    refresh: <><path d="M14 4 V8 H10 M2 12 V8 H6"/><path d="M3 6 A6 6 0 0 1 13 6 M13 10 A6 6 0 0 1 3 10"/></>,
    settings:<><circle cx="8" cy="8" r="2"/><path d="M8 1 V3 M8 13 V15 M1 8 H3 M13 8 H15 M3 3 L4.5 4.5 M11.5 11.5 L13 13 M3 13 L4.5 11.5 M11.5 4.5 L13 3"/></>,
    grid:    <><rect x="2" y="2" width="5" height="5"/><rect x="9" y="2" width="5" height="5"/><rect x="2" y="9" width="5" height="5"/><rect x="9" y="9" width="5" height="5"/></>,
    chev:    <><path d="M5 3 L11 8 L5 13"/></>,
    chevD:   <><path d="M3 5 L8 11 L13 5"/></>,
    folder:  <><path d="M1 4 H6 L8 6 H15 V13 H1 Z"/></>,
    eye:     <><path d="M1 8 C3 4 5 3 8 3 C11 3 13 4 15 8 C13 12 11 13 8 13 C5 13 3 12 1 8 Z"/><circle cx="8" cy="8" r="2"/></>,
    cpu:     <><rect x="4" y="4" width="8" height="8"/><path d="M2 6 H4 M2 10 H4 M12 6 H14 M12 10 H14 M6 2 V4 M10 2 V4 M6 12 V14 M10 12 V14"/></>,
  };
  return (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="square" strokeLinejoin="miter">
      {paths[name] || paths.spark}
    </svg>
  );
};

// ---------- Window chrome ----------
const WindowChrome = ({ title, crumbs, children }) => (
  <div className="win-wrap">
    <div className="win">
      <header className="titlebar">
        <div className="titlebar__brand">
          <span className="titlebar__brand-mark"></span>
          <span>Foundry</span>
        </div>
        <div className="titlebar__crumb">
          {crumbs.map((c, i) => (
            <React.Fragment key={i}>
              {i > 0 && <span className="sep">/</span>}
              <span style={i === crumbs.length - 1 ? { color: "var(--ink)" } : {}}>{c}</span>
            </React.Fragment>
          ))}
        </div>
        <div className="titlebar__btns">
          <button className="titlebar__btn"><Icon name="minimize" size={12}/></button>
          <button className="titlebar__btn"><Icon name="maximize" size={11}/></button>
          <button className="titlebar__btn close"><Icon name="close" size={11}/></button>
        </div>
      </header>
      {children}
      <footer className="statusbar">
        <div className="statusbar__left">
          <span className="statusbar__chip"><span className="statusbar__dot live"></span>Claude · Sonnet 4.5</span>
          <span className="statusbar__chip"><span className="statusbar__dot"></span>Sidecar build123d · 127.0.0.1:8731</span>
          <span className="statusbar__chip"><span className="statusbar__dot"></span>Nexar OK</span>
          <span className="statusbar__chip"><span className="statusbar__dot warn"></span>DigiKey rate-limit 40%</span>
        </div>
        <div className="statusbar__right">
          <span>Design aid · verify before you build</span>
          <span>v0.4.1</span>
        </div>
      </footer>
    </div>
  </div>
);

// ---------- Pipeline pill (for chat) ----------
const PipelinePill = ({ pipeline }) => (
  <div className="msg__pipeline">
    {pipeline.map((p, i) => (
      <div key={i} className={p.state}>
        <span className="pmark">{p.state === "done" ? "✓" : p.state === "live" ? "◆" : "·"}</span>
        <span className="pname">{p.stage}</span>
        <span className="pstate">{p.state === "live" ? "running" : p.state === "done" ? "done" : "—"}</span>
      </div>
    ))}
  </div>
);

// ---------- Page header ----------
const PageHead = ({ kicker, h1, h1em, sub, right }) => (
  <header className="page__head">
    <div className="page__head-text">
      <div className="kicker">{kicker}</div>
      <h1 className="page__h1">
        {h1} {h1em && <em>{h1em}</em>}
      </h1>
      {sub && <p className="page__sub">{sub}</p>}
    </div>
    {right && <div className="page__head-actions">{right}</div>}
  </header>
);

// ---------- Net swatch ----------
const NET_COLORS = {
  power:  "var(--power)",
  ground: "var(--ground)",
  signal: "var(--signal)",
  i2c:    "var(--signal-2)",
};

Object.assign(window, { Icon, WindowChrome, PipelinePill, PageHead, NET_COLORS });
