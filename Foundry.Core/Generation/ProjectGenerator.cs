using System.Globalization;
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
public sealed partial class ProjectGenerator
{
    private readonly IAnthropicClient _ai;
    private readonly string _model;

    public ProjectGenerator(IAnthropicClient ai, string? model = null)
    {
        _ai = ai;
        _model = string.IsNullOrWhiteSpace(model) ? ModelCatalog.DefaultModelId : model;
    }

    private const string SystemPrompt = """
You are Foundry, an AI hardware-design studio. Given a maker's request, design a complete, buildable,
electrically-correct electronics project and return it as ONE JSON object. Use this exact shape:

{
 "title": "short product name",
 "summary": "one or two sentences",
 "subsystems": [{"role":"Controller","name":"ESP32 DevKit v1","mpn":"ESP32-DEVKITC-32E",
                 "specs":[["Logic","3.3 V"],["Idle","12 mA"]]}],
 "components": [{"alias":"MCU","ref":"esp32_devkit","name":"ESP32 DevKit v1","logicV":3.3,
                 "inputV":[3.0,5.5],"currentMa":80,
                 "pins":[{"name":"3V3","kind":"power"},{"name":"GND","kind":"ground"},
                         {"name":"GPIO34","kind":"analog","inputOnly":true},
                         {"name":"GPIO0","kind":"bidir","strapping":true}]},
                {"alias":"REG","ref":"ldo_3v3","name":"3.3 V LDO (AMS1117-3.3)","outputV":3.3,
                 "inputV":[4.5,12.0],"currentMa":1,
                 "pins":[{"name":"VIN","kind":"power"},{"name":"GND","kind":"ground"},{"name":"VOUT","kind":"power"}]},
                {"alias":"SENSOR","ref":"bme280","name":"BME280","logicV":3.3,"i2cAddr":"0x76","currentMa":1,
                 "pins":[{"name":"VCC","kind":"power"},{"name":"GND","kind":"ground"},
                         {"name":"SDA","kind":"bidir"},{"name":"SCL","kind":"bidir"},{"name":"AOUT","kind":"output"}]}],
 "bom": [{"qty":1,"name":"ESP32 DevKit v1","mpn":"ESP32-DEVKITC-32E","price":8.5,"stock":1442,
          "lead":"Stock","dist":"DigiKey","note":"Wi-Fi MCU"}],
 "connections": [{"from":"MCU.GPIO34","to":"SENSOR.AOUT","net":"signal"},
                 {"from":"MCU.GPIO21","to":"SENSOR.SDA","net":"i2c"}],
 "enclosure": {"inner":[62,48,26],"wall":2.0,"lid":"screw","standoffs":4,"mount":"wall-tabs",
               "cutouts":[{"face":"front","shape":"rect","size":[9.5,3.5],"pos":[0,-6],"label":"USB-C"},
                          {"face":"top","shape":"circle","d":6,"pos":[40,10],"label":"Reset button"},
                          {"face":"right","shape":"rect","size":[14,8],"pos":[0,0],"label":"Soil-probe slot"}],
               "vents":[{"face":"left","count":4},{"face":"right","count":4}]},
 "firmwarePlatform": "Arduino C++",
 "assembly": [{"title":"Prepare the enclosure","body":"...","chips":["enclosure.stl"]}]
}

OUTPUT CONTRACT — read this first, it is the most common failure:
- Return the JSON object and NOTHING else. No prose before or after, no markdown fences, no comments.
- Emit STRICT, valid JSON: double-quote every key and string, no trailing commas, no single quotes, no
  // or /* */ comments, no NaN/Infinity, no unquoted identifiers, numbers as plain JSON (3.3 not "3.3 V").
- Escape characters inside strings (\", \\, \n). Keep it compact — finish the object; never stop mid-token.
- Use only the fields shown. Do not wrap the object in another key.

ELECTRICAL RULES:
- Connection endpoints are "ALIAS.PIN" using the aliases and pin names you define in "components". Every
  endpoint pin MUST exist on its component. Net is one of power|ground|signal|i2c.
- Pin kind is power|ground|input|output|bidir|analog. Mark input-only pins ("inputOnly":true) and strapping
  pins ("strapping":true) so validation can avoid them. Never wire a sensed input to an input-only or
  strapping pin if a normal GPIO is free.
- Voltage: a component's "logicV" is its I/O level; "inputV":[min,max] is its supply tolerance. For any part
  that SOURCES a rail (battery, regulator, AC-DC module, USB/DC input) set "outputV" to the rail voltage it
  produces — validation identifies supplies by outputV, so omitting it breaks the power check. Don't drive a
  3.3 V logic input directly from a 5 V output; add a level shifter or divider as a real part when levels differ.
- Shared buses: put every device on one I²C bus on the SAME two nets (one SDA net, one SCL net) — don't
  duplicate per-device. Add ONE pair of pull-up resistors (e.g. 4.7 kΩ) to the bus as components + BOM.
- EVERY part on an I²C bus MUST carry its 7-bit "i2cAddr" ("0x76" or 118). Validation cannot check a bus for
  address collisions without it, and will report the design as unverified rather than assume you got it right.
- Add the passives a real board needs: a series resistor for every indicator LED, decoupling where it matters,
  and the I²C pull-ups above — as components AND BOM lines. These are checked by validation.

POWER SOURCE — ALWAYS include it (never omit it):
- Portable/battery: add the battery as a component AND BOM line with realistic "capacityMah" (a single
  18650 ≈ 3000), plus its charger/regulator (e.g. TP4056 + 3.3 V LDO) as components/BOM, regulator "outputV" set.
- USB- or DC-jack-powered: add the input (USB-C / DC jack) + regulator as components and BOM lines.
- Mains/AC-powered (relays, triacs, AC loads): add an ISOLATED AC-DC supply (e.g. HLK-PM01 5 V module) as a
  component and BOM line — never leave a mains design without its low-voltage supply.
- Wire the power/ground rails from the source through the regulator to each component.

ENCLOSURE — design it for THIS device, not a generic box:
- Size inner [L,W,H] (mm) to the actual parts plus ~3–5 mm clearance and the standoff height; don't guess round numbers.
- Add a cutout for EVERY external interface: USB/DC-power, each connector/header, buttons, status LEDs (small
  circle d≈3 as a light pipe), displays, sensor windows/probes, antenna. Put each on the face it naturally exits;
  pos is [x,y] mm offset from that face's centre (x horizontal, y vertical). faces: front|back|left|right|top|bottom.
  Keep every cutout at least ~6 mm in from every edge of its face — never place an opening at or near a corner,
  the wall is curved/weakened there and corner standoffs may live underneath.
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

        // Log only the prompt's length, never its body — AppLog persists to disk and is documented to never
        // contain prompts (a prompt can carry proprietary/PII content the user didn't consent to retaining).
        Diagnostics.AppLog.Info("generation", $"design pass started · model {_model}", $"prompt: {prompt.Length} chars");

        // Two attempts: complex designs occasionally truncate or return stray prose; a retry (with a
        // stricter nudge) recovers without bothering the user.
        string? json = null;
        for (int attempt = 1; attempt <= 2 && json is null; attempt++)
        {
            var user = attempt == 1 ? prompt
                : prompt + "\n\n(Your previous reply was not parseable JSON. Return ONE complete, strictly-valid JSON "
                    + "object and nothing else: no prose, no markdown fences, no comments, no trailing commas, all "
                    + "keys and strings double-quoted, numbers plain. Keep it compact enough to finish the object.)";
            string raw;
            try { raw = await _ai.CompleteAsync(SystemPrompt, user, _model, ct); }
            catch (Ai.TruncatedResponseException tex)
            {
                // Token-cap cutoff: the compact-JSON nudge on the retry often recovers it; only fail honestly
                // if the last attempt also truncates — never accept a half-built design.
                Diagnostics.AppLog.Warn("generation", $"attempt {attempt}: response truncated at the {tex.MaxTokens}-token cap — retrying compact");
                if (attempt == 2)
                    return new GenerationResult(false, null,
                        "The design response was cut off at the output-token cap. Raise the output-token limit in Settings or simplify the prompt.");
                continue;
            }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Error("generation", $"design pass failed: {ex.Message}");
                return new GenerationResult(false, null, $"Generation failed: {ex.Message}");
            }
            json = ExtractJson(raw);
            // Don't guess the cause here: AnthropicClient already emits an authoritative "TRUNCATED" WARN when
            // the model hit the token cap (stop_reason=max_tokens). If you see ONLY this line, the model stopped
            // normally but emitted unparseable JSON (stray prose, a syntax slip) — the retry usually recovers it.
            if (json is null) Diagnostics.AppLog.Warn("generation", $"attempt {attempt}: could not parse JSON from the response ({raw.Length} chars) — retrying");
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

        // Log only the request's length, never its body — AppLog persists to disk and is documented to
        // never contain prompts (a chat request can carry proprietary/PII content, same as a design prompt).
        Diagnostics.AppLog.Info("revise", $"revise pass started · model {_model}{(forceEdit ? " · force-edit" : "")}",
            $"request: {request.Length} chars");
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
                capacityMah = c.CapacityMah, i2cAddr = c.I2cAddress,
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


    /// <summary>Tolerate accidental markdown fences / leading prose by extracting the outermost JSON object.</summary>
    private static string? ExtractJson(string raw) => JsonText.Extract(raw);

    private Project.Project Map(string json, string prompt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var project = new Project.Project
        {
            // Collision-free: a clock-based id (p_HHmmss) repeats every 24h, and SaveToLibrary keys the
            // library file AND the revision folder off it — a repeat silently overwrote an existing project
            // and inherited its history. Matches ProjectStore.SaveToLibrary's own scheme.
            Id = "p_" + Guid.NewGuid().ToString("N")[..8],
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
        project.Validation = Validation.ProjectValidator.Rollup(project.Findings);

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

    /// <summary>Internal so tests can exercise the model-JSON → ComponentSpec mapping without an API key.</summary>
    internal static List<ComponentSpec> MapComponents(JsonElement root) =>
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
            I2cAddress = I2cAddr(e),
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
    /// <summary>
    /// 7-bit I²C address, accepting the forms models actually emit: "0x76", "76h", "118", or a plain
    /// number. Anything outside 0x08..0x77 (the addressable range — the rest are reserved) is dropped
    /// rather than trusted, which leaves the bus UNVERIFIED instead of silently checked against junk.
    /// </summary>
    private static int? I2cAddr(JsonElement e)
    {
        if (!e.TryGetProperty("i2cAddr", out var v)) return null;

        int? n = v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => ParseAddr(v.GetString()),
            _ => null,
        };
        return n is >= 0x08 and <= 0x77 ? n : null;
    }

    private static int? ParseAddr(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h2) ? h2 : null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

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
