using Foundry.Core.Ai;
using Foundry.Core.Diagnostics;
using Foundry.Core.Fabrication;
using Foundry.Core.Kb;

namespace Foundry.Core.Pcb;

/// <summary>
/// The AI placement pass (Track B v2.3) — mirrors <see cref="Generation.ProjectGenerator"/>: one
/// structured Claude call, defensive parse, offline fallback. The model is fenced to PLACEMENT INTENT
/// only (functional groups + edge/near hints, never coordinates); the deterministic <see cref="PcbPlacer"/>
/// turns the plan into collision-free positions. No key / any failure / unparseable reply →
/// <see cref="PlacementPlan.Empty"/> (tidy-grid default). NEVER throws — placement never blocks a board.
/// </summary>
public sealed class PcbPlanner
{
    private readonly IAnthropicClient _ai;
    private readonly string _model;

    public PcbPlanner(IAnthropicClient ai, string? model = null)
    {
        _ai = ai;
        _model = string.IsNullOrWhiteSpace(model) ? ModelCatalog.DefaultModelId : model;
    }

    private const string SystemPrompt = """
You are a senior PCB layout engineer. You are given a parts list and a netlist. Propose a PLACEMENT
PLAN as ONE JSON object — functional groups and relative/edge INTENT only. You do NOT output
coordinates; a deterministic placer turns your plan into exact positions and guarantees no overlaps.

Apply these layout principles:
- Group parts by FUNCTION: power/regulation, the MCU and its support, each sensor/peripheral block
  (name I2C/SPI blocks by bus), and connectors. One group per function.
- Put every decoupling/bypass capacitor in the SAME group as the IC it serves and set its
  "near" to that IC's ref — caps must sit directly against their IC's power pin.
- Put CONNECTORS, USB, power input, and ANTENNAS/RF at a BOARD EDGE (set "edge"): connectors on a
  side edge, antenna/RF on the nearest edge (prefer top), pointing outward.
- Keep high-speed / bus nets (I2C, SPI, crystal) short: place those parts adjacent within one group.
- Keep noisy power/switching away from sensitive analog/RF: order regions so power is at one end and
  RF/analog at the other.
- Every component ref in the parts list must appear in exactly one group.

Return ONLY this JSON (no prose, no fences). All fields optional except group "id" and "members":
{"groups":[{"id":"power","members":["U2","C1"],"edge":"none"}],
 "hints":[{"ref":"C3","near":"U1"},{"ref":"J1","edge":"left"}],
 "regionOrder":["power","mcu","sensor-i2c","connectors"]}
edge ∈ none|left|right|top|bottom. rotation ∈ 0|90|180|270 (optional).
""";

    /// <summary>
    /// Ask the model for a <see cref="PlacementPlan"/> for this design. No key / any failure /
    /// unparseable reply → <see cref="PlacementPlan.Empty"/>. NEVER throws.
    /// </summary>
    public async Task<PlacementPlan> PlanAsync(Project.Project project, CancellationToken ct = default)
    {
        if (!_ai.HasKey) return PlacementPlan.Empty;
        try
        {
            var user = BuildUserPrompt(project);
            var raw = await _ai.CompleteAsync(SystemPrompt, user, _model, ct);
            var plan = PlacementPlan.Parse(ExtractJson(raw));
            AppLog.Info("pcb", $"placement plan · {plan.Groups.Count} groups · {plan.Hints.Count} hints");
            return plan;
        }
        catch (Exception ex)
        {
            AppLog.Warn("pcb", $"placement plan failed: {ex.Message} — using tidy-grid default");
            return PlacementPlan.Empty;
        }
    }

    /// <summary>
    /// Compact parts (ref, name, resolved footprint, pin count) + netlist (from -> to [net]) summary,
    /// reusing the <see cref="Generation.ProjectGenerator.EnrichFirmwareAsync"/> summarization shape so the
    /// model reasons about real groupings and which caps decouple which ICs.
    /// </summary>
    private static string BuildUserPrompt(Project.Project project)
    {
        var nets = KiCadNetlist.Nets(project);
        var endpointNets = nets.SelectMany(n => n.Nodes.Select(ep => (Endpoint: ep, Net: n.Name))).ToList();

        var refs = endpointNets.Select(e => FootprintMap.RefOf(e.Endpoint))
            .Concat(project.Components.Select(c => c.Alias))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string PartLine(string alias)
        {
            var spec = project.Components.FirstOrDefault(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
            var name = spec?.Name
                ?? project.Subsystems.FirstOrDefault(s => s.Name.StartsWith(alias, StringComparison.OrdinalIgnoreCase))?.Name
                ?? alias;
            var padNets = FootprintMap.PadNets(alias, endpointNets);
            int pinCount = Math.Max(spec?.Pins.Count ?? 0, padNets.Count);
            if (pinCount == 0) pinCount = 1;
            var fp = FootprintMap.Resolve(spec ?? new ComponentSpec { Ref = alias, Alias = alias, Name = name }, pinCount).LibId;
            return $"- {alias} ({name}): footprint {fp}, {pinCount} pins";
        }

        var parts = string.Join("\n", refs.Select(PartLine));
        var netlist = string.Join("\n", project.Connections
            .Where(c => c.From.Length > 0 && c.To.Length > 0)
            .Select(c => $"- {c.From} -> {c.To} [{c.Net}]"));

        return $"Device: {project.Title}\n\nParts:\n{parts}\n\nNetlist:\n{netlist}\n\n" +
               "Propose the placement plan as the JSON contract.";
    }

    /// <summary>Tolerate accidental markdown fences / leading prose by extracting the outermost JSON object.</summary>
    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw[start..(end + 1)];
    }
}
