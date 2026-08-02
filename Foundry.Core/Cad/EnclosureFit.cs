using Foundry.Core.Project;

namespace Foundry.Core.Cad;

/// <summary>A part's real vertical extent about the PCB plane, taken from its KiCad 3D model.
/// <paramref name="AboveMm"/> is what must clear the lid; <paramref name="BelowMm"/> is the pin tail or
/// connector body under the board, which sets the minimum standoff.</summary>
public sealed record PartHeight(string LibId, double AboveMm, double BelowMm)
{
    public static PartHeight Unknown(string libId) => new(libId, double.NaN, double.NaN);
    public bool IsKnown => !double.IsNaN(AboveMm);
}

/// <summary>The placed board's outline footprint, in mm.</summary>
public sealed record BoardExtent(double WidthMm, double DepthMm);

/// <summary>
/// Deterministic mechanical fit between a PCB and the enclosure generated for it.
///
/// <para>
/// This is the one verification class in Foundry that is fully decidable. "Is this net driven?" needs
/// design INTENT that a netlist does not carry; "does the board fit in the box?" is geometry, and the
/// geometry is all present — board outline from the placer, courtyards and 3D heights from KiCad. So
/// unlike the electrical rules this never has to guess: it either proves the fit, or it names the exact
/// part whose height it could not obtain and reports UNPROVEN.
/// </para>
///
/// Pure: no KiCad, no sidecar, no I/O. Every number in, findings out.
/// </summary>
public static class EnclosureFit
{
    /// <summary>Clearance between the board edge and the inner wall, per side.</summary>
    public const double SideClearanceMm = 1.0;
    /// <summary>Air gap between the tallest component and the underside of the lid.</summary>
    public const double LidClearanceMm = 1.0;
    /// <summary>Standard PCB thickness assumed when the board doesn't state one.</summary>
    public const double PcbThicknessMm = 1.6;
    /// <summary>Shortest sensible screw boss — also the floor for "pins must clear the floor".</summary>
    public const double MinStandoffMm = 3.0;

    /// <summary>
    /// The board's footprint, derived from the placer's outline. The outline is a rectangle from the
    /// origin expressed as <c>[x1,y1,x2,y2]</c> segments, so the extent is its maximum corner.
    /// </summary>
    public static BoardExtent BoardExtentOf(IReadOnlyList<double[]> outlineSegmentsMm)
    {
        double w = 0, d = 0;
        foreach (var s in outlineSegmentsMm ?? Array.Empty<double[]>())
        {
            if (s is not { Length: >= 4 }) continue;
            w = Math.Max(w, Math.Max(s[0], s[2]));
            d = Math.Max(d, Math.Max(s[1], s[3]));
        }
        return new BoardExtent(Math.Round(w, 2), Math.Round(d, 2));
    }

    /// <summary>
    /// Run the whole mechanical check for a project: resolve footprints, place the board with the SAME
    /// deterministic placer the PCB path uses, measure the parts, and compare against the generated case.
    ///
    /// <para>
    /// Pure by default — <see cref="Pcb.PcbPlacer"/> and <see cref="Pcb.FootprintMap.CourtyardOf"/> need no
    /// KiCad, so fit is checkable offline. Pass <paramref name="realSizes"/> from
    /// <see cref="Pcb.PcbBuilder.MeasureAsync"/> and <paramref name="modelDir"/> from a located install to
    /// upgrade the approximations to measured geometry.
    /// </para>
    /// </summary>
    public static List<Finding> CheckProject(
        Project.Project project,
        IReadOnlyDictionary<string, (double WMm, double HMm)>? realSizes = null,
        string? modelDir = null)
    {
        var items = new List<Pcb.PcbPlacer.PlacedItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in project.Components)
        {
            if (!seen.Add(spec.Alias)) continue;
            var libId = Pcb.FootprintMap.Resolve(spec, Math.Max(1, spec.Pins.Count)).LibId;
            var size = realSizes is not null && realSizes.TryGetValue(libId, out var s)
                ? s
                : Pcb.FootprintMap.CourtyardOf(libId);
            items.Add(new Pcb.PcbPlacer.PlacedItem(spec.Alias, libId, size));
        }

        if (items.Count == 0) return new List<Finding>();
        // No enclosure was asked for — not every project has a case, and a missing one is not a defect.
        if (project.Enclosure.Inner is not { Length: >= 3 } inner || inner.Take(3).All(v => v <= 0))
            return new List<Finding>();

