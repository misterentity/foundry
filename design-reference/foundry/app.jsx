/* ============================================================
   FOUNDRY — app root, screen router, tweaks
   ============================================================ */

const DEFAULTS = /*EDITMODE-BEGIN*/{
  "accent": "#ff5a1f",
  "density": "comfy",
  "screen": "workspace",
  "tab": "overview",
  "showGrid": true,
  "displayFont": "Instrument Serif"
}/*EDITMODE-END*/;

const SCREENS = [
  { id: "onboarding", label: "Onboarding" },
  { id: "projects",   label: "Projects" },
  { id: "workspace",  label: "Workspace" },
];

const ACCENT_OPTIONS = ["#ff5a1f", "#4ade80", "#5dd2ff", "#fbbf24", "#c084fc"];
const FONT_OPTIONS = ["Instrument Serif", "Newsreader", "Playfair Display"];

const App = () => {
  const [t, setTweak] = useTweaks(DEFAULTS);
  const [screen, setScreen] = React.useState(t.screen || "workspace");
  const [tab, setTab] = React.useState(t.tab || "overview");

  // sync tweak-driven screen/tab when user changes them via the panel
  React.useEffect(() => { if (t.screen && t.screen !== screen) setScreen(t.screen); }, [t.screen]);
  React.useEffect(() => { if (t.tab && t.tab !== tab) setTab(t.tab); }, [t.tab]);

  // dev helper for screenshots: window.__nav({screen, tab})
  React.useEffect(() => {
    window.__nav = ({ screen: s, tab: tb }) => {
      if (s) { setScreen(s); setTweak({ screen: s }); }
      if (tb) { setTab(tb); setTweak({ tab: tb }); }
    };
  }, []);

  // apply tweaks as CSS vars
  React.useEffect(() => {
    const r = document.documentElement;
    r.style.setProperty("--accent", t.accent);
    r.style.setProperty("--serif", `"${t.displayFont}", Georgia, serif`);
    if (t.density === "compact") {
      r.style.setProperty("--rail-w", "188px");
      r.style.setProperty("--chat-w", "320px");
      r.style.setProperty("--titlebar-h", "30px");
      r.style.setProperty("--tabbar-h", "32px");
    } else {
      r.style.setProperty("--rail-w", "212px");
      r.style.setProperty("--chat-w", "360px");
      r.style.setProperty("--titlebar-h", "36px");
      r.style.setProperty("--tabbar-h", "38px");
    }
  }, [t.accent, t.displayFont, t.density]);

  const crumbs = screen === "onboarding"
    ? ["Foundry", "Setup"]
    : screen === "projects"
    ? ["Foundry", "Library"]
    : ["Foundry", "Cap. Soil Moisture Sentinel", (window.PROJECT && tab) ? tab.toUpperCase() : "Workspace"];

  return (
    <>
      <WindowChrome crumbs={crumbs}>
        {screen === "onboarding" && <Onboarding onContinue={() => { setScreen("projects"); setTweak({ screen: "projects" }); }}/>}
        {screen === "projects" && <ProjectsList
          onOpen={() => { setScreen("workspace"); setTweak({ screen: "workspace" }); }}
          onNew={() => { setScreen("workspace"); setTweak({ screen: "workspace" }); }}
        />}
        {screen === "workspace" && <Workspace
          tab={tab}
          onTab={(id) => { setTab(id); setTweak({ tab: id }); }}
          onProjectsView={() => { setScreen("projects"); setTweak({ screen: "projects" }); }}
        />}
      </WindowChrome>

      <TweaksPanel title="Tweaks" defaultPosition={{ right: 20, bottom: 64 }}>
        <TweakSection label="Screen">
          <TweakRadio
            value={screen}
            onChange={(v) => { setScreen(v); setTweak({ screen: v }); }}
            options={[
              { value: "onboarding", label: "Setup" },
              { value: "projects",   label: "Library" },
              { value: "workspace",  label: "Studio" },
            ]}
          />
        </TweakSection>

        {screen === "workspace" && (
          <TweakSection label="Workspace tab">
            <TweakSelect
              value={tab}
              onChange={(v) => { setTab(v); setTweak({ tab: v }); }}
              options={[
                { value: "overview",  label: "Overview" },
                { value: "bom",       label: "BOM" },
                { value: "wiring",    label: "Wiring" },
                { value: "enclosure", label: "Enclosure" },
                { value: "firmware",  label: "Firmware" },
                { value: "validation",label: "Validation" },
                { value: "guide",     label: "Assembly guide" },
              ]}
            />
          </TweakSection>
        )}

        <TweakSection label="Accent">
          <TweakColor
            value={t.accent}
            onChange={(v) => setTweak({ accent: v })}
            options={ACCENT_OPTIONS}
          />
        </TweakSection>

        <TweakSection label="Display font">
          <TweakSelect
            value={t.displayFont}
            onChange={(v) => setTweak({ displayFont: v })}
            options={FONT_OPTIONS.map(f => ({ value: f, label: f }))}
          />
        </TweakSection>

        <TweakSection label="Density">
          <TweakRadio
            value={t.density}
            onChange={(v) => setTweak({ density: v })}
            options={[
              { value: "comfy",   label: "Comfy" },
              { value: "compact", label: "Compact" },
            ]}
          />
        </TweakSection>
      </TweaksPanel>
    </>
  );
};

// load extra fonts on demand
(function loadExtraFonts() {
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = "https://fonts.googleapis.com/css2?family=Newsreader:ital,opsz@0,6..72;1,6..72&family=Playfair+Display:ital,wght@0,400..900;1,400..900&display=swap";
  document.head.appendChild(link);
})();

ReactDOM.createRoot(document.getElementById("root")).render(<App/>);
