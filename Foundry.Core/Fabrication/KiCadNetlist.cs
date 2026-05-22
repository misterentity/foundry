using System.Text;
using Foundry.Core.Project;

namespace Foundry.Core.Fabrication;

/// <summary>
/// Exports the Project's components + connections to a KiCad eeschema netlist (.net, version "D")
/// so a design can move straight into PCB layout (PRD v2 G4). Pure, deterministic string generation —
/// no AI, no KiCad install. Point-to-point connections are unioned into electrical nets (union-find).
/// </summary>
public static class KiCadNetlist
{
    public static string Export(Project.Project project)
    {
        var conns = project.Connections
            .Where(c => !string.IsNullOrWhiteSpace(c.From) && !string.IsNullOrWhiteSpace(c.To))
            .ToList();

        // ---- union-find over endpoints ("REF.PIN") to form electrical nets ----
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string x)
        {
            parent.TryAdd(x, x);
            var root = x;
            while (!string.Equals(parent[root], root, StringComparison.OrdinalIgnoreCase)) root = parent[root];
            while (!string.Equals(parent[x], root, StringComparison.OrdinalIgnoreCase)) { var n = parent[x]; parent[x] = root; x = n; }
            return root;
        }
        void Union(string a, string b) { parent[Find(a)] = Find(b); }

        foreach (var c in conns) { Union(c.From, c.To); }

        // net "type" hint per endpoint (for naming): the net of any connection touching it
        var endpointNet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conns)
        {
            endpointNet[c.From] = c.Net;
            endpointNet[c.To] = c.Net;
        }

        var groups = parent.Keys
            .GroupBy(Find, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ---- components: every alias that appears, ref = alias, value = part name ----
        var refs = conns.SelectMany(c => new[] { RefOf(c.From), RefOf(c.To) })
            .Concat(project.Components.Select(c => c.Alias))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string NameOf(string alias) =>
            project.Components.FirstOrDefault(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))?.Name
            ?? project.Subsystems.FirstOrDefault(s => s.Name.StartsWith(alias, StringComparison.OrdinalIgnoreCase))?.Name
            ?? alias;
        string MpnOf(string alias) =>
            project.Components.FirstOrDefault(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))?.Ref
            ?? "";

        var sb = new StringBuilder();
        sb.AppendLine("(export (version \"D\")");
        sb.AppendLine("  (design");
        sb.AppendLine($"    (source \"Foundry: {Esc(project.Title)}\")");
        sb.AppendLine($"    (date \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\")");
        sb.AppendLine("    (tool \"Foundry 1.x\"))");

        sb.AppendLine("  (components");
        foreach (var r in refs)
        {
            sb.AppendLine($"    (comp (ref \"{Esc(r)}\")");
            sb.AppendLine($"      (value \"{Esc(NameOf(r))}\")");
            sb.AppendLine("      (footprint \"\")");
            var mpn = MpnOf(r);
            if (mpn.Length > 0)
                sb.AppendLine($"      (fields (field (name \"MPN\") \"{Esc(mpn)}\"))");
            sb.AppendLine("    )");
        }
        sb.AppendLine("  )");

        sb.AppendLine("  (nets");
        int code = 1;
        foreach (var g in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var nodes = g.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
            var name = NetName(nodes, endpointNet, code);
            sb.AppendLine($"    (net (code \"{code}\") (name \"{Esc(name)}\")");
            foreach (var ep in nodes)
                sb.AppendLine($"      (node (ref \"{Esc(RefOf(ep))}\") (pin \"{Esc(PinOf(ep))}\"))");
            sb.AppendLine("    )");
            code++;
        }
        sb.AppendLine("  )");
        sb.AppendLine(")");
        return sb.ToString();
    }

    private static string RefOf(string endpoint)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0 ? endpoint.Trim() : endpoint[..dot].Trim();
    }
    private static string PinOf(string endpoint)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0 ? "1" : endpoint[(dot + 1)..].Trim();
    }

    private static string NetName(List<string> nodes, Dictionary<string, string> endpointNet, int code)
    {
        bool Any(Func<string, bool> f) => nodes.Any(n => f(PinOf(n).ToUpperInvariant()));
        if (Any(p => p is "GND" or "GROUND" or "VSS")) return "GND";
        var pwr = nodes.Select(n => PinOf(n).ToUpperInvariant())
            .FirstOrDefault(p => p is "3V3" or "3.3V" or "VCC" or "VDD" or "5V" or "VIN" or "VBAT" or "VOUT" or "+");
        if (pwr is not null) return pwr == "+" ? "+VBATT" : "+" + pwr.TrimStart('+');
        var i2c = nodes.Select(n => PinOf(n).ToUpperInvariant()).FirstOrDefault(p => p is "SDA" or "SCL");
        if (i2c is not null) return i2c;
        return $"Net-({code:000})";
    }

    private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
