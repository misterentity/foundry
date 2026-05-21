using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Core.Validation;

/// <summary>
/// Re-runs the deterministic <see cref="RulesEngine"/> over a Project and applies bounded,
/// deterministic auto-fixes by editing the authoritative netlist (PRD §6/§8.8). Auto-fix never
/// invents parts — it only remaps a pin to a free, capable GPIO or connects a missing power/ground
/// rail; everything else is left to the user.
/// </summary>
public static class ProjectValidator
{
    /// <summary>Recompute findings + the overall rollup from the project's current netlist + components.</summary>
    public static void Revalidate(Project.Project p, int batteryGoalDays = 0)
    {
        var kb = new ComponentKb(p.Components);
        p.Findings = RulesEngine.Validate(p.Connections, kb, batteryGoalDays);
        p.Validation = p.Findings.Any(f => f.Severity == "fail") ? "fail"
            : p.Findings.Any(f => f.Severity == "warn") ? "warn" : "pass";
    }

    /// <summary>True if this finding has a deterministic netlist fix this engine can apply.</summary>
    public static bool CanAutoFix(Finding f) => f.Code is "PIN-04" or "PIN-IO" or "PIN-CONF" or "PWR-NC" or "GND-NC";

    /// <summary>Apply the fix to the netlist in place. Returns false if nothing could be changed.</summary>
    public static bool TryAutoFix(Project.Project p, Finding f)
    {
        var kb = new ComponentKb(p.Components);
        return f.Code switch
        {
            "PIN-04" or "PIN-IO" => RemapPin(p, kb, f.Refs.FirstOrDefault(), conflictKeepFirst: false),
            "PIN-CONF" => RemapPin(p, kb, f.Refs.FirstOrDefault(), conflictKeepFirst: true),
            "PWR-NC" => ConnectRail(p, kb, f.Refs.FirstOrDefault(), "power"),
            "GND-NC" => ConnectRail(p, kb, f.Refs.FirstOrDefault(), "ground"),
            _ => false,
        };
    }

    private static (string alias, string pin) Split(string? ep)
    {
        if (string.IsNullOrEmpty(ep)) return ("", "");
        var dot = ep.IndexOf('.');
        return dot < 0 ? (ep, "") : (ep[..dot], ep[(dot + 1)..]);
    }

    /// <summary>Move the offending signal endpoint to a free, output-capable, non-strapping GPIO.</summary>
    private static bool RemapPin(Project.Project p, ComponentKb kb, string? endpoint, bool conflictKeepFirst)
    {
        var (alias, pin) = Split(endpoint);
        var spec = kb.ByAlias(alias);
        if (spec is null || pin.Length == 0) return false;

        var used = new HashSet<string>(
            p.Connections.SelectMany(c => new[] { c.From, c.To }), StringComparer.OrdinalIgnoreCase);

        var free = spec.Pins.FirstOrDefault(pn =>
            pn.Kind == PinKind.Bidir && !pn.Strapping && !pn.InputOnly &&
            !used.Contains($"{alias}.{pn.Name}"));
        if (free is null) return false;

        var newEp = $"{alias}.{free.Name}";
        var matches = p.Connections
            .Where(c => Eq(c.From, endpoint!) || Eq(c.To, endpoint!))
            .ToList();
        if (matches.Count == 0) return false;

        // For a pin conflict, keep the first claimant and move the rest; otherwise move all.
        foreach (var c in conflictKeepFirst ? matches.Skip(1) : matches)
        {
            if (Eq(c.From, endpoint!)) c.From = newEp;
            if (Eq(c.To, endpoint!)) c.To = newEp;
        }
        return true;
    }

    /// <summary>Add a missing power/ground connection from the part to a suitable rail source.</summary>
    private static bool ConnectRail(Project.Project p, ComponentKb kb, string? alias, string net)
    {
        if (string.IsNullOrEmpty(alias)) return false;
        var spec = kb.ByAlias(alias);
        if (spec is null) return false;

        var wantKind = net == "power" ? PinKind.Power : PinKind.Ground;
        var sinkPin = spec.Pins.FirstOrDefault(pn => pn.Kind == wantKind);
        if (sinkPin is null) return false;

        // Pick a source: a regulator/MCU power pin (for power) or any ground pin elsewhere.
        var source = kb.All.FirstOrDefault(s =>
            !s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
            s.Pins.Any(pn => pn.Kind == wantKind));
        if (source is null) return false;
        var srcPin = source.Pins.First(pn => pn.Kind == wantKind);

        p.Connections.Add(new Connection
        {
            From = $"{source.Alias}.{srcPin.Name}",
            To = $"{alias}.{sinkPin.Name}",
            Net = net,
        });
        return true;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
