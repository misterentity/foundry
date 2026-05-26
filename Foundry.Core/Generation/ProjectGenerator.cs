using System.Text.Json;
using Foundry.Core.Ai;
using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Core.Generation;

public sealed record GenerationResult(bool Ok, Project.Project? Project, string Message);

/// <summary>
/// Turns a plain-language prompt into a populated <see cref="Project.Project"/> via one structured
/// Claude call (PRD §7), then runs the deterministic engines over the result — validation findings,
/// firmware/pin-map, and KPIs are computed locally, not taken from the model. Defensive parsing
/// (PRD §13): a malformed response yields an error, never a crash.
/// </summary>
public sealed class ProjectGenerator
{
    private readonly IAnthropicClient _ai;
    private readonly string _model;

    public ProjectGenerator(IAnthropicClient ai, string? model = null)
    {
        _ai = ai;
        _model = string.IsNullOrWhiteSpace(model) ? ModelCatalog.DefaultModelId : model;
    }

    private const string SystemPrompt = """
You are Foundry, an AI hardware-design studio. Given a maker's request, design a complete, buildable
electronics project and return it as ONE JSON object — no prose, no markdown fences. Use this exact shape:

{
 "title": "short product name",
 "summary": "one or two sentences",
 "subsystems": [{"role":"Controller","name":"ESP32 DevKit v1","mpn":"ESP32-DEVKITC-32E",
                 "specs":[["Logic","3.3 V"],["Idle","12 mA"]]}],
 "components": [{"alias":"MCU","ref":"esp32_devkit","name":"ESP32 DevKit v1","logicV":3.3,
                 "inputV":[3.0,5.5],"currentMa":80,
                 "pins":[{"name":"3V3","kind":"power"},{"name":"GND","kind":"ground"},
                         {"name":"GPIO34","kind":"analog","inputOnly":true},
                         {"name":"GPIO0","kind":"bidir","strapping":true}]}],
 "bom": [{"qty":1,"name":"ESP32 DevKit v1","mpn":"ESP32-DEVKITC-32E","price":8.5,"stock":1442,
          "lead":"Stock","dist":"DigiKey","note":"Wi-Fi MCU"}],
 "connections": [{"from":"MCU.GPIO34","to":"SENSOR.AOUT","net":"signal"}],
 "enclosure": {"inner":[62,48,26],"wall":2.0,"lid":"screw","standoffs":4,"mount":"wall-tabs",
               "cutouts":[{"face":"front","shape":"rect","size":[9.5,3.5],"pos":[0,-6],"label":"USB-C"},
                          {"face":"top","shape":"circle","d":6,"pos":[40,10],"label":"Reset button"},
                          {"face":"right","shape":"rect","size":[14,8],"pos":[0,0],"label":"Soil-probe slot"}],
               "vents":[{"face":"left","count":4},{"face":"right","count":4}]},
 "firmwarePlatform": "Arduino C++",
 "assembly": [{"title":"Prepare the enclosure","body":"...","chips":["enclosure.stl"]}]
}

Rules: connection endpoints are "ALIAS.PIN" using the component aliases and pin names you define in
"components". Net is one of power|ground|signal|i2c. Every component needs power and ground nets where
applicable. Pin kind is power|ground|input|output|bidir|analog. Mark input-only and strapping pins.

Power source — ALWAYS include it (never omit it):
- Portable/battery designs: add the battery as a component AND a BOM line, with realistic "capacityMah"
  (e.g. a single 18650 ≈ 3000), plus its charger/regulator (e.g. TP4056 + 3.3 V LDO) as components/BOM.
- USB- or DC-jack-powered: add the input (USB-C / DC jack) + regulator as components and BOM lines.
- Mains/AC-powered (relays, triacs, AC loads): add an ISOLATED AC-DC supply (e.g. HLK-PM01 5 V module)
  as a component and BOM line — never leave a mains design without its low-voltage supply.
- Wire the power/ground rails from the source through the regulator to each component.

Enclosure — design it for THIS device, not a generic box:
- Size inner [L,W,H] (mm) to the actual parts plus ~3–5 mm clearance and the standoff height; don't guess round numbers.
- Add a cutout for EVERY external interface: USB/DC-power, each connector/header, buttons, status LEDs (small
  circle d≈3 as a light pipe), displays, sensor windows/probes, antenna. Put each on the face it naturally exits;
  pos is [x,y] mm offset from that face's centre (x horizontal, y vertical). faces: front|back|left|right|top|bottom.
- "vents":[{"face","count"}] — add ventilation slots when the design makes heat (regulators, motor/LED drivers,
  >300 mA draw, or a sealed warm part); omit for cool/low-power designs.
- "mount": "none" | "wall-tabs" (flanged screw tabs) | "flange" — pick for how it installs (a wall sensor → wall-tabs).
- "lid": "snap" for easy indoor access, "screw" for outdoor/secure/vibration. "standoffs": PCB mount-hole count (0–4).
Output ONLY the JSON object.
""";

    public async Task<GenerationResult> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        if (!_ai.HasKey)
            return new GenerationResult(false, null, "Add your Anthropic API key in Settings to generate a project.");
        if (string.IsNullOrWhiteSpace(prompt))
            return new GenerationResult(false, null, "Describe what you want to build.");

        Diagnostics.AppLog.Info("generation", $"design pass started · model {_model}", prompt);

