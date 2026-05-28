using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Core.Fabrication;
using Foundry.Core.Kb;

namespace Foundry.Core.Pcb;

/// <summary>One component in the build job: a ref, its footprint lib id, grid position, and pad→net map.</summary>
public sealed record PcbJobComponent(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("footprint")] string Footprint,
    [property: JsonPropertyName("x_mm")] double XMm,
    [property: JsonPropertyName("y_mm")] double YMm,
    [property: JsonPropertyName("rot")] double Rot,
    [property: JsonPropertyName("padNets")] IReadOnlyDictionary<string, string> PadNets);

/// <summary>One net (name only — pad membership lives on each component's <see cref="PcbJobComponent.PadNets"/>).</summary>
public sealed record PcbJobNet([property: JsonPropertyName("name")] string Name);

/// <summary>
/// The JSON job document handed to <c>build_board.py</c> (spec §C) — built purely from a <see cref="Project"/>:
/// nets come straight from <see cref="KiCadNetlist.Nets"/> (no recompute), footprints from
/// <see cref="FootprintMap"/>, positions from a simple grid. Serializes 1:1 to the shape the script reads.
/// </summary>
public sealed record PcbJob(
    [property: JsonPropertyName("outPath")] string OutPath,
    [property: JsonPropertyName("footprintDirs")] IReadOnlyList<string> FootprintDirs,
    [property: JsonPropertyName("outlineSegments_mm")] IReadOnlyList<double[]> OutlineSegmentsMm,
    [property: JsonPropertyName("nets")] IReadOnlyList<PcbJobNet> Nets,
    [property: JsonPropertyName("components")] IReadOnlyList<PcbJobComponent> Components)
{
    /// <summary>Diagnostics surfaced while building the job (unresolved nodes, generic-footprint fallbacks).</summary>
    [JsonIgnore] public IReadOnlyList<PcbDiagnostic> Diagnostics { get; init; } = Array.Empty<PcbDiagnostic>();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// Build the job from a project. Reuses <see cref="KiCadNetlist.Nets"/> for nets, <see cref="FootprintMap"/>
    /// for footprints, and a left→right / wrapped-row grid for placement. Validates that every net node
    /// resolves to a pad in its component, emitting a <see cref="PcbDiagnostic"/> rather than mis-wiring.
    /// Pure — no KiCad needed. <paramref name="footprintDirs"/> is the located KiCad footprint dir(s).
    /// </summary>
    public static PcbJob Build(Project.Project project, string outPath, IReadOnlyList<string> footprintDirs,
        PlacementPlan? plan = null)
    {
        var diags = new List<PcbDiagnostic>();
        var nets = KiCadNetlist.Nets(project);

        // (endpoint, netName) pairs grouped by component ref, so PadNets can be built per component.
        var endpointNets = nets
            .SelectMany(n => n.Nodes.Select(ep => (Endpoint: ep, Net: n.Name)))
            .ToList();

        // Every ref that appears in the netlist OR as a declared component.
        var refs = endpointNets.Select(e => FootprintMap.RefOf(e.Endpoint))
            .Concat(project.Components.Select(c => c.Alias))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Resolve footprint + padNets per ref (identical to v2.2); positions come from the placer.
        var choiceByRef = new Dictionary<string, FootprintMap.FootprintChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in refs)
        {
            var spec = SpecFor(project, alias);
            var padNets = FootprintMap.PadNets(alias, endpointNets);

            // pin count: prefer KB pin table, else the count of distinct pads seen on the net.
            int pinCount = Math.Max(spec.Pins.Count, padNets.Count);
            if (pinCount == 0) pinCount = 1;

            var choice = FootprintMap.Resolve(spec, pinCount);
            if (choice.IsFallback)
                diags.Add(PcbDiagnostic.Warn(choice.Reason));
            choiceByRef[alias] = choice;
        }

        // Deterministic placement: the AI plan (if any) is advice; PcbPlacer owns the coordinates and
        // guarantees no overlap. A null/Empty plan degrades to a tidy grid (identical in spirit to v2.2).
        var items = refs.Select(alias => new PcbPlacer.PlacedItem(
            alias, choiceByRef[alias].LibId, FootprintMap.CourtyardOf(choiceByRef[alias].LibId))).ToList();
        var placement = PcbPlacer.Place(items, plan ?? PlacementPlan.Empty);

        var components = refs.Select(alias =>
        {
            var pos = placement[alias];
            return new PcbJobComponent(alias, choiceByRef[alias].LibId, pos.XMm, pos.YMm, pos.Rot,
                FootprintMap.PadNets(alias, endpointNets));
        }).ToList();

        // Validate every net node resolves to a pad in its component.
        var padNetsByRef = components.ToDictionary(c => c.Ref, c => c.PadNets, StringComparer.OrdinalIgnoreCase);
        foreach (var (ep, netName) in endpointNets)
        {
            var r = FootprintMap.RefOf(ep);
            var pad = FootprintMap.PinOf(ep);
            if (!padNetsByRef.TryGetValue(r, out var pn) || !pn.ContainsKey(pad))
                diags.Add(PcbDiagnostic.Error($"net {netName} node {ep}: no pad '{pad}' on {r}."));
        }

        var jobNets = nets.Select(n => new PcbJobNet(n.Name)).ToList();
        return new PcbJob(outPath, footprintDirs, placement.OutlineSegmentsMm, jobNets, components) { Diagnostics = diags };
    }

    /// <summary>
    /// The KB spec for an alias, or a synthesized minimal spec (so subsystem-only refs still resolve to a
    /// footprint). The synthesized spec carries the alias name so keyword heuristics can still fire.
    /// </summary>
    private static ComponentSpec SpecFor(Project.Project project, string alias)
    {
        var spec = project.Components.FirstOrDefault(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (spec is not null) return spec;

        var name = project.Subsystems.FirstOrDefault(s => s.Name.StartsWith(alias, StringComparison.OrdinalIgnoreCase))?.Name
                   ?? alias;
        return new ComponentSpec { Ref = alias, Alias = alias, Name = name };
    }
}
