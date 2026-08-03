using Foundry.Core.Project;

namespace Foundry.Core.Cad;

/// <summary>
/// Derives a port cutout's FACE, POSITION and SIZE from where its component actually sits on the placed
/// board — instead of taking the model's word for it.
///
/// <para>
/// Cutout coordinates arrived straight from the generation JSON with nothing linking a cutout to the part
/// it exposes, so the USB hole landed wherever the model felt like putting it. That is the most expensive
/// class of error the enclosure can make: the case is perfectly manufacturable and simply wrong, and you
/// only find out after printing it.
/// </para>
///
/// <para>
/// The transform is fully determined, so this never guesses. When a cutout does not name a resolvable
/// part, or that part is not near a board edge, or its height is unknown for a side face, the model's
/// value is KEPT and an <c>unproven</c> finding says the position could not be verified.
/// </para>
///
/// Pure: numbers in, cutouts and findings out.
/// </summary>
public static class CutoutFit
{
    /// <summary>
    /// How close a part's courtyard must come to a board edge before a port on that edge is credible.
    /// A part in the middle of the board has no defensible face, so its cutout is left unproven.
    /// </summary>
    public const double EdgeProximityMm = 12.0;

    /// <summary>Clearance added around the part's cross-section so the port isn't a press fit.</summary>
    public const double PortClearanceMm = 0.6;

    /// <summary>The outcome for one cutout.</summary>
    public sealed record Result(Cutout Cutout, bool Derived, string? Reason);

    /// <summary>
    /// Derive every cutout that can be derived. Returns one <see cref="Result"/> per input cutout, in
    /// order, plus findings for the ones that could not be verified.
    /// </summary>
    public static (List<Result> Results, List<Finding> Findings) Derive(
        Enclosure enclosure, BoardPlacement board)
    {
        var results = new List<Result>();
        var unverified = new List<string>();

        foreach (var c in enclosure.Cutouts)
        {
            var r = DeriveOne(c, enclosure, board);
            results.Add(r);
            if (!r.Derived) unverified.Add($"{Label(c)} ({r.Reason})");
        }

        var findings = new List<Finding>();
        if (unverified.Count > 0)
            findings.Add(new Finding
            {
                Severity = "unproven", Code = "CUT-POS",
                Title = $"{unverified.Count} cutout position(s) not verified against the board",
                Description =
                    "These ports were placed from the design description rather than from where the part " +
                    "actually sits on the board, so they may not line up with anything: " +
                    string.Join("; ", unverified.Take(6)) +
                    (unverified.Count > 6 ? $" (+{unverified.Count - 6} more)" : "") + ".",
                Refs = enclosure.Cutouts.Where(c => string.IsNullOrEmpty(c.Ref)).Select(Label).Take(6).ToList(),
                Fix = "Name the component each port exposes",
            });

        return (results, findings);
    }

    /// <summary>
    /// How the enclosure's ports were positioned, for the UI to state plainly: "2 of 5 derived from the
    /// board". The header used to assert "derived from footprints" for every port, which was true for
    /// none of them before this class existed and is true for only some of them now.
    /// </summary>
    public static string SummariseSource(Enclosure enclosure, BoardPlacement? board)
    {
        var total = enclosure.Cutouts.Count;
        if (total == 0) return "none";
        if (board is null) return "positions from the design";

        var derived = Derive(enclosure, board).Results.Count(r => r.Derived);
        return derived == total ? "all derived from the board"
             : derived == 0 ? "positions from the design"
             : $"{derived} of {total} derived from the board";
    }

    private static string Label(Cutout c) =>
        !string.IsNullOrWhiteSpace(c.Label) ? c.Label : $"{c.Shape} on {c.Face}";

    private static Result DeriveOne(Cutout c, Enclosure enclosure, BoardPlacement board)
    {
        if (string.IsNullOrWhiteSpace(c.Ref))
            return new Result(c, false, "no component named");
        if (!board.Parts.TryGetValue(c.Ref!, out var part))
            return new Result(c, false, $"no placed part called {c.Ref}");

        var (face, gap) = NearestFace(part, board.Extent);
        if (gap > EdgeProximityMm)
            return new Result(c, false, $"{c.Ref} sits {gap:0.#} mm from the nearest edge");

        // "bottom"/"top" are legitimate author choices (a probe slot, a reset button) — honour the face
        // the design asked for when it is a Z face, and only derive the in-plane position.
        var authored = (c.Face ?? "").Trim().ToLowerInvariant();
        if (authored is "top" or "bottom")
            return new Result(WithPos(c, authored,
                part.XMm - board.Extent.WidthMm / 2, part.YMm - board.Extent.DepthMm / 2,
                part.WidthMm, part.DepthMm), true, null);

        if (!part.HeightKnown)
            return new Result(c, false, $"no height for {c.Ref}, so the port's height is unknown");

        // Vertical placement, in the case's own coordinate system: the base spans z 0..oz with its inner
        // floor at the wall thickness, and _cutout_solid measures a side-face offset from oz/2.
        var t = enclosure.Wall;
        var oz = enclosure.Inner[2] + t;
        var centreZ = t + board.StandoffMm + board.ThicknessMm + part.HeightMm / 2.0;
        var v = centreZ - oz / 2.0;

        // Horizontal axis differs per face: front/back run along board X, left/right along board Y.
        var u = face is "left" or "right"
            ? part.YMm - board.Extent.DepthMm / 2
            : part.XMm - board.Extent.WidthMm / 2;
        var across = face is "left" or "right" ? part.DepthMm : part.WidthMm;

        return new Result(WithPos(c, face, u, v, across, part.HeightMm), true, null);
    }

    private static Cutout WithPos(Cutout c, string face, double u, double v, double across, double up)
    {
        var derived = new Cutout
        {
            Face = face,
            Shape = c.Shape,
            Pos = new[] { Math.Round(u, 2), Math.Round(v, 2) },
            Label = c.Label,
            Ref = c.Ref,
        };
        if (c.Shape.Equals("circle", StringComparison.OrdinalIgnoreCase))
            derived.D = c.D ?? Math.Round(Math.Min(across, up) + PortClearanceMm, 2);
        else
            derived.Size = new[] { Math.Round(across + PortClearanceMm, 2), Math.Round(up + PortClearanceMm, 2) };
        return derived;
    }

    /// <summary>The board edge a part is closest to, and how far its courtyard stops short of it.</summary>
    internal static (string Face, double GapMm) NearestFace(PartPlacement p, BoardExtent board)
    {
        // Board +Y is the case's BACK, y=0 the FRONT; board x=0 is LEFT, x=W is RIGHT.
        var candidates = new (string Face, double Gap)[]
        {
            ("left",  p.XMm - p.WidthMm / 2),
            ("right", board.WidthMm - (p.XMm + p.WidthMm / 2)),
            ("front", p.YMm - p.DepthMm / 2),
            ("back",  board.DepthMm - (p.YMm + p.DepthMm / 2)),
        };
        var best = candidates.OrderBy(x => x.Gap).First();
        return (best.Face, Math.Max(0, best.Gap));
    }
}