        // Two attempts: complex designs occasionally truncate or return stray prose; a retry (with a
        // stricter nudge) recovers without bothering the user.
        string? json = null;
        for (int attempt = 1; attempt <= 2 && json is null; attempt++)
        {
            var user = attempt == 1 ? prompt
                : prompt + "\n\n(Return the COMPLETE JSON object only — no prose, no markdown fences, and keep it compact enough to finish.)";
            string raw;
            try { raw = await _ai.CompleteAsync(SystemPrompt, user, _model, ct); }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Error("generation", $"design pass failed: {ex.Message}");
                return new GenerationResult(false, null, $"Generation failed: {ex.Message}");
            }
            json = ExtractJson(raw);
            if (json is null) Diagnostics.AppLog.Warn("generation", $"attempt {attempt}: invalid/truncated JSON ({raw.Length} chars)");
        }
        if (json is null) return new GenerationResult(false, null, "The model did not return valid JSON after a retry. Try simplifying the prompt.");

        Project.Project project;
        try { project = Map(json, prompt); }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("generation", $"design parse failed: {ex.Message}");
            return new GenerationResult(false, null, $"Could not parse the design: {ex.Message}");
        }
        Diagnostics.AppLog.Info("generation",
            $"design parsed · {project.Subsystems.Count} subsystems · {project.Bom.Count} BOM · {project.Connections.Count} nets · {project.Findings.Count} findings");

        // Second pass: have the AI write the full, project-specific firmware (the deterministic
        // build from Map() stays as the fallback if this call fails). Pins remain netlist-derived.
        await EnrichFirmwareAsync(project, prompt, ct);

        return new GenerationResult(true, project, "Generated.");
    }

    /// <summary>
    /// Apply a chat request to the current design and return a fully revised project. When
    /// <paramref name="forceEdit"/> is true (e.g. validation auto-fix) the model MUST return an
    /// updated design — a prose-only reply is treated as a failure, not a Q&amp;A answer.
    /// </summary>
    public async Task<GenerationResult> ReviseAsync(Project.Project current, string request, CancellationToken ct = default, bool forceEdit = false)
    {
        if (!_ai.HasKey) return new GenerationResult(false, null, "Add your Anthropic API key in Settings to edit the design by chat.");
        if (string.IsNullOrWhiteSpace(request)) return new GenerationResult(false, null, "Tell me what to change.");

        Diagnostics.AppLog.Info("revise", $"revise pass started · model {_model}{(forceEdit ? " · force-edit" : "")}", request);
        string raw;
        try
        {
            var user = $"Current design (Foundry JSON):\n{BuildGenJson(current)}\n\nRequested change:\n{request}";
            raw = await _ai.CompleteAsync(forceEdit ? EditOnlySystemPrompt : ReviseSystemPrompt, user, _model, ct);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("revise", $"revise pass failed: {ex.Message}");
            return new GenerationResult(false, null, $"Revision failed: {ex.Message}");
        }

        var json = ExtractJson(raw);
        if (json is null)
        {
            if (forceEdit)   // a fix must produce a design edit, never a prose answer
            {
                Diagnostics.AppLog.Warn("revise", "force-edit returned no JSON design");
                return new GenerationResult(false, null, "The model didn't return an updated design. Try again or rephrase the fix.");
            }
            // The model answered a question / gave advice rather than editing the design — show its prose.
            Diagnostics.AppLog.Info("revise", "answered (no design change)");
            return new GenerationResult(true, null, raw.Trim());
        }

        Project.Project revised;
        try { revised = Map(json, current.Prompt); }
        catch (Exception ex) { return new GenerationResult(false, null, $"Couldn't parse the revision: {ex.Message}"); }

        revised.Id = current.Id;          // keep library identity
        revised.Prompt = current.Prompt;

        // Firmware only depends on the netlist + platform. If the edit didn't change the netlist,
        // keep the existing (AI-written) firmware and skip the second AI call — a big speedup.
        bool netlistChanged = !SameNetlist(current.Connections, revised.Connections)
            || !string.Equals(current.Firmware.Platform, revised.Firmware.Platform, StringComparison.OrdinalIgnoreCase);
        if (netlistChanged)
            await EnrichFirmwareAsync(revised, current.Prompt, ct);
        else
        {
            revised.Firmware = current.Firmware;
            Diagnostics.AppLog.Info("revise", "netlist unchanged — kept existing firmware (skipped firmware pass)");
        }

        Diagnostics.AppLog.Info("revise", $"revised · {revised.Bom.Count} BOM · {revised.Connections.Count} nets · {revised.Findings.Count} findings");
        return new GenerationResult(true, revised, "Revised.");
    }

    private static bool SameNetlist(List<Connection> a, List<Connection> b)
    {
        if (a.Count != b.Count) return false;
        static string Key(Connection c) => $"{c.From}|{c.To}|{c.Net}".ToLowerInvariant();
        var sa = a.Select(Key).OrderBy(x => x);
        var sb = b.Select(Key).OrderBy(x => x);
        return sa.SequenceEqual(sb);
    }

    private const string ReviseSystemPrompt = SystemPrompt +
        "\n\n--- REVISION MODE (overrides the 'only JSON' rule above) ---\n" +
        "You are assisting with an EXISTING design supplied as JSON. The user's message is either:\n" +
        "(a) a QUESTION or request for advice — reply with 2–5 sentences of PLAIN PROSE and NO JSON; or\n" +
        "(b) a concrete CHANGE to the design — return the FULL updated project as ONE JSON object in the schema " +
        "above, preserving everything the change doesn't affect and keeping connection endpoints consistent with " +
        "the components you define.\n" +
        "Decide which it is from their message. Output ONLY prose (for a question) or ONLY the JSON object (for a change). " +
        "Do not announce or restate which mode you chose — just answer directly, or just output the JSON.";

    private const string EditOnlySystemPrompt = SystemPrompt +
        "\n\n--- FIX MODE ---\n" +
        "You are revising an EXISTING design supplied as JSON to resolve a specific issue. Apply the change " +
        "and return the FULL updated project as ONE JSON object in the schema above, preserving everything the " +
        "change doesn't affect and keeping connection endpoints consistent with the components you define. " +
        "For a strapping/conflicting GPIO, move the net to a real free GPIO on that chip; for a logic-level " +
        "mismatch, insert a level shifter (add the part + nets); for a missing rail, add the connection. " +
        "Output ONLY the JSON object — no prose, no markdown fences.";

    private static string BuildGenJson(Project.Project p)
    {
        string Kind(Kb.PinKind k) => k switch
        {
            Kb.PinKind.Power => "power", Kb.PinKind.Ground => "ground", Kb.PinKind.Input => "input",
            Kb.PinKind.Output => "output", Kb.PinKind.Analog => "analog", _ => "bidir",
        };
        var dto = new
        {
            title = p.Title,
            summary = p.Prompt,
            subsystems = p.Subsystems.Select(s => new { role = s.Role, name = s.Name, mpn = s.Mpn,
                specs = s.Specs.Select(sp => new[] { sp.Key, sp.Value }) }),
            components = p.Components.Select(c => new { alias = c.Alias, @ref = c.Ref, name = c.Name,
                logicV = c.LogicV, inputV = c.InputVRange, outputV = c.OutputV, currentMa = c.CurrentMaActive,
                capacityMah = c.CapacityMah,
                pins = c.Pins.Select(pn => new { name = pn.Name, kind = Kind(pn.Kind), inputOnly = pn.InputOnly, strapping = pn.Strapping }) }),
            bom = p.Bom.Select(b => new { qty = b.Qty, name = b.Name, mpn = b.Mpn, price = b.Price, stock = b.Stock, lead = b.Lead, dist = b.Dist, note = b.Note }),
            connections = p.Connections.Select(c => new { from = c.From, to = c.To, net = c.Net }),
            enclosure = new { inner = p.Enclosure.Inner, wall = p.Enclosure.Wall, lid = p.Enclosure.Lid, standoffs = p.Enclosure.Standoffs,
                cutouts = p.Enclosure.Cutouts.Select(c => new { face = c.Face, shape = c.Shape, size = c.Size, d = c.D, pos = c.Pos, label = c.Label }) },
            firmwarePlatform = p.Firmware.Platform,
            assembly = p.Assembly.Select(a => new { title = a.Title, body = a.Body, chips = a.Chips }),
        };
        return JsonSerializer.Serialize(dto);
    }

    /// <summary>Ask the model for complete, working firmware for this exact device; inject the derived pin map.</summary>
    private async Task EnrichFirmwareAsync(Project.Project project, string prompt, CancellationToken ct)
    {
        try
        {
            var kb = new ComponentKb(project.Components);
            var entries = PinMap.Build(project.Connections, kb);
            var platform = project.Firmware.Platform;
            var pinmapName = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "pinmap.py" : "pinmap.h";
            var pinmap = platform.Contains("python", StringComparison.OrdinalIgnoreCase)
                ? string.Join("\n", entries.Select(e => $"{e.Macro} = {e.Gpio}  # {e.Net}: {e.FromPin} <-> {e.ToPin}"))
                : PinMap.RenderHeader(entries);

            var parts = string.Join("\n", project.Components.Select(c =>
                $"- {c.Alias} ({c.Name}): pins {string.Join(", ", c.Pins.Select(p => p.Name))}"));
            var nets = string.Join("\n", project.Connections.Select(c => $"- {c.From} -> {c.To} [{c.Net}]"));

            var macroList = string.Join(", ", entries.Select(e => e.Macro));
            var inc = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "from pinmap import *" : "#include \"pinmap.h\"";
            var user =
                $"Device: {prompt}\n\nPlatform: {platform}\nParts:\n{parts}\n\nNetlist:\n{nets}\n\n" +
                $"Pin map ({pinmapName}) is PRE-DEFINED and supplied — DO NOT redefine pins. In your main file you MUST " +
                $"`{inc}` and use ONLY these exact macro names (do not invent, rename, or alias them):\n{pinmap}\n\n" +
                $"Available pin macros (use verbatim): {macroList}";

            var raw = await _ai.CompleteAsync(FirmwareSystemPrompt, user, _model, ct);
            var json = ExtractJson(raw);
            if (json is null) return; // keep deterministic fallback

            var fw = MapFirmware(json, project.Firmware.Platform);
            if (fw.Files.Count == 0)
            {
                Diagnostics.AppLog.Warn("generation", "firmware pass returned no files — using deterministic fallback");
                return; // keep the deterministic fallback
            }
            Diagnostics.AppLog.Info("generation", $"firmware pass · {fw.Files.Count} files · {fw.Platform}");

            // guarantee the netlist-derived pin map is present + authoritative
            fw.Files.RemoveAll(f => f.Name.Equals(pinmapName, StringComparison.OrdinalIgnoreCase));
            fw.Files.Add(new FirmwareFile { Name = pinmapName, Path = "/foundry/firmware/", Content = pinmap });
            foreach (var f in fw.Files) f.Active = false;
            PickMainFile(fw.Files).Active = true;
            project.Firmware = fw;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("generation", $"firmware pass failed: {ex.Message} — using deterministic fallback");
        }
    }

    /// <summary>Ask the AI to write parametric OpenSCAD for this enclosure (PRD v2 Phase B).</summary>
    public async Task<(bool Ok, string Scad, string Message)> GenerateEnclosureScadAsync(Project.Project project, CancellationToken ct = default)
    {
        if (!_ai.HasKey) return (false, "", "Add your Anthropic API key in Settings.");
        var e = project.Enclosure;
        string Dim(int i) => e.Inner is { Length: > 0 } a && i < a.Length ? a[i].ToString("0.##") : "60";
        var cutouts = string.Join("\n", e.Cutouts.Select(c =>
            $"  - face {c.Face} · {c.Shape}" + (c.Shape == "circle"
                ? $" d={c.D:0.##}"
                : $" {(c.Size?.Length > 0 ? c.Size[0].ToString("0.##") : "10")}×{(c.Size?.Length > 1 ? c.Size[1].ToString("0.##") : "6")}")
              + $" pos=[{string.Join(",", c.Pos.Select(p => p.ToString("0.##")))}]  label=\"{c.Label}\""));
        var vents = e.Vents.Count == 0 ? "(none)" : string.Join(", ", e.Vents.Select(v => $"{v.Count}×{v.Face}"));
        var parts = string.Join("\n", project.Components.Take(8).Select(c => $"  - {c.Alias} ({c.Name})"));

        // Surface face dimensions and an occupied/free map so the model can apply style
        // features only to clear faces and size the recessed panel to contain its cutouts.
        double il = e.Inner is { Length: > 0 } ? e.Inner[0] : 60, iw = e.Inner is { Length: > 1 } ? e.Inner[1] : 40, ih = e.Inner is { Length: > 2 } ? e.Inner[2] : 25;
        double w = e.Wall, outerL = il + 2 * w, outerW = iw + 2 * w, outerH = ih + w;
        var occupied = new HashSet<string>(e.Cutouts.Select(c => c.Face), StringComparer.OrdinalIgnoreCase);
        occupied.UnionWith(e.Vents.Select(v => v.Face));
        string FaceState(string face, double fw, double fh) =>
            $"  - {face,-6} {fw:0.##}×{fh:0.##} mm  {(occupied.Contains(face) ? "[OCCUPIED — cutouts/vents present]" : "[free — style here]")}";
        var faceMap = string.Join("\n", new[]
        {
            FaceState("front",  outerL, outerH),
            FaceState("back",   outerL, outerH),
            FaceState("left",   outerW, outerH),
            FaceState("right",  outerW, outerH),
            FaceState("top",    outerL, outerW),
            FaceState("bottom", outerL, outerW),
        });

        var user =
            $"Design a 3D-printable enclosure for this device. Output ONLY parametric OpenSCAD code (no prose, no fences).\n\n" +
            $"Inner cavity: [{il:0.##}, {iw:0.##}, {ih:0.##}] mm   outer: [{outerL:0.##}, {outerW:0.##}, {outerH:0.##}] mm   wall: {w:0.##} mm   lid: {e.Lid}   mount: {e.Mount}   standoffs: {e.Standoffs}\n" +
            $"Faces (outer dimensions + occupancy):\n{faceMap}\n\n" +
            $"Vents: {vents}\nCutouts:\n{(string.IsNullOrEmpty(cutouts) ? "  (none)" : cutouts)}\n\nComponents:\n{parts}\n\n" +
            "Reminder: cutout `pos = [x,y]` is the offset from the FACE's centre (x = horizontal axis of that face, y = vertical axis); " +
            "see the face-axes block in the system prompt. " +
            "Apply the recess/bezel to a face that has cutouts (size the recess to contain ALL of that face's cutouts + a 2 mm margin). " +
            "Place the accent ridge on a face marked [free] (prefer the lid top, then back); never run it across an OCCUPIED face. " +
            "Apply the 45° corner clip only at a corner whose two adjacent faces are [free] within 8 mm. " +
            "Subtract every cutout LAST and use a cut depth ≥ wall + bezel_inset + accent_ridge_w + 4 mm so it pierces through any style feature in front of it.";

        try
        {
            var raw = await _ai.CompleteAsync(EnclosureScadSystemPrompt, user, _model, ct);
            var scad = StripFences(raw).Trim();
            if (scad.Length < 50 || !LooksLikeScad(scad))
                return (false, "", "The model didn't return OpenSCAD code. Try again.");
            Diagnostics.AppLog.Info("scad", $"enclosure SCAD generated · {scad.Length} chars");
            return (true, scad, "Generated.");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("scad", $"enclosure SCAD failed: {ex.Message}");
            return (false, "", $"Failed: {ex.Message}");
        }
    }

    /// <summary>Ask the AI to repair OpenSCAD code given the compiler stderr (PRD v2 Phase D — SCAD fix loop).</summary>
    public async Task<(bool Ok, string Scad, string Message)> FixEnclosureScadAsync(Project.Project project, string currentScad, string compilerError, CancellationToken ct = default)
    {
        if (!_ai.HasKey || string.IsNullOrWhiteSpace(compilerError)) return (false, currentScad, "no error to fix");
        var user =
            $"This OpenSCAD script fails to render in OpenSCAD 2021.01. Fix it and return ONLY the corrected " +
            $"OpenSCAD code (no prose, no fences). Keep the parametric structure.\n\n" +
            $"COMPILER ERROR:\n{compilerError}\n\nCURRENT SCAD:\n{currentScad}";
        try
        {
            var raw = await _ai.CompleteAsync(EnclosureScadSystemPrompt, user, _model, ct);
            var scad = StripFences(raw).Trim();
            if (scad.Length < 50 || !LooksLikeScad(scad)) return (false, currentScad, "fix didn't return SCAD");
            Diagnostics.AppLog.Info("scad", $"SCAD fix applied · {scad.Length} chars");
            return (true, scad, "Fixed.");
        }
        catch (Exception ex) { return (false, currentScad, $"Fix failed: {ex.Message}"); }
    }

    private const string EnclosureScadSystemPrompt = """
You are a senior parametric CAD engineer designing **techno-futurist** 3D-printable enclosures.
Aim for the look of a high-end gadget — chamfered edges, recessed face panels, subtle accent ridges,
diagonal vent grilles — never a plain box. Write COMPLETE, valid OpenSCAD that satisfies the request.

Required parametric structure
- Start with a clearly-named block of top-level parameters: `wall_thickness`, `inner_l`, `inner_w`,
  `inner_h`, `lid_thickness`, `screw_diameter`, plus styling knobs (`chamfer`, `bezel_inset`,
  `accent_ridge_w`, `vent_angle`) so the user can dial the look in. Realistic defaults; integer
  for counts.
- Build geometry from named modules: `outer_shell`, `lid`, `cutouts`, `standoffs`, `mounts`, plus a
  `style_features` module for the futurist details. Use difference/union/hull/minkowski/rotate/
  translate cleanly. Keep `$fn = 32` for speed (use 24 for very small fillets).

Coordinate convention — STRICT
- The base origin is the OUTER box centre at z=0. The base sits with its bottom on z=0 and its
  open top at z = `inner_h + wall_thickness`. Outer footprint is `outer_l × outer_w` where
  `outer_l = inner_l + 2*wall_thickness` and `outer_w = inner_w + 2*wall_thickness`.
- Cutout `pos = [x, y]` is the offset from the FACE'S OWN CENTRE, in millimetres, where x is the
  horizontal axis of that face and y is the vertical axis of that face (z is "up" on the
  side/front/back faces; y is "up" on the top/bottom faces). `(0,0)` means the face's exact centre.
- Face axes (re-confirm before placing each cutout):
    * front  (−Y wall)  → x along +X (left→right), y along +Z (down→up)
    * back   (+Y wall)  → x along −X,              y along +Z
    * left   (−X wall)  → x along +Y (back→front), y along +Z
    * right  (+X wall)  → x along −Y,              y along +Z
    * top    (+Z lid)   → x along +X,              y along +Y
    * bottom (−Z floor) → x along +X,              y along −Y

Build order — MUST follow this sequence
1. Build the outer shell (chamfered + top-bevelled).
2. UNION style additions (recessed panels, raised bezels, accent ridges, light-pipe trim).
3. UNION standoffs and mount tabs/flanges.
4. DIFFERENCE the inner cavity.
5. DIFFERENCE every cutout — LAST. Each cutout's subtracting tool extends from the inside of the
   cavity all the way through any style additions on that face (cut depth ≥ `wall_thickness +
   bezel_inset + accent_ridge_w + 4`). Result: cutouts always read as clean openings, never
   partially blocked by a ridge, bezel border, or recess wall.
6. DIFFERENCE vent slots (also using a generous depth, same reasoning).

Cutout keepout — style features must yield to cutouts
- For every cutout, treat its bounding box plus a 2 mm margin as a KEEPOUT zone on its face.
- The accent ridge must skip keepout zones (split it into segments that go AROUND them, or place
  the ridge on a face that has NO cutouts — preferring the lid top, then the back face).
- The recessed-panel bezel border must STAY CLEAR of every cutout's keepout — either size the
  recess big enough to contain all the face's cutouts inside it (with the 2 mm margin), or shrink
  it to a region that contains no cutouts at all. Never let the raised bezel border cross a cutout.
- The 45° stealth corner clip is allowed ONLY on a corner whose two adjacent faces have NO cutouts
  within 8 mm of that corner. If no such corner exists, omit the corner clip rather than break a
  cutout.
- Light-pipe slits are extra cutouts — they obey the same keepout rule (never inside another
  cutout's keepout).

Techno-futurist styling — REQUIRED, applied subject to the keepout rule above
- **Chamfered vertical edges** on the outer shell (`hull()` over offset round-rects, or a
  `minkowski` with a small chamfer cylinder). 2–3 mm radius reads "designed", not "printed-box".
- **Top edge chamfer/bevel** on the base wall tops and the lid — a ~1.5 mm 45° bevel.
- **Recessed face panel** on a face that has cutouts (front by default): inset `bezel_inset`
  (1–2 mm) with a slim raised border (the bezel) framing the cutouts. The recess must contain
  ALL of that face's cutouts plus their 2 mm margin.
- **Slim accent ridge** (~1 mm proud, `accent_ridge_w` ≈ 3 mm wide), parallel to the long axis,
  stopping short of corners. Prefer the lid top or a cutout-free side; segment around keepouts.
- **Diagonal/angled vent grille**: rotate vent slots by `vent_angle` (default 25°) or use a
  triangular/hex slot pattern. Keep the rotated slot bounding box entirely inside the face.
- **Stealth corner detail**: 45° angled cut on a corner — subject to the keepout rule above.
- **Light-pipe slits** for indicator LEDs near the front bezel (thin 1.0×4 mm cutouts) when an
  LED appears in the components and a clear spot exists.
- Keep the design printable: no overhangs >45°, no bridges longer than the wall is thick, no
  fragile features <0.8 mm. Avoid `text()` (fonts aren't installed).

Functional rules (must still be met)
- Place every requested cutout on its named face at the given (x,y) offset, using the face
  axes above. Do not drop or relocate cutouts.
- Vent slots respect the schema's face + count, but render as the angled/grille style above.
- Add corner standoffs with M2/M3 pilot holes when the schema asks for them; position them so
  they do not collide with any cutout's keepout.
- `lid == "screw"` → add corner screw bosses + matching clearance holes in the lid.
- `lid == "snap"` → add a locating lip on the lid that nests inside the base opening.
- `mount == "wall-tabs"` → flanged wall-mount tabs with screw holes on the long sides
  (avoid faces dense with cutouts).
- Render the base and the lid SIDE-BY-SIDE at z=0 (translate the lid +X past the base by
  `outer_l + 12`), oriented for printing so the .stl is ready to slice.

Output discipline
- OpenSCAD 2021.01 only. No external libraries, no `text()`, no `import()`.
- Output ONLY OpenSCAD code — no markdown fences, no prose, no explanation.
""";

    private static bool LooksLikeScad(string s) =>
        s.Contains("module ") || s.Contains("difference(") || s.Contains("union(") || s.Contains("cube(") || s.Contains("cylinder(");

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```")) { var nl = s.IndexOf('\n'); if (nl > 0) s = s[(nl + 1)..]; }
        if (s.EndsWith("```")) s = s[..^3];
        return s.Trim();
    }

    /// <summary>Suggest pin-compatible substitute parts for a BOM line, ranked cheaper/in-stock (PRD v2 G10).</summary>
    public async Task<List<Sourcing.Alternate>> SuggestAlternatesAsync(string partName, string mpn, CancellationToken ct = default)
    {
        var result = new List<Sourcing.Alternate>();
        if (!_ai.HasKey || string.IsNullOrWhiteSpace(partName)) return result;
        const string sys =
            "You are a hardware sourcing expert. Given a component, propose up to 3 PIN-COMPATIBLE, drop-in " +
            "alternative parts that are cheaper and/or more widely in stock. Real parts only, with real MPNs. " +
            "Return ONLY this JSON: {\"alternates\":[{\"name\":\"\",\"mpn\":\"\",\"price\":0.0,\"note\":\"why it's a good swap\"}]}";
        try
        {
            var raw = await _ai.CompleteAsync(sys, $"Component: {partName} (MPN {mpn}). Suggest pin-compatible alternatives.", _model, ct);
            var json = ExtractJson(raw);
            if (json is null) return result;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("alternates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                    result.Add(new Sourcing.Alternate
                    {
                        Name = Str(a, "name", ""), Mpn = Str(a, "mpn", ""),
                        Price = Num(a.TryGetProperty("price", out var pv) ? pv : default), Note = Str(a, "note", ""),
                        Replaces = partName,
                    });
        }
        catch (Exception ex) { Diagnostics.AppLog.Warn("sourcing", $"alternates failed: {ex.Message}"); }
        return result.Where(a => a.Name.Length > 0).Take(3).ToList();
    }

    /// <summary>Ask the AI to fix firmware that failed to compile, given the compiler errors (PRD v2 G3).</summary>
    public async Task<bool> FixFirmwareAsync(Project.Project project, string compilerErrors, CancellationToken ct = default)
    {
        if (!_ai.HasKey || string.IsNullOrWhiteSpace(compilerErrors)) return false;
        try
        {
            var kb = new ComponentKb(project.Components);
            var entries = PinMap.Build(project.Connections, kb);
            var platform = project.Firmware.Platform;
            var pinmapName = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "pinmap.py" : "pinmap.h";
            var pinmap = platform.Contains("python", StringComparison.OrdinalIgnoreCase)
                ? string.Join("\n", entries.Select(e => $"{e.Macro} = {e.Gpio}"))
                : PinMap.RenderHeader(entries);
            var inc = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "from pinmap import *" : "#include \"pinmap.h\"";
            var current = string.Join("\n\n", project.Firmware.Files
                .Where(f => !f.Name.Equals(pinmapName, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"// ===== FILE: {f.Name} =====\n{f.Content}"));

            var user =
                $"This {platform} firmware fails to compile. Fix the errors and return the FULL corrected firmware as " +
                $"the usual JSON. Keep the same files; change only what's needed.\n\nCOMPILER ERRORS:\n{compilerErrors}\n\n" +
                $"Pin map ({pinmapName}) is PRE-DEFINED — `{inc}` and use these macros verbatim:\n{pinmap}\n\n" +
                $"CURRENT FIRMWARE:\n{current}";

            var raw = await _ai.CompleteAsync(FirmwareSystemPrompt, user, _model, ct);
            var json = ExtractJson(raw);
            if (json is null) return false;
            var fw = MapFirmware(json, platform);
            if (fw.Files.Count == 0) return false;

            fw.Files.RemoveAll(f => f.Name.Equals(pinmapName, StringComparison.OrdinalIgnoreCase));
            fw.Files.Add(new FirmwareFile { Name = pinmapName, Path = "/foundry/firmware/", Content = pinmap });
            foreach (var f in fw.Files) f.Active = false;
            PickMainFile(fw.Files).Active = true;
            project.Firmware = fw;
            Diagnostics.AppLog.Info("build", $"AI build-fix applied · {fw.Files.Count} files");
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("build", $"build-fix failed: {ex.Message}");
            return false;
        }
    }

    private const string FirmwareSystemPrompt = """
You are a senior embedded-firmware engineer. Write COMPLETE, working firmware for the exact device
described — real application logic, not a skeleton: initialize every peripheral, implement the
protocols it needs (Wi-Fi/MQTT/HTTP/BLE/I2C/SPI/ADC as appropriate), the main control loop, timing,
and sensible defaults. The primary sketch MUST be named exactly "main.ino" (Arduino C++) or "main.py"
(MicroPython) — name it nothing else. The main file MUST include the supplied pin map
(`#include "pinmap.h"` for Arduino, `from pinmap import *` for MicroPython) and use its PIN_* macros
verbatim for every pin — never hard-code, rename, redefine, or invent pin macros not in the supplied map.
Put secrets (Wi-Fi creds, tokens) as clearly-marked #define/constant placeholders in a separate config
file. Use only widely-available libraries. Return ONLY one JSON object, no prose:
{
 "platform": "Arduino C++" | "MicroPython",
 "board": "esp32:esp32:esp32",
 "files": [{"name":"main.ino","content":"<full source>"}, {"name":"config.h","content":"..."}],
 "libraries": [["WiFi","built-in"], ["PubSubClient","2.8"]]
}
Include the main sketch and any helper/config files. Do NOT include the pin-map file (it is supplied).
""";

    /// <summary>The sketch to show/flash first: a main.* file if present, else the largest source file.</summary>
    public static FirmwareFile PickMainFile(IReadOnlyList<FirmwareFile> files)
    {
        var main = files.FirstOrDefault(f => f.Name.StartsWith("main", StringComparison.OrdinalIgnoreCase));
        if (main is not null) return main;
        bool IsSource(string n) => n.EndsWith(".ino") || n.EndsWith(".py") || n.EndsWith(".cpp") || n.EndsWith(".c");
        return files.Where(f => IsSource(f.Name.ToLowerInvariant())).OrderByDescending(f => f.Content.Length).FirstOrDefault()
               ?? files[0];
    }

    private static Project.Firmware MapFirmware(string json, string fallbackPlatform)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new Project.Firmware
        {
            Platform = Str(root, "platform", fallbackPlatform),
            Board = Str(root, "board", ""),
            Files = Arr(root, "files")
                .Where(f => f.TryGetProperty("name", out _) )
                .Select(f => new FirmwareFile
                {
                    Name = Str(f, "name", "file.txt"),
                    Path = "/foundry/firmware/",
                    Content = Str(f, "content", ""),
                })
                .Where(f => f.Content.Length > 0)
                .ToList(),
            Libraries = Arr(root, "libraries")
                .Where(l => l.ValueKind == JsonValueKind.Array && l.GetArrayLength() >= 2)
                .Select(l => new SpecPair(l[0].GetString() ?? "", l[1].GetString() ?? "")).ToList(),
        };
    }

    /// <summary>Tolerate accidental markdown fences / leading prose by extracting the outermost JSON object.</summary>
    private static string? ExtractJson(string raw)
    {
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var slice = raw[start..(end + 1)];
        try { using var _ = JsonDocument.Parse(slice); return slice; }
        catch { return null; }
    }

    private Project.Project Map(string json, string prompt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var project = new Project.Project
        {
            Id = "p_" + DateTime.Now.ToString("HHmmss"),
            Title = Str(root, "title", "Untitled project"),
            Prompt = prompt,
            Status = "READY",
            Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Subsystems = MapSubsystems(root),
            Bom = MapBom(root),
            Connections = MapConnections(root),
            Enclosure = MapEnclosure(root),
            Assembly = MapAssembly(root),
            Components = MapComponents(root),
        };

        var kb = new ComponentKb(project.Components);
        var platform = Str(root, "firmwarePlatform", "Arduino C++").Contains("python", StringComparison.OrdinalIgnoreCase)
            ? FirmwarePlatform.MicroPython : FirmwarePlatform.ArduinoCpp;

        project.Firmware = FirmwareGenerator.Generate(project.Connections, kb, platform);
        project.Findings = RulesEngine.Validate(project.Connections, kb, batteryGoalDays: 0);
        project.Validation = project.Findings.Any(f => f.Severity == "fail") ? "fail"
            : project.Findings.Any(f => f.Severity == "warn") ? "warn" : "pass";

        var peakMa = kb.All.Sum(c => c.CurrentMaActive);
        project.Kpis = new ProjectKpis
        {
            Parts = project.Bom.Sum(b => b.Qty),
            Cost = project.Bom.Sum(b => b.Qty * b.Price),
            CurrentMa = peakMa,
            BatteryDays = 0,
            PrintGrams = (int)EstimatePrintGrams(project.Enclosure),
        };

        project.Chat = new List<ChatMessage>
        {
            new() { Role = "user", Text = prompt, Time = DateTime.Now.ToString("HH:mm") },
            new() { Role = "assistant", Time = DateTime.Now.ToString("HH:mm"),
                    Text = Str(root, "summary", "Designed your project. Review the tabs, then iterate by chat."),
                    Pipeline = IPipeline.Stages.Select(s => new PipelineStage(s, "done")).ToList() },
        };
        return project;
    }

    private static List<Subsystem> MapSubsystems(JsonElement root) =>
        Arr(root, "subsystems").Select((e, i) => new Subsystem
        {
            Id = "s" + i, Role = Str(e, "role", "Subsystem"), Name = Str(e, "name", "Part"), Mpn = Str(e, "mpn", ""),
            Specs = Arr(e, "specs").Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
                .Select(p => new SpecPair(p[0].GetString() ?? "", p[1].GetString() ?? "")).ToList(),
        }).ToList();

    private static List<BomLine> MapBom(JsonElement root) =>
        Arr(root, "bom").Select(e => new BomLine
        {
            Qty = Int(e, "qty", 1), Name = Str(e, "name", "Part"), Mpn = Str(e, "mpn", ""),
            Price = Dbl(e, "price", 0), Stock = Int(e, "stock", 0),
            Lead = Str(e, "lead", "—"), Dist = Str(e, "dist", "DigiKey"), Note = Str(e, "note", ""),
        }).ToList();

    private static List<Connection> MapConnections(JsonElement root) =>
        Arr(root, "connections").Select(e => new Connection
        {
            From = Str(e, "from", ""), To = Str(e, "to", ""), Net = Str(e, "net", "signal"),
        }).Where(c => c.From.Length > 0 && c.To.Length > 0).ToList();

    private static List<ComponentSpec> MapComponents(JsonElement root) =>
        Arr(root, "components").Select(e => new ComponentSpec
        {
            Ref = Str(e, "ref", Str(e, "alias", "part")),
            Alias = Str(e, "alias", "PART"),
            Name = Str(e, "name", "Part"),
            LogicV = e.TryGetProperty("logicV", out var lv) && lv.ValueKind != JsonValueKind.Null ? Num(lv) : null,
            InputVRange = e.TryGetProperty("inputV", out var iv) && iv.ValueKind == JsonValueKind.Array && iv.GetArrayLength() >= 2
                ? new[] { Num(iv[0]), Num(iv[1]) } : null,
            OutputV = e.TryGetProperty("outputV", out var ov) && ov.ValueKind != JsonValueKind.Null && ov.ValueKind != JsonValueKind.Array ? Num(ov) : null,
            CurrentMaActive = Int(e, "currentMa", 0),
            CapacityMah = Int(e, "capacityMah", 0),
            Pins = Arr(e, "pins").Select(p => new PinSpec
            {
                Name = Str(p, "name", "?"),
                Kind = ParseKind(Str(p, "kind", "bidir")),
                InputOnly = Bool(p, "inputOnly"),
                Strapping = Bool(p, "strapping"),
            }).ToList(),
        }).ToList();

    private static Enclosure MapEnclosure(JsonElement root)
    {
        if (!root.TryGetProperty("enclosure", out var e) || e.ValueKind != JsonValueKind.Object)
            return new Enclosure { Inner = new double[] { 60, 40, 25 }, Wall = 2.0, Lid = "snap" };
        return new Enclosure
        {
            Inner = NormalizeInner(e),
            Wall = Dbl(e, "wall", 2.0),
            Lid = Str(e, "lid", "snap"),
            Standoffs = Int(e, "standoffs", 4),
            Mount = Str(e, "mount", "none"),
            Vents = Arr(e, "vents").Select(v => new Vent
            {
                Face = Str(v, "face", "left"),
                Count = Math.Clamp(Int(v, "count", 4), 1, 12),
            }).ToList(),
            MassGrams = 0,
            PrintTime = "",
            Cutouts = Arr(e, "cutouts").Select(c => new Cutout
            {
                Face = Str(c, "face", "side"), Shape = Str(c, "shape", "rect"), Label = Str(c, "label", ""),
                Size = c.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Array
                    ? sz.EnumerateArray().Select(x => Num(x)).ToArray() : null,
                D = c.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Number ? Num(d) : null,
                Pos = c.TryGetProperty("pos", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => Num(x)).ToArray() : new double[] { 0, 0 },
            }).ToList(),
        };
    }

    /// <summary>Always return a 3-element [L,W,H] so downstream readouts can't index out of range.</summary>
    private static double[] NormalizeInner(JsonElement enclosure)
    {
        var dims = enclosure.TryGetProperty("inner", out var inner) && inner.ValueKind == JsonValueKind.Array
            ? inner.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList()
            : new List<double>();
        var def = new[] { 60.0, 40.0, 25.0 };
        return new[]
        {
            dims.Count > 0 ? dims[0] : def[0],
            dims.Count > 1 ? dims[1] : def[1],
            dims.Count > 2 ? dims[2] : def[2],
        };
    }

    private static List<AssemblyStep> MapAssembly(JsonElement root) =>
        Arr(root, "assembly").Select((e, i) => new AssemblyStep
        {
            N = i + 1, Title = Str(e, "title", $"Step {i + 1}"), Body = Str(e, "body", ""),
            Chips = Arr(e, "chips").Select(c => c.GetString() ?? "").Where(s => s.Length > 0).ToList(),
        }).ToList();

    private static double EstimatePrintGrams(Enclosure e)
    {
        if (e.Inner.Length < 3) return 0;
        double l = e.Inner[0] + 2 * e.Wall, w = e.Inner[1] + 2 * e.Wall, h = e.Inner[2] + e.Wall;
        double shellCm3 = (l * w * h - e.Inner[0] * e.Inner[1] * e.Inner[2]) / 1000.0;
        return Math.Round(shellCm3 * 1.24 * 0.3); // PETG density × ~30% infill-equivalent wall
    }

    private static PinKind ParseKind(string k) => k.ToLowerInvariant() switch
    {
        "power" => PinKind.Power, "ground" => PinKind.Ground, "input" => PinKind.Input,
        "output" => PinKind.Output, "analog" => PinKind.Analog, _ => PinKind.Bidir,
    };

    // ---- defensive JSON helpers ----
    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string Str(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) ? (int)Math.Round(Num(v, fallback)) : fallback;
    private static double Dbl(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) ? Num(v, fallback) : fallback;
    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True);

    /// <summary>Read a number from a JSON value, tolerating decimals, out-of-range ints, and numeric strings
    /// (the model sometimes returns e.g. capacityMah:3000.0, currentMa:0.08, or "2.0"). Never throws.</summary>
    private static double Num(JsonElement v, double fallback = 0)
    {
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetDouble(out var d) ? d : fallback;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(
                new string((v.GetString() ?? "").Where(ch => char.IsDigit(ch) || ch is '.' or '-' or '+').ToArray()),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s))
            return s;
        return fallback;
    }
}
