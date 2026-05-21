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
 "enclosure": {"inner":[62,48,26],"wall":2.0,"lid":"snap","standoffs":4,
               "cutouts":[{"face":"side","shape":"rect","size":[9.5,6.5],"pos":[12,18],"label":"USB-C"},
                          {"face":"top","shape":"circle","d":6,"pos":[40,10],"label":"Reset"}]},
 "firmwarePlatform": "Arduino C++",
 "assembly": [{"title":"Prepare the enclosure","body":"...","chips":["enclosure.stl"]}]
}

Rules: connection endpoints are "ALIAS.PIN" using the component aliases and pin names you define in
"components". Net is one of power|ground|signal|i2c. Every component needs power and ground nets where
applicable. Pin kind is power|ground|input|output|bidir|analog. Mark input-only and strapping pins.
Keep it realistic and minimal. Output ONLY the JSON object.
""";

    public async Task<GenerationResult> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        if (!_ai.HasKey)
            return new GenerationResult(false, null, "Add your Anthropic API key in Settings to generate a project.");
        if (string.IsNullOrWhiteSpace(prompt))
            return new GenerationResult(false, null, "Describe what you want to build.");

        Diagnostics.AppLog.Info("generation", $"design pass started · model {_model}", prompt);

        string raw;
        try { raw = await _ai.CompleteAsync(SystemPrompt, prompt, _model, ct); }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("generation", $"design pass failed: {ex.Message}");
            return new GenerationResult(false, null, $"Generation failed: {ex.Message}");
        }

        var json = ExtractJson(raw);
        if (json is null) return new GenerationResult(false, null, "The model did not return valid JSON. Try again.");

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

    /// <summary>Apply a chat request to the current design and return a fully revised project.</summary>
    public async Task<GenerationResult> ReviseAsync(Project.Project current, string request, CancellationToken ct = default)
    {
        if (!_ai.HasKey) return new GenerationResult(false, null, "Add your Anthropic API key in Settings to edit the design by chat.");
        if (string.IsNullOrWhiteSpace(request)) return new GenerationResult(false, null, "Tell me what to change.");

        Diagnostics.AppLog.Info("revise", $"revise pass started · model {_model}", request);
        string raw;
        try
        {
            var user = $"Current design (Foundry JSON):\n{BuildGenJson(current)}\n\nRequested change:\n{request}";
            raw = await _ai.CompleteAsync(ReviseSystemPrompt, user, _model, ct);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("revise", $"revise pass failed: {ex.Message}");
            return new GenerationResult(false, null, $"Revision failed: {ex.Message}");
        }

        var json = ExtractJson(raw);
        if (json is null)
        {
            // The model answered a question / gave advice rather than editing the design — show its prose.
            Diagnostics.AppLog.Info("revise", "answered (no design change)");
            return new GenerationResult(true, null, raw.Trim());
        }

        Project.Project revised;
        try { revised = Map(json, current.Prompt); }
        catch (Exception ex) { return new GenerationResult(false, null, $"Couldn't parse the revision: {ex.Message}"); }

        revised.Id = current.Id;          // keep library identity
        revised.Prompt = current.Prompt;
        await EnrichFirmwareAsync(revised, current.Prompt, ct);
        Diagnostics.AppLog.Info("revise", $"revised · {revised.Bom.Count} BOM · {revised.Connections.Count} nets · {revised.Findings.Count} findings");
        return new GenerationResult(true, revised, "Revised.");
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

            var user =
                $"Device: {prompt}\n\nPlatform: {platform}\nParts:\n{parts}\n\nNetlist:\n{nets}\n\n" +
                $"Pin map ({pinmapName}) — reference these macros, do not redefine pins:\n{pinmap}";

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
            (fw.Files.FirstOrDefault(f => f.Name.StartsWith("main", StringComparison.OrdinalIgnoreCase)) ?? fw.Files[0]).Active = true;
            project.Firmware = fw;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("generation", $"firmware pass failed: {ex.Message} — using deterministic fallback");
        }
    }

    private const string FirmwareSystemPrompt = """
You are a senior embedded-firmware engineer. Write COMPLETE, working firmware for the exact device
described — real application logic, not a skeleton: initialize every peripheral, implement the
protocols it needs (Wi-Fi/MQTT/HTTP/BLE/I2C/SPI/ADC as appropriate), the main control loop, timing,
and sensible defaults. Reference the pin macros from the provided pin map; never hard-code or redefine
pins. Put secrets (Wi-Fi creds, tokens) as clearly-marked #define/constant placeholders in a separate
config file. Return ONLY one JSON object, no prose:
{
 "platform": "Arduino C++" | "MicroPython",
 "board": "esp32:esp32:esp32",
 "files": [{"name":"main.ino","content":"<full source>"}, {"name":"config.h","content":"..."}],
 "libraries": [["WiFi","built-in"], ["PubSubClient","2.8"]]
}
Include the main sketch and any helper/config files. Do NOT include the pin-map file (it is supplied).
""";

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
            LogicV = e.TryGetProperty("logicV", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetDouble() : null,
            InputVRange = e.TryGetProperty("inputV", out var iv) && iv.ValueKind == JsonValueKind.Array && iv.GetArrayLength() >= 2
                ? new[] { iv[0].GetDouble(), iv[1].GetDouble() } : null,
            OutputV = e.TryGetProperty("outputV", out var ov) && ov.ValueKind == JsonValueKind.Number ? ov.GetDouble() : null,
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
            MassGrams = 0,
            PrintTime = "",
            Cutouts = Arr(e, "cutouts").Select(c => new Cutout
            {
                Face = Str(c, "face", "side"), Shape = Str(c, "shape", "rect"), Label = Str(c, "label", ""),
                Size = c.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Array
                    ? sz.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null,
                D = c.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
                Pos = c.TryGetProperty("pos", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => x.GetDouble()).ToArray() : new double[] { 0, 0 },
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
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
    private static double Dbl(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True);
}
