using System.Globalization;
using System.Text.RegularExpressions;

namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Pure, fake-able derivation of a board's width/height (mm) from its outline — feeds
/// <see cref="FabOrderSpec"/>, mirroring how <see cref="FabFileSet.Validate"/> is a pure check. The best
/// source is the <c>.kicad_pcb</c> text: the builder writes the outline as <c>gr_line</c>/segments (and
/// possibly <c>gr_rect</c>) on layer <c>Edge.Cuts</c>; we collect every <c>(start …)/(end …)/(xy …)</c> on
/// those shapes and take the bounding box. No KiCad process, no filesystem — tests feed a synthetic board
/// string. The <c>.gm1</c> outline-gerber fallback is coarser; use only when the source board isn't at hand.
/// </summary>
public static class BoardDimensions
{
    /// <summary>A sane default footprint (a small 2-layer board) when the outline can't be read.</summary>
    public static readonly (double WidthMm, double HeightMm) Default = (50.0, 50.0);

    // (gr_line ... (layer "Edge.Cuts") ...) — KiCad emits each shape as one s-expression block. We match a
    // shape that mentions Edge.Cuts and pull the coordinate points out of it.
    private static readonly Regex EdgeShape = new(
        @"\(gr_(?:line|rect|poly|arc|curve)\b(?<body>.*?)\(layer\s+""?Edge\.Cuts""?\)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The same shapes can also place (layer "Edge.Cuts") before the points; catch the reverse order too.
    private static readonly Regex EdgeShapeAlt = new(
        @"\(gr_(?:line|rect|poly|arc|curve)\b[^()]*?\(layer\s+""?Edge\.Cuts""?\)(?<body>.*?)(?=\(gr_|\Z)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // (start X Y) (end X Y) (xy X Y) (center X Y) (mid X Y) — any coordinate pair inside a shape body.
    private static readonly Regex Point = new(
        @"\((?:start|end|xy|center|mid)\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse the bounding box of all <c>Edge.Cuts</c> shapes in a <c>.kicad_pcb</c> board string. Returns
    /// <see cref="Default"/> when nothing usable is found. Pure — never touches the filesystem, never throws.
    /// </summary>
    public static (double WidthMm, double HeightMm) FromKicadPcb(string? boardText)
    {
        if (string.IsNullOrWhiteSpace(boardText)) return Default;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (Match shape in EdgeShape.Matches(boardText))
            any |= Accumulate(shape.Groups["body"].Value, ref minX, ref minY, ref maxX, ref maxY);

        if (!any)
            foreach (Match shape in EdgeShapeAlt.Matches(boardText))
                any |= Accumulate(shape.Groups["body"].Value, ref minX, ref minY, ref maxX, ref maxY);

        if (!any) return Default;

        var w = Math.Round(maxX - minX, 2);
        var h = Math.Round(maxY - minY, 2);
        if (w <= 0 || h <= 0) return Default;
        return (w, h);
    }

    private static bool Accumulate(string body, ref double minX, ref double minY, ref double maxX, ref double maxY)
    {
        bool any = false;
        foreach (Match p in Point.Matches(body))
        {
            if (!double.TryParse(p.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!double.TryParse(p.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            any = true;
        }
        return any;
    }
}
