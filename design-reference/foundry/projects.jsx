/* ============================================================
   FOUNDRY — projects list screen
   ============================================================ */

// Tiny inline diagram preview for each card — varies per id
const MiniDiagram = ({ id, status }) => {
  const seed = parseInt(id.replace(/\D/g, ""), 10) || 0;
  const rand = (n) => (Math.sin(seed * (n + 1)) * 10000) % 1;
  const color = status === "fail" ? "var(--fail)" : status === "warn" ? "var(--warn)" : "var(--accent)";
  const nodes = Array.from({ length: 5 }, (_, i) => ({
    x: 20 + ((rand(i * 2) + 1) * 0.5) * 280,
    y: 14 + ((rand(i * 2 + 1) + 1) * 0.5) * 50,
  }));
  return (
    <svg viewBox="0 0 320 80" className="proj-card__diagram">
      <defs>
        <pattern id={`grid-${id}`} width="16" height="16" patternUnits="userSpaceOnUse">
          <path d="M 16 0 L 0 0 0 16" fill="none" stroke="rgba(255,255,255,0.04)" strokeWidth="1"/>
        </pattern>
      </defs>
      <rect width="320" height="80" fill={`url(#grid-${id})`}/>
      {nodes.map((n, i) => i > 0 && (
        <line key={`l-${i}`} x1={nodes[i-1].x} y1={nodes[i-1].y} x2={n.x} y2={n.y}
          stroke={i === 1 ? color : "var(--ink-faint)"} strokeWidth="1.2"/>
      ))}
      {nodes.map((n, i) => (
        <rect key={`n-${i}`} x={n.x - 4} y={n.y - 4} width="8" height="8"
          fill={i === 0 ? color : "var(--surface-2)"} stroke={i === 0 ? color : "var(--hairline-3)"} strokeWidth="1"/>
      ))}
    </svg>
  );
};

const ProjectsList = ({ onOpen, onNew }) => {
  const [query, setQuery] = React.useState("");
  const projects = window.RECENT_PROJECTS;

  return (
    <div className="fullpage" style={{ background: "var(--bg)" }}>
      {/* hero */}
      <div style={{ padding: "44px 44px 28px", borderBottom: "1px solid var(--hairline)" }}>
        <div className="kicker">Library · 24 projects · 1.4 GB</div>
        <h1 className="serif" style={{ fontSize: 96, margin: "12px 0 0", lineHeight: 1.1, letterSpacing: "-0.025em", paddingBottom: "0.18em" }}>
          Your <em style={{ color: "var(--accent)", fontStyle: "italic" }}>foundry</em>.
        </h1>
      </div>

      {/* toolbar */}
      <div style={{
        display: "grid", gridTemplateColumns: "1fr auto auto auto",
        alignItems: "center", borderBottom: "1px solid var(--hairline)",
        background: "var(--surface-0)",
      }}>
        <div style={{ display: "flex", alignItems: "center", gap: 10, padding: "14px 22px", borderRight: "1px solid var(--hairline)" }}>
          <Icon name="search" size={14}/>
          <input
            placeholder="Search projects, parts, prompts…"
            value={query} onChange={e => setQuery(e.target.value)}
            style={{
              background: "transparent", border: "none", outline: "none", flex: 1,
              fontFamily: "var(--mono)", fontSize: 12, color: "var(--ink)",
            }}/>
          <span style={{ fontFamily: "var(--mono)", fontSize: 10, color: "var(--ink-faint)", letterSpacing: "0.14em" }}>⌘ K</span>
        </div>
        <button className="main__action">All <Icon name="chevD" size={10}/></button>
        <button className="main__action">Updated <Icon name="chevD" size={10}/></button>
        <button className="main__action main__action--primary" onClick={onNew}>
          <Icon name="plus" size={12}/> NEW
        </button>
      </div>

      {/* recent project — large */}
      <div style={{ padding: "32px 44px 0" }}>
        <div className="kicker">Continue</div>
        <div style={{
          marginTop: 14,
          display: "grid", gridTemplateColumns: "1.5fr 1fr",
          border: "1px solid var(--hairline-2)",
          background: "linear-gradient(135deg, color-mix(in oklab, var(--accent) 8%, transparent), transparent 60%)",
          cursor: "pointer",
        }} onClick={() => onOpen(projects[0].id)}>
          <div style={{ padding: "28px 32px", borderRight: "1px solid var(--hairline)" }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <div className="kicker" style={{ color: "var(--accent)" }}>{projects[0].id} · Active</div>
              <span className="kicker">{projects[0].updated}</span>
            </div>
            <h2 className="serif" style={{ fontSize: 46, margin: "10px 0 0", lineHeight: 1.18, paddingBottom: 10 }}>
              {projects[0].title}
            </h2>
            <div style={{ color: "var(--ink-soft)", fontSize: 13, marginTop: 12, maxWidth: "60ch" }}>
              {projects[0].prompt}
            </div>
            <div style={{ display: "flex", gap: 8, marginTop: 20 }}>
              <span className="tag tag--warn">2 warnings</span>
              <span className="tag">8 parts</span>
              <span className="tag">$38.42</span>
              <span className="tag tag--accent">Firmware ready</span>
            </div>
          </div>
          <div style={{ padding: 24, display: "grid", placeItems: "stretch" }}>
            <MiniDiagram id={projects[0].id} status={projects[0].status}/>
          </div>
        </div>
      </div>

      {/* recent grid */}
      <div style={{ padding: "32px 44px 60px" }}>
        <div className="kicker">All projects</div>
        <div className="proj-grid" style={{ marginTop: 14 }}>
          {projects.slice(1).map((p) => (
            <div key={p.id} className="proj-card" onClick={() => onOpen(p.id)}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
                <span className="kicker">{p.id}</span>
                <span className={`tag tag--${p.status === "ok" ? "ok" : p.status === "warn" ? "warn" : "fail"}`} style={{ fontSize: 9 }}>
                  {p.status === "ok" ? "passing" : p.status === "warn" ? "warn" : "fail"}
                </span>
              </div>
              <div className="proj-card__name">{p.title}</div>
              <div className="proj-card__desc">{p.prompt}</div>
              <MiniDiagram id={p.id} status={p.status}/>
              <div className="proj-card__footer">
                <span>{p.parts} parts · ${p.cost.toFixed(2)}</span>
                <span>{p.updated}</span>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
};

window.ProjectsList = ProjectsList;
