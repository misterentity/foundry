using System.Text.Json;

namespace Foundry.Core.Pcb;

/// <summary>Coarse board-edge intent for an edge-affinity group or component (connectors, antennas, RF).</summary>
public enum EdgeAffinity { None, Left, Right, Top, Bottom }

/// <summary>One functional cluster the AI proposes (e.g. "power", "mcu", "sensor-i2c", "connectors").</summary>
public sealed record PlacementGroup(
    string Id,
    IReadOnlyList<string> Members,
    EdgeAffinity Edge = EdgeAffinity.None);

/// <summary>Per-component placement intent. All fields optional; defaults make a sparse hint safe.</summary>
public sealed record PlacementHint(
    string Ref,
    string? Group = null,
    EdgeAffinity Edge = EdgeAffinity.None,
    string? NearRef = null,
    double Rotation = 0);

/// <summary>
/// The AI's placement proposal — functional grouping + relative/edge intent + a coarse region order.
/// Pure advice (never coordinates); <see cref="PcbPlacer"/> turns it into collision-free mm coordinates.
/// An empty plan = tidy grid (exact v2.2 behavior). The parser NEVER throws — garbage degrades to
/// <see cref="Empty"/>, mirroring the defensive style of <see cref="Generation.ProjectGenerator"/>.
/// </summary>
public sealed record PlacementPlan(
    IReadOnlyList<PlacementGroup> Groups,
    IReadOnlyList<PlacementHint> Hints,
    IReadOnlyList<string> RegionOrder)
{
    public static PlacementPlan Empty { get; } = new(
        Array.Empty<PlacementGroup>(), Array.Empty<PlacementHint>(), Array.Empty<string>());

    /// <summary>
    /// Tolerant parse of the AI's placement JSON. Unknown enum strings → <see cref="EdgeAffinity.None"/>;
    /// missing arrays → empty; non-numeric rotation → 0; any exception or null/empty input →
    /// <see cref="Empty"/>. Never throws — a malformed plan degrades to the tidy-grid default.
    /// </summary>
    public static PlacementPlan Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            var groups = Arr(root, "groups")
                .Where(g => g.ValueKind == JsonValueKind.Object)
                .Select(g => new PlacementGroup(
                    Str(g, "id", ""),
                    Arr(g, "members").Select(m => m.GetString() ?? "").Where(s => s.Length > 0).ToList(),
                    ParseEdge(Str(g, "edge", ""))))
                .Where(g => g.Id.Length > 0)
                .ToList();

            var hints = Arr(root, "hints")
                .Where(h => h.ValueKind == JsonValueKind.Object)
                .Select(h => new PlacementHint(
                    Str(h, "ref", ""),
                    NullIfEmpty(Str(h, "group", "")),
                    ParseEdge(Str(h, "edge", "")),
                    NullIfEmpty(Str(h, "near", "")),
                    Dbl(h, "rotation", 0)))
                .Where(h => h.Ref.Length > 0)
                .ToList();

            var regionOrder = Arr(root, "regionOrder")
                .Select(r => r.GetString() ?? "").Where(s => s.Length > 0).ToList();

            if (groups.Count == 0 && hints.Count == 0 && regionOrder.Count == 0) return Empty;
            return new PlacementPlan(groups, hints, regionOrder);
        }
        catch { return Empty; }
    }

    private static EdgeAffinity ParseEdge(string s) => s.Trim().ToLowerInvariant() switch
    {
        "left" => EdgeAffinity.Left, "right" => EdgeAffinity.Right,
        "top" => EdgeAffinity.Top, "bottom" => EdgeAffinity.Bottom,
        _ => EdgeAffinity.None,
    };

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Str(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static double Dbl(JsonElement e, string name, double fallback)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return fallback;
    }
}
