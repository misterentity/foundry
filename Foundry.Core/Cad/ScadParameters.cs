using System.Globalization;
using System.Text.RegularExpressions;

namespace Foundry.Core.Cad;

public sealed class ScadParam
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; } = 0.1;
    public string DisplayName => Name.Replace('_', ' ');
}

/// <summary>
/// Extracts top-level numeric parameters from an OpenSCAD script and patches their values back in
/// (PRD v2 Phase D — CADAM-style parametric sliders).
/// </summary>
public static class ScadParameters
{
    // top-level `name = number;` at column 0, optional trailing comment
    private static readonly Regex TopLevel = new(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<val>-?\d+(?:\.\d+)?)\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // codename → friendly axis (we don't expose every var; only the obvious geometry knobs by default)
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    { "$fn", "$fs", "$fa", "epsilon", "eps", "tol" };

    public static List<ScadParam> Parse(string scad, int maxItems = 12)
    {
        var list = new List<ScadParam>();
        if (string.IsNullOrWhiteSpace(scad)) return list;
        foreach (Match m in TopLevel.Matches(scad))
        {
            var name = m.Groups["name"].Value;
            if (Hidden.Contains(name)) continue;
            if (!double.TryParse(m.Groups["val"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            if (list.Any(p => p.Name == name)) continue;   // first declaration wins

            var (min, max, step) = Range(name, v);
            list.Add(new ScadParam { Name = name, Value = v, Min = min, Max = max, Step = step });
            if (list.Count >= maxItems) break;
        }
        return list;
    }

    /// <summary>Replace the first top-level declaration of <paramref name="name"/> with <paramref name="newValue"/>.</summary>
    public static string Patch(string scad, string name, double newValue)
    {
        if (string.IsNullOrEmpty(scad) || string.IsNullOrEmpty(name)) return scad;
        var re = new Regex(
            $@"^(?<lhs>{Regex.Escape(name)}\s*=\s*)(-?\d+(?:\.\d+)?)(?<rhs>\s*;)",
            RegexOptions.Multiline);
        var rendered = newValue.ToString("0.###", CultureInfo.InvariantCulture);
        return re.Replace(scad, m => m.Groups["lhs"].Value + rendered + m.Groups["rhs"].Value, 1);
    }

    /// <summary>Heuristic range/step for a parameter, scaled by its current value and what it likely is.</summary>
    private static (double min, double max, double step) Range(string name, double v)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("count") || n.Contains("segments") || n.Contains("fn") || n.Contains("screws")
            || n.Contains("tabs") || n.Contains("vents") || n.Contains("holes") || n.Contains("sides"))
            return (Math.Max(1, v / 2), Math.Max(v * 2, v + 8), 1);
        if (n.Contains("thickness") || n.Contains("wall") || n.Contains("lid") || n.Contains("gap") || n.Contains("clearance"))
            return (Math.Max(0.4, v * 0.4), Math.Max(v * 2.5, v + 4), 0.1);
        if (n.Contains("radius") || n.Contains("fillet") || n.Contains("chamfer"))
            return (0, Math.Max(v * 2.5, v + 5), 0.1);
        // generic: scale around v, default 50%–200%
        if (v == 0) return (0, 50, 0.1);
        var lo = Math.Max(v >= 0 ? 0 : v * 2, v - Math.Abs(v));
        var hi = v + Math.Abs(v);
        if (Math.Abs(v) < 5) return (Math.Max(0, v - 5), v + 10, 0.1);
        if (Math.Abs(v) > 50) return (lo, hi, 1);
        return (lo, hi, 0.5);
    }
}