        var placement = Pcb.PcbPlacer.Place(items, Pcb.PlacementPlan.Empty);
        return Check(project.Enclosure, BoardExtentOf(placement.OutlineSegmentsMm), HeightsFor(project, modelDir));
    }

    /// <summary>
    /// Resolve a height for every component in the project, through the same footprint decision the PCB
    /// build makes (<see cref="Pcb.FootprintMap.Resolve"/>) so the case is measured against the parts that
    /// will actually be placed. A component whose footprint has no model resolves to an UNKNOWN height and
    /// surfaces as FIT-UNK rather than being assumed flat.
    /// </summary>
    public static IReadOnlyList<PartHeight> HeightsFor(Project.Project project, string? modelDir)
    {
        var heights = new List<PartHeight>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in project.Components)
        {
            var libId = Pcb.FootprintMap.Resolve(spec, Math.Max(1, spec.Pins.Count)).LibId;
            if (!seen.Add(libId)) continue;
            heights.Add(StepHeights.For(libId, modelDir));
        }
        return heights;
    }

    /// <summary>The smallest inner cavity [L, W, H] that will actually hold this board and its parts.</summary>
    public static double[] MinimumInner(BoardExtent board, IReadOnlyList<PartHeight> parts)
    {
        var known = parts.Where(p => p.IsKnown).ToList();
        var tallest = known.Count == 0 ? 0.0 : known.Max(p => p.AboveMm);
        var deepest = known.Count == 0 ? 0.0 : known.Max(p => p.BelowMm);
        var standoff = Math.Max(MinStandoffMm, deepest);

        return new[]
        {
            Math.Round(board.WidthMm + 2 * SideClearanceMm, 2),
            Math.Round(board.DepthMm + 2 * SideClearanceMm, 2),
            Math.Round(standoff + PcbThicknessMm + tallest + LidClearanceMm, 2),
        };
    }

    /// <summary>
    /// Check a generated enclosure against the board it is supposed to hold. Findings use the same
    /// <see cref="Finding"/> shape as the electrical rules so the report card renders them unchanged.
    /// </summary>
    public static List<Finding> Check(Enclosure enclosure, BoardExtent board, IReadOnlyList<PartHeight> parts)
    {
        var findings = new List<Finding>();
        if (enclosure.Inner is not { Length: >= 3 })
        {
            findings.Add(new Finding
            {
                Severity = "fail", Code = "FIT-DIM",
                Title = "Enclosure has no inner dimensions",
                Description = "The enclosure carries no [L, W, H], so nothing about fit can be checked.",
                Fix = "Regenerate the enclosure",
            });
            return findings;
        }

        double innerL = enclosure.Inner[0], innerW = enclosure.Inner[1], innerH = enclosure.Inner[2];
        var min = MinimumInner(board, parts);

        // ---- X/Y: does the board physically go in? ----
        var needL = min[0];
        var needW = min[1];
        if (innerL < needL || innerW < needW)
            findings.Add(new Finding
            {
                Severity = "fail", Code = "FIT-XY",
                Title = "The board does not fit inside the enclosure",
                Description =
                    $"The PCB is {board.WidthMm:0.#} × {board.DepthMm:0.#} mm and needs an inner floor of at least " +
                    $"{needL:0.#} × {needW:0.#} mm (allowing {SideClearanceMm:0.#} mm per side), but the enclosure is " +
                    $"{innerL:0.#} × {innerW:0.#} mm.",
                Refs = new() { "enclosure.inner" },
                Fix = "Resize the enclosure to the board",
            });

        // ---- Z: does the tallest part clear the lid? ----
        var unknown = parts.Where(p => !p.IsKnown).Select(p => p.LibId).Distinct().ToList();
        var known = parts.Where(p => p.IsKnown).ToList();
        if (known.Count > 0)
        {
            var tallest = known.OrderByDescending(p => p.AboveMm).First();
            var deepest = known.OrderByDescending(p => p.BelowMm).First();
            var standoff = Math.Max(MinStandoffMm, deepest.BelowMm);
            var stack = standoff + PcbThicknessMm + tallest.AboveMm + LidClearanceMm;

            if (innerH < stack)
                findings.Add(new Finding
                {
                    Severity = "fail", Code = "FIT-Z",
                    Title = $"{tallest.LibId} is too tall for the lid to close",
                    Description =
                        $"Stack-up is {standoff:0.#} mm standoff + {PcbThicknessMm:0.#} mm PCB + " +
                        $"{tallest.AboveMm:0.#} mm for the tallest part + {LidClearanceMm:0.#} mm clearance = " +
                        $"{stack:0.#} mm, but the cavity is only {innerH:0.#} mm deep.",
                    Refs = new() { tallest.LibId },
                    Fix = "Deepen the enclosure",
                });

            if (deepest.BelowMm > MinStandoffMm)
                findings.Add(new Finding
                {
                    Severity = "warn", Code = "FIT-UNDER",
                    Title = $"{deepest.LibId} protrudes {deepest.BelowMm:0.#} mm below the board",
                    Description =
                        $"Its pins or body sit {deepest.BelowMm:0.#} mm under the PCB, so the standoffs must be at " +
                        $"least that tall or the part will foul the floor. Default bosses are {MinStandoffMm:0.#} mm.",
                    Refs = new() { deepest.LibId },
                    Fix = "Raise the standoffs",
                });
        }

        // ---- honest refusal for anything we could not measure ----
        if (unknown.Count > 0)
            findings.Add(new Finding
            {
                Severity = "unproven", Code = "FIT-UNK",
                Title = $"No height data for {unknown.Count} part(s)",
                Description =
                    "These parts have no 3D model, so their height above the board is unknown and the lid " +
                    $"clearance cannot be proven: {string.Join(", ", unknown.Take(6))}" +
                    (unknown.Count > 6 ? $" (+{unknown.Count - 6} more)" : "") + ".",
                Refs = unknown.Take(6).ToList(),
                Fix = "Supply a height for these parts",
            });

        return findings;
    }
}
