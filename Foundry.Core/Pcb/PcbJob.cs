using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Core.Fabrication;
using Foundry.Core.Kb;

namespace Foundry.Core.Pcb;

/// <summary>One pin→net assignment for a component, in netlist order so the build script can fall back to
/// ordinal pad assignment when the footprint's pad names don't match the netlist pin names.</summary>
public sealed record PcbPadNet(
    [property: JsonPropertyName("pin")] string Pin,
    [property: JsonPropertyName("net")] string Net);

/// <summary>One component in the build job: a ref, its footprint lib id, grid position, and pad→net map.
/// <see cref="PadNets"/> is the name-keyed map (v2.2); <see cref="PadNetList"/> is the same data ORDERED,
/// which lets build_board.py assign by pad-name match first, then by ordinal position — so generic
/// fallback headers (pads "1".."N") still get every net even though the netlist uses pin names.</summary>
public sealed record PcbJobComponent(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("footprint")] string Footprint,
    [property: JsonPropertyName("x_mm")] double XMm,
    [property: JsonPropertyName("y_mm")] double YMm,
    [property: JsonPropertyName("rot")] double Rot,
    [property: JsonPropertyName("padNets")] IReadOnlyDictionary<string, string> PadNets,
    [property: JsonPropertyName("padNetList")] IReadOnlyList<PcbPadNet> PadNetList,
    // True when FootprintMap couldn't resolve a real footprint and dropped in a generic placeholder header.
    // build_board.py only ordinal-maps logical pins onto a placeholder; a resolved real footprint addressed
    // by a logical name with no pad match is left UNMAPPED (connectivity unverified) rather than mis-wired.
    [property: JsonPropertyName("isFallback")] bool IsFallback = false);

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
    /// <param name="realSizes">Optional measured courtyard sizes (lib id → W×H mm) from KiCad
    /// (<see cref="PcbBuilder.MeasureAsync"/>). When present, preferred over <see cref="FootprintMap.CourtyardOf"/>
    /// per lib id so the placer packs using true geometry. Null/missing entries fall back to the offline
    /// approximation, keeping the no-KiCad path and existing tests deterministic.</param>
    public static PcbJob Build(Project.Project project, string outPath, IReadOnlyList<string> footprintDirs,
        PlacementPlan? plan = null, double marginMm = 5.0, double gapMm = 1.5,
        IReadOnlyDictionary<string, (double WMm, double HMm)>? realSizes = null, string? symbolDir = null)
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
        // Prefer the REAL measured courtyard (KiCad) over the offline approximation, per lib id.
        (double, double) SizeOf(string libId) =>
            realSizes is not null && realSizes.TryGetValue(libId, out var s) ? s : FootprintMap.CourtyardOf(libId);

        var items = refs.Select(alias => new PcbPlacer.PlacedItem(
            alias, choiceByRef[alias].LibId, SizeOf(choiceByRef[alias].LibId))).ToList();
        var placement = PcbPlacer.Place(items, plan ?? PlacementPlan.Empty, marginMm, gapMm);

        var components = refs.Select(alias =>
        {
            var pos = placement[alias];
            var libId = choiceByRef[alias].LibId;
            // A bare chip in a GENERIC package is identified by the PART (ChipCatalog), not the footprint.
            var chip = ChipCatalog.Match(SpecFor(project, alias).Name);
            // Translate logical MCU pins (e.g. ESP32 GPIO34) to the footprint's real pad (6). Resolution order:
            // curated McuPinMap (fast, KiCad-free, chip-specific aliases) → SymbolPinMap by FOOTPRINT (part-
            // specific module footprints) → SymbolPinMap by PART identity (ChipCatalog, for chips in generic
            // packages) → keep the logical name, which falls through to the fail-closed gate in build_board.py
            // (refused), never ordinal-guessed.
            var padNetList = FootprintMap.PadNetList(alias, endpointNets)
                .Select(pn => new PcbPadNet(
                    McuPinMap.ResolvePad(libId, pn.Pin)
                        ?? SymbolPinMap.ResolvePad(libId, pn.Pin, symbolDir)
                        ?? (chip is not null ? SymbolPinMap.ResolvePadBySymbol(chip.SymbolLib, chip.SymbolName, pn.Pin, symbolDir) : null)
                        ?? pn.Pin,
                    pn.Net))
                .ToList();
            return new PcbJobComponent(alias, libId, pos.XMm, pos.YMm, pos.Rot,
                FootprintMap.PadNets(alias, endpointNets), padNetList,
                choiceByRef[alias].IsFallback);
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
    /// The distinct footprint lib ids the job will use for <paramref name="project"/> — the same
    /// resolve pass <see cref="Build"/> runs, exposed so <see cref="PcbBuilder.MeasureAsync"/> can ask
    /// KiCad to measure exactly those footprints before placement. Pure; order is deterministic.
    /// </summary>
    public static IReadOnlyList<string> ResolvedLibIds(Project.Project project)
    {
        var nets = KiCadNetlist.Nets(project);
        var endpointNets = nets
            .SelectMany(n => n.Nodes.Select(ep => (Endpoint: ep, Net: n.Name)))
            .ToList();

        var refs = endpointNets.Select(e => FootprintMap.RefOf(e.Endpoint))
            .Concat(project.Components.Select(c => c.Alias))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var libIds = new List<string>();
        foreach (var alias in refs)
        {
            var spec = SpecFor(project, alias);
            int pinCount = Math.Max(spec.Pins.Count, FootprintMap.PadNets(alias, endpointNets).Count);
            if (pinCount == 0) pinCount = 1;
            libIds.Add(FootprintMap.Resolve(spec, pinCount).LibId);
        }
        return libIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
