using System.Text.Json;

namespace Foundry.Core.Pcb;

/// <summary>One geometric item referenced by a DRC violation (a pad, track, via, …) with its position.</summary>
public sealed record DrcItem(string Uuid, string Description, double X, double Y);

/// <summary>
/// One DRC violation or unconnected-item entry (same JSON shape per the KiCad <c>drc.v1.json</c> schema):
/// a <see cref="Type"/> string ("clearance", "copper_edge_clearance", "track_dangling", …), a
/// <see cref="Severity"/> ("error"|"warning"), a human <see cref="Description"/>, the <see cref="Excluded"/>
/// suppression flag, and the geometric <see cref="Items"/> involved.
/// </summary>
public sealed record DrcViolation(
    string Type,
    string Severity,
    string Description,
    bool Excluded,
    IReadOnlyList<DrcItem> Items)
{
    /// <summary>The first item's position, if any — a convenient single "where" for the UI/remediation.</summary>
    public (double X, double Y)? Location => Items.Count > 0 ? (Items[0].X, Items[0].Y) : null;
}

/// <summary>
/// Result of running <c>kicad-cli pcb drc</c> on a routed <c>.kicad_pcb</c> (Track B v2.5) — the
/// deterministic GATE. Mirrors <see cref="RouteResult"/>'s Installed/Ok/Summary shape. <see cref="Clean"/>
/// is the gate bit: <see cref="Ok"/> and no gated errors and no unconnected nets. Counts exclude
/// <c>excluded:true</c> entries. <see cref="Parse"/> reads the report FILE text + the cli exit code
/// (exit 0 ⇒ clean, exit 5 ⇒ violations, anything else ⇒ infra error) and NEVER throws.
/// </summary>
public sealed record DrcReport(
    bool Installed,
    bool Ok,
    string Summary,
    IReadOnlyList<DrcViolation> Violations,
    IReadOnlyList<DrcViolation> Unconnected,
    int ErrorCount,
    int WarningCount,
    int UnconnectedCount,
    IReadOnlyList<string> Notes)
{
    /// <summary>The gate: ran cleanly, no gated errors, no nets left open.</summary>
    public bool Clean => Ok && ErrorCount == 0 && UnconnectedCount == 0;

    public static DrcReport NotInstalled() =>
        new(false, false,
            $"DRC needs KiCad — install it from {KiCadInstaller.DownloadUrl} to run kicad-cli pcb drc.",
            Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 0, 0, 0, Array.Empty<string>());

    public static DrcReport Failed(string summary, IEnumerable<string>? notes = null) =>
        new(true, false, summary, Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(),
            0, 0, 0, (notes ?? Array.Empty<string>()).ToArray());

    /// <summary>
    /// The dominant violation classes this iteration, most-frequent first — used by the fix loop to pick
    /// which knob to bump. Unconnected entries are folded in as the "unconnected_items" class.
    /// </summary>
    public IReadOnlyList<string> DominantClasses()
    {
        var classes = Violations.Where(v => !v.Excluded && v.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Type)
            .Concat(Unconnected.Where(u => !u.Excluded).Select(_ => "unconnected_items"));
        return classes
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .ToList();
    }

    /// <summary>
    /// Parse the DRC report FILE contents + the cli exit code, tolerant in the <see cref="RouteResult.Parse"/>
    /// style. The JSON is authoritative for *what* the violations are; the exit code is the fast clean/dirty
    /// bit (0 ⇒ clean, 5 ⇒ violations, anything else ⇒ infra/IO error → <see cref="Ok"/> false + stderr note).
    /// <c>excluded:true</c> entries are filtered from the gate counts. Missing/garbage report ⇒ <see cref="Failed"/>.
    /// Never throws.
    /// </summary>
    public static DrcReport Parse(string? reportJson, int exitCode, string? stderr)
    {
        // exit ∉ {0,5} ⇒ infra/IO error: the report (if any) isn't trustworthy.
        if (exitCode != 0 && exitCode != 5)
        {
            var note = string.IsNullOrWhiteSpace(stderr) ? $"kicad-cli exited {exitCode}." : stderr!.Trim();
            return Failed("Couldn't run DRC.", new[] { note });
        }

        if (string.IsNullOrWhiteSpace(reportJson))
        {
            // exit 0 with no report file is reconciled to "clean" (clean boards sometimes write nothing
            // useful), but a missing file on a violations exit is an IO error.
            if (exitCode == 0)
                return new DrcReport(true, true, "DRC clean — 0 errors, fully connected.",
                    Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 0, 0, 0, Array.Empty<string>());
            return Failed("DRC reported violations but produced no readable report.",
                string.IsNullOrWhiteSpace(stderr) ? null : new[] { stderr!.Trim() });
        }

        List<DrcViolation> violations, unconnected;
        try
        {
            using var doc = JsonDocument.Parse(reportJson);
            var root = doc.RootElement;
            violations = ParseArray(root, "violations");
            unconnected = ParseArray(root, "unconnected_items");

            // Some KiCad builds fold unconnected nets into violations[] as type "unconnected_items";
            // pull those out so the gate counts them as connectivity, not as generic errors.
            var folded = violations.Where(v => v.Type.Equals("unconnected_items", StringComparison.OrdinalIgnoreCase)).ToList();
            if (folded.Count > 0)
            {
                violations = violations.Except(folded).ToList();
                unconnected = unconnected.Concat(folded).ToList();
            }
        }
        catch
        {
            return Failed("Couldn't parse the DRC report.",
                string.IsNullOrWhiteSpace(stderr) ? null : new[] { stderr!.Trim() });
        }

        bool Gated(DrcViolation v) => !v.Excluded;
        int errors = violations.Count(v => Gated(v) && v.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        int warnings = violations.Count(v => Gated(v) && v.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
        int unconnectedCount = unconnected.Count(Gated);

        var notes = new List<string>();
        // JSON wins for *what*; reconcile the exit code as a secondary signal.
        if (exitCode == 5 && errors == 0 && warnings == 0 && unconnectedCount == 0)
            notes.Add("kicad-cli reported violations (exit 5) but all parsed entries were excluded or warnings.");

        bool clean = errors == 0 && unconnectedCount == 0;
        var summary = clean
            ? (warnings == 0 ? "DRC clean — 0 errors, fully connected." : $"DRC clean — 0 errors, {warnings} warning(s), fully connected.")
            : $"DRC found {Count(errors, "error")}" +
              (unconnectedCount > 0 ? $", {unconnectedCount} net(s) unconnected" : "") +
              (warnings > 0 ? $", {warnings} warning(s)" : "") + ".";

        return new DrcReport(true, true, summary, violations, unconnected, errors, warnings, unconnectedCount, notes);
    }

    private static string Count(int n, string noun) => $"{n} {noun}" + (n == 1 ? "" : "s");

    private static List<DrcViolation> ParseArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<DrcViolation>();

        var result = new List<DrcViolation>();
        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            result.Add(new DrcViolation(
                Str(v, "type", "unknown"),
                Str(v, "severity", "error"),
                Str(v, "description", ""),
                Bool(v, "excluded"),
                ParseItems(v)));
        }
        return result;
    }

    private static IReadOnlyList<DrcItem> ParseItems(JsonElement violation)
    {
        if (!violation.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<DrcItem>();

        var result = new List<DrcItem>();
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            double x = 0, y = 0;
            if (it.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                x = Dbl(pos, "x", 0);
                y = Dbl(pos, "y", 0);
            }
            result.Add(new DrcItem(Str(it, "uuid", ""), Str(it, "description", ""), x, y));
        }
        return result;
    }

    private static string Str(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Dbl(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;
}
