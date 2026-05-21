/* ============================================================
   FOUNDRY — enclosure "3D" SVG preview
   Isometric box with cutouts, standoffs, cable gland.
   Faked 3D but technical & convincing.
   ============================================================ */

const ENCLOSURE_SVG = ({ rotation = 0 }) => {
  const W = 720, H = 460;
  // iso projection helpers
  const CX = 360, CY = 230;
  const ax = 1, ay = 0.55;   // x axis tilt
  const bx = -1, by = 0.55;  // y axis tilt
  const cz = -1;             // z (up) axis
  const SCALE = 4;
  // box dims (mm) — 62 × 48 × 26 inner + 2mm wall = 66 × 52 × 30
  const L = 66, Wd = 52, Hd = 30;
  const p = (x, y, z) => [
    CX + (x * ax + y * bx) * SCALE,
    CY + (x * ay + y * by + z * cz) * SCALE,
  ];
  const pl = (...pts) => pts.map(([x, y]) => `${x},${y}`).join(" ");

  // verts — origin at front-bottom corner; box from (0,0,0) to (L,Wd,Hd)
  const v = {
    A: p(0, 0, 0), B: p(L, 0, 0),  C: p(L, Wd, 0), D: p(0, Wd, 0),
    E: p(0, 0, Hd), F: p(L, 0, Hd), G: p(L, Wd, Hd), H: p(0, Wd, Hd),
  };

  // ----- faces -----
  // front face: A-B-F-E
  // top:        E-F-G-H
  // right side: B-C-G-F

  // cutout helpers — project rectangle on front face (centered at u,v in mm)
  const frontPt = (u, hv) => p(u, 0, hv);

  return (
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: "100%", height: "100%", display: "block" }}>
      <defs>
        <linearGradient id="face-top" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#3a3a46"/>
          <stop offset="100%" stopColor="#1d1d24"/>
        </linearGradient>
        <linearGradient id="face-front" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#26262e"/>
          <stop offset="100%" stopColor="#0e0e12"/>
        </linearGradient>
        <linearGradient id="face-side" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#1d1d24"/>
          <stop offset="100%" stopColor="#06060a"/>
        </linearGradient>
        <pattern id="iso-grid" width="32" height="18" patternUnits="userSpaceOnUse">
          <path d="M 0 0 L 32 0 M 0 0 L 0 18" stroke="rgba(255,255,255,0.04)" strokeWidth="1"/>
        </pattern>
      </defs>

      {/* viewport grid */}
      <rect width={W} height={H} fill="url(#iso-grid)"/>

      {/* ===== TICK + DIMENSION lines ===== */}
      {/* length L */}
      <g stroke="#5dd2ff" strokeWidth="0.8" fill="none" opacity="0.9">
        <line x1={v.D[0]} y1={v.D[1] + 30} x2={v.C[0]} y2={v.C[1] + 30}/>
        <line x1={v.D[0]} y1={v.D[1] + 24} x2={v.D[0]} y2={v.D[1] + 36}/>
        <line x1={v.C[0]} y1={v.C[1] + 24} x2={v.C[0]} y2={v.C[1] + 36}/>
        <text x={(v.D[0] + v.C[0]) / 2} y={v.D[1] + 50}
          fill="#5dd2ff" fontFamily="var(--mono)" fontSize="11" textAnchor="middle" letterSpacing="0.6">
          L · 66.00 mm
        </text>
      </g>

      {/* shadow on floor */}
      <ellipse cx={CX} cy={CY + 100} rx="180" ry="22" fill="rgba(0,0,0,0.5)"/>

      {/* ===== BOX FACES ===== */}
      {/* right side */}
      <polygon points={pl(v.B, v.C, v.G, v.F)} fill="url(#face-side)" stroke="#3a3a46" strokeWidth="1"/>
      {/* top face */}
      <polygon points={pl(v.E, v.F, v.G, v.H)} fill="url(#face-top)" stroke="#3a3a46" strokeWidth="1"/>
      {/* front face */}
      <polygon points={pl(v.A, v.B, v.F, v.E)} fill="url(#face-front)" stroke="#3a3a46" strokeWidth="1"/>

      {/* lid seam */}
      <line x1={v.E[0]} y1={v.E[1] - 6} x2={v.F[0]} y2={v.F[1] - 6}
        stroke="#ff5a1f" strokeWidth="0.8" strokeDasharray="2 3" opacity="0.8"/>
      <line x1={v.F[0]} y1={v.F[1] - 6} x2={v.G[0]} y2={v.G[1] - 6}
        stroke="#ff5a1f" strokeWidth="0.8" strokeDasharray="2 3" opacity="0.8"/>
      <text x={v.F[0] + 18} y={v.F[1] - 8}
        fontFamily="var(--mono)" fontSize="9" fill="#ff5a1f" letterSpacing="0.6">SNAP LID · −2mm</text>

      {/* ===== CUTOUTS ON FRONT FACE ===== */}
      {/* USB-C rect at u=12, w=9.5×6.5, h=18 */}
      {(() => {
        const u0 = 12, h0 = 18, uw = 9.5, hh = 6.5;
        const c1 = p(u0, 0, h0);
        const c2 = p(u0 + uw, 0, h0);
        const c3 = p(u0 + uw, 0, h0 + hh);
        const c4 = p(u0, 0, h0 + hh);
        return (
          <g>
            <polygon points={pl(c1, c2, c3, c4)} fill="#04040a" stroke="#5dd2ff" strokeWidth="1.2"/>
            <line x1={c2[0] + 8} y1={(c1[1] + c4[1]) / 2}
              x2={c2[0] + 60} y2={(c1[1] + c4[1]) / 2 + 30}
              stroke="#5dd2ff" strokeWidth="0.6"/>
            <rect x={c2[0] + 60} y={(c1[1] + c4[1]) / 2 + 18} width="74" height="28" fill="#0c0c10" stroke="#5dd2ff" strokeWidth="0.8"/>
            <text x={c2[0] + 68} y={(c1[1] + c4[1]) / 2 + 30} fontFamily="var(--mono)" fontSize="9" fill="#5dd2ff" letterSpacing="0.6">USB-C</text>
            <text x={c2[0] + 68} y={(c1[1] + c4[1]) / 2 + 41} fontFamily="var(--mono)" fontSize="8" fill="#b6b6bb">9.5 × 6.5</text>
          </g>
        );
      })()}

      {/* M12 cable gland circle at u=50, hv=13, d=12 */}
      {(() => {
        const center = p(50, 0, 13);
        const ed = p(50 + 6, 0, 13);
        const ru = Math.abs(ed[0] - center[0]);
        return (
          <g>
            <ellipse cx={center[0]} cy={center[1]} rx={ru} ry={ru * 0.6} fill="#04040a" stroke="#ff5a1f" strokeWidth="1.2"/>
            <line x1={center[0]} y1={center[1] + 12} x2={center[0] - 80} y2={center[1] + 80}
              stroke="#ff5a1f" strokeWidth="0.6"/>
            <rect x={center[0] - 168} y={center[1] + 70} width="90" height="28" fill="#0c0c10" stroke="#ff5a1f" strokeWidth="0.8"/>
            <text x={center[0] - 160} y={center[1] + 82} fontFamily="var(--mono)" fontSize="9" fill="#ff5a1f" letterSpacing="0.6">M12 GLAND</text>
            <text x={center[0] - 160} y={center[1] + 93} fontFamily="var(--mono)" fontSize="8" fill="#b6b6bb">⌀ 12.00 · IP65</text>
          </g>
        );
      })()}

      {/* ===== CUTOUT ON TOP FACE (reset button) ===== */}
      {(() => {
        const center = p(40, 10, Hd);
        return (
          <g>
            <ellipse cx={center[0]} cy={center[1]} rx="6" ry="3.5" fill="#04040a" stroke="#fbbf24" strokeWidth="1.2"/>
            <line x1={center[0] + 6} y1={center[1] - 1} x2={center[0] + 70} y2={center[1] - 50}
              stroke="#fbbf24" strokeWidth="0.6"/>
            <rect x={center[0] + 70} y={center[1] - 70} width="74" height="28" fill="#0c0c10" stroke="#fbbf24" strokeWidth="0.8"/>
            <text x={center[0] + 78} y={center[1] - 58} fontFamily="var(--mono)" fontSize="9" fill="#fbbf24" letterSpacing="0.6">RESET</text>
            <text x={center[0] + 78} y={center[1] - 47} fontFamily="var(--mono)" fontSize="8" fill="#b6b6bb">⌀ 6.00</text>
          </g>
        );
      })()}

      {/* ===== STANDOFFS visible through top ===== */}
      {[[8, 8], [L - 8, 8], [8, Wd - 8], [L - 8, Wd - 8]].map(([sx, sy], i) => {
        const c = p(sx, sy, Hd);
        return (
          <g key={i}>
            <ellipse cx={c[0]} cy={c[1]} rx="4" ry="2.4" fill="#2a2a36" stroke="#3a3a46" strokeWidth="0.8"/>
            <ellipse cx={c[0]} cy={c[1]} rx="1.4" ry="0.9" fill="#04040a"/>
          </g>
        );
      })}

      {/* coordinate axes (bottom left) */}
      <g transform="translate(70, 380)">
        <line x1="0" y1="0" x2="40" y2="22" stroke="#ff5a1f" strokeWidth="1.2"/>
        <text x="46" y="26" fill="#ff5a1f" fontFamily="var(--mono)" fontSize="10">X</text>
        <line x1="0" y1="0" x2="-40" y2="22" stroke="#4ade80" strokeWidth="1.2"/>
        <text x="-50" y="26" fill="#4ade80" fontFamily="var(--mono)" fontSize="10">Y</text>
        <line x1="0" y1="0" x2="0" y2="-40" stroke="#5dd2ff" strokeWidth="1.2"/>
        <text x="-4" y="-44" fill="#5dd2ff" fontFamily="var(--mono)" fontSize="10">Z</text>
      </g>

      {/* viewport HUD top-left */}
      <g transform="translate(20, 20)">
        <text fontFamily="var(--mono)" fontSize="9" fill="#6a6a72" letterSpacing="1.2">
          <tspan x="0" y="0">VIEWPORT · ISO NE · ORTHOGRAPHIC</tspan>
          <tspan x="0" y="14">build123d · OpenCASCADE 7.7.2</tspan>
          <tspan x="0" y="28" fill="#ededee">/sidecar/enclosure.stl</tspan>
        </text>
      </g>

      {/* top-right HUD */}
      <g transform="translate(700, 20)" textAnchor="end">
        <text fontFamily="var(--mono)" fontSize="9" fill="#ff5a1f" letterSpacing="1.2">
          <tspan x="0" y="0">● LIVE PREVIEW</tspan>
        </text>
        <text fontFamily="var(--mono)" fontSize="9" fill="#6a6a72" letterSpacing="1.2">
          <tspan x="0" y="14">238k tris · 1.42 MB</tspan>
          <tspan x="0" y="28">17 ms regen</tspan>
        </text>
      </g>
    </svg>
  );
};

window.ENCLOSURE_SVG = ENCLOSURE_SVG;
