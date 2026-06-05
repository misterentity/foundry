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

    private const string RevisePromptSuffix = """


--- REVISION MODE (DRC fix loop) ---
The placement you proposed produced a board that FAILED DRC. The deterministic placer/router still own
every coordinate — you only adjust the PLACEMENT PLAN (groups/edge/near/regionOrder, NEVER coordinates).
You are given the CURRENT plan and the violating component refs + their violation classes. Revise the
plan to relieve them:
- clearance / courtyards_overlap / hole_clearance around some refs → SPREAD those parts: move them into
  separate groups or to the ends of the region order so they aren't packed tightly together.
- copper_edge_clearance → pull the named refs away from the edge (drop their edge affinity unless they are
  connectors/antennas that must stay at an edge).
- unconnected / dangling → loosen a dense block: split a crowded group, or reorder regions so the
  congested nets have more routing room.
Keep everything the violations don't touch. Return ONLY the revised JSON in the SAME contract (groups /
hints / regionOrder). No prose, no fences, no coordinates.
""";

    /// <summary>
    /// Fenced AI plan revision for the v2.5 DRC fix loop: feed the violating refs + their classes and the
    /// current <see cref="PlacementPlan"/> back to the model and ask for a revised plan (advice only — same
    /// JSON contract, never coordinates). No key / any failure / unparseable reply / empty parse ⇒ the
    /// <paramref name="currentPlan"/> is kept unchanged (degrade to deterministic-only). NEVER throws.
    /// </summary>
    public async Task<PlacementPlan> RevisePlanAsync(Project.Project project, PlacementPlan currentPlan,
        IReadOnlyList<DrcViolation> violations, CancellationToken ct = default)
    {
        currentPlan ??= PlacementPlan.Empty;
        if (!_ai.HasKey) return currentPlan;
        try
        {
            var user = BuildRevisePrompt(project, currentPlan, violations);
            var raw = await _ai.CompleteAsync(SystemPrompt + RevisePromptSuffix, user, _model, ct);
            var revised = PlacementPlan.Parse(ExtractJson(raw));
            if (ReferenceEquals(revised, PlacementPlan.Empty))
            {
                AppLog.Warn("pcb", "plan revision returned empty/garbage — keeping current plan");
                return currentPlan;
            }
            AppLog.Info("pcb", $"plan revised · {revised.Groups.Count} groups · {revised.Hints.Count} hints");
            return revised;
        }
        catch (Exception ex)
        {
            AppLog.Warn("pcb", $"plan revision failed: {ex.Message} — keeping current plan");
            return currentPlan;
        }
    }

    /// <summary>Current plan JSON + a violation digest (refs grouped by class) for the revision call.</summary>
    private static string BuildRevisePrompt(Project.Project project, PlacementPlan plan, IReadOnlyList<DrcViolation> violations)
    {
        var parts = BuildUserPrompt(project);
        var planJson = PlanToJson(plan);

        var byClass = (violations ?? Array.Empty<DrcViolation>())
            .Where(v => !v.Excluded)
            .GroupBy(v => v.Type, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var refs = g.SelectMany(v => v.Items.Select(RefFromItem))
                    .Where(r => r.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(r => r, StringComparer.OrdinalIgnoreCase);
                var refList = string.Join(", ", refs);
                return $"- {g.Key} ({g.Count()}): {(refList.Length > 0 ? refList : "(no specific refs)")}";
            });
        var digest = string.Join("\n", byClass);
        if (digest.Length == 0) digest = "(no violations supplied)";

        return $"{parts}\n\nCurrent placement plan (JSON):\n{planJson}\n\nDRC violations to relieve:\n{digest}";
    }

    /// <summary>Pull a component ref out of a DRC item description like "Pad 1 of C3" / "Footprint U1".</summary>
    private static string RefFromItem(DrcItem item)
    {
        var desc = item.Description ?? "";
        var tokens = desc.Split(new[] { ' ', ',', '(', ')', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        // a ref looks like one-or-more letters followed by one-or-more digits (R1, U12, ANT1, J2).
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Length >= 2 && char.IsLetter(t[0]) && char.IsDigit(t[^1]) &&
                t.All(c => char.IsLetterOrDigit(c)))
                return t;
        }
        return "";
    }

    /// <summary>Serialize a <see cref="PlacementPlan"/> back to the AI-facing JSON contract.</summary>
    private static string PlanToJson(PlacementPlan plan)
    {
        string Edge(EdgeAffinity e) => e switch
        {
            EdgeAffinity.Left => "left", EdgeAffinity.Right => "right",
            EdgeAffinity.Top => "top", EdgeAffinity.Bottom => "bottom", _ => "none",
        };
        var dto = new
        {
            groups = plan.Groups.Select(g => new { id = g.Id, members = g.Members, edge = Edge(g.Edge) }),
            hints = plan.Hints.Select(h => new { @ref = h.Ref, group = h.Group, edge = Edge(h.Edge), near = h.NearRef, rotation = h.Rotation }),
            regionOrder = plan.RegionOrder,
        };
        return System.Text.Json.JsonSerializer.Serialize(dto);
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

    /// <summary>Tolerate accidental markdown fences / leading prose by extracting the outermost JSON object.
    /// Shared, hardened implementation (also validates the slice parses). PlacementPlan.Parse(null) and
    /// Parse(garbage) both degrade to Empty, so adding slice validation here is behavior-equivalent.</summary>
    private static string? ExtractJson(string raw) => Generation.JsonText.Extract(raw);
}
