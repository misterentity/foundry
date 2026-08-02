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
    /// <summary>Recompute findings + the overall rollup from the project's current netlist + components.
    /// <paramref name="modelDir"/> is KiCad's 3dmodels dir when available — without it, component heights
    /// are unknown and mechanical fit reports <c>unproven</c> rather than guessing them flat.</summary>
    public static void Revalidate(Project.Project p, int batteryGoalDays = 0, string? modelDir = null)
    {
        var kb = new ComponentKb(p.Components);
        var findings = RulesEngine.Validate(p.Connections, kb, batteryGoalDays);

        // Mechanical fit joins the electrical findings. This is pure — the placer and the courtyard
        // table need no KiCad — so a case that the board cannot physically go into is caught offline,
        // on the same report card, instead of at the printer. Never throws: geometry problems must not
        // take down electrical validation.
        try { findings.AddRange(Cad.EnclosureFit.CheckProject(p, modelDir: modelDir)); }
        catch (Exception ex) { Diagnostics.AppLog.Warn("validation", $"enclosure fit check skipped: {ex.Message}"); }

        // Re-sort and re-number the COMBINED set. Validate already ordered its own findings, so appending
        // afterwards left the mechanical ones stranded below the passing rows with no F·nn / ?·nn label.
        p.Findings = RulesEngine.Order(findings);
        p.Validation = Rollup(p.Findings);
    }

    /// <summary>
    /// The single place a set of findings becomes the report card's verdict. <c>unproven</c> outranks
    /// <c>pass</c> deliberately: a check the engine could not complete is not a check the design passed,
    /// and letting it fall through to "pass" is exactly how a validator ends up certifying what it never
    /// looked at.
    /// </summary>
    public static string Rollup(IEnumerable<Finding> findings)
    {
        var all = findings as ICollection<Finding> ?? findings.ToList();
        if (all.Any(f => f.Severity == "fail")) return "fail";
        if (all.Any(f => f.Severity == "warn")) return "warn";
        if (all.Any(f => f.Severity == "unproven")) return "unproven";
        return "pass";
    }

    /// <summary>True if this finding has a deterministic netlist fix this engine can apply.</summary>
    public static bool CanAutoFix(Finding f) => f.AutoFixable;

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

        var matches = p.Connections
            .Where(c => Eq(c.From, endpoint!) || Eq(c.To, endpoint!))
            .ToList();
        if (matches.Count == 0) return false;

        // For a pin conflict, keep the first claimant and move the rest; otherwise move all.
        var toMove = (conflictKeepFirst ? matches.Skip(1) : matches).ToList();
        if (toMove.Count == 0) return false;

        bool movedAny = false;
        foreach (var c in toMove)
        {
            // Recompute used pins each iteration so each moved connection gets a DISTINCT free GPIO. Computing
            // one free pin up front and moving every loser onto it just re-creates the conflict on the new pin.
            var used = new HashSet<string>(
                p.Connections.SelectMany(cc => new[] { cc.From, cc.To }), StringComparer.OrdinalIgnoreCase);
            var free = spec.Pins.FirstOrDefault(pn =>
                pn.Kind == PinKind.Bidir && !pn.Strapping && !pn.InputOnly &&
                !used.Contains($"{alias}.{pn.Name}"));
            if (free is null) return movedAny;   // ran out of free GPIOs — keep what we managed to move

            var newEp = $"{alias}.{free.Name}";
            if (Eq(c.From, endpoint!)) c.From = newEp;
            if (Eq(c.To, endpoint!)) c.To = newEp;
            movedAny = true;
        }
        return movedAny;
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

        if (net == "power")
        {
            // PIN-ACCURATE rail matching. A part's InputVRange describes what the BOARD accepts — usually via a
            // VIN/raw pin — and does NOT license putting that voltage on a pin whose NAME declares a different
            // rail. An ESP32 DevKit is inputV [3.0,5.5] with a single 3V3 pin, so a component-level check alone
            // happily wires 5 V onto 3V3 and destroys the board, then re-validates to "pass". Every candidate
            // supply must therefore land on a pin that actually accepts its voltage, or we refuse outright.
            foreach (var s in kb.All)
            {
                if (s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.OutputV is not double v) continue;   // must actually SOURCE a voltage, not merely have VCC
                var srcPin0 = s.Pins.FirstOrDefault(pn => pn.Kind == PinKind.Power);
                if (srcPin0 is null) continue;
                if (spec.InputVRange is { Length: 2 } r && (v < r[0] || v > r[1])) continue;

                var sink = spec.Pins.FirstOrDefault(pn => pn.Kind == PinKind.Power && AcceptsRail(pn.Name, v));
                if (sink is null) continue;

                p.Connections.Add(new Connection
                {
                    From = $"{s.Alias}.{srcPin0.Name}",
                    To = $"{alias}.{sink.Name}",
                    Net = net,
                });
                return true;
            }
            // No supply lands unambiguously on a power pin — refuse. Leaving the rail unconnected keeps the
            // PWR-NC finding visible; guessing would manufacture a hazard behind a green verdict.
            return false;
        }
        // Ground is a shared rail — any other part exposing a ground pin is a valid tie point.
        var source = kb.All.FirstOrDefault(s =>
            !s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
            s.Pins.Any(pn => pn.Kind == PinKind.Ground));
        if (source is null) return false;

        p.Connections.Add(new Connection
        {
            From = $"{source.Alias}.{source.Pins.First(pn => pn.Kind == PinKind.Ground).Name}",
            To = $"{alias}.{sinkPin.Name}",
            Net = net,
        });
        return true;
    }

    /// <summary>
    /// The nominal rail a power pin's NAME declares — 3V3/3.3V → 3.3, +5V/VBUS → 5.0, 1V8 → 1.8 — or null
    /// when the name is generic (VIN, VCC, VDD, VBAT, V+) and the pin therefore accepts whatever the
    /// component-level <see cref="ComponentSpec.InputVRange"/> allows. Pure and unit-testable.
    /// </summary>
    internal static double? RailVoltageOf(string? pinName)
    {
        var n = (pinName ?? "").Trim().TrimStart('+').ToUpperInvariant();
        if (n.Length == 0) return null;
        if (n is "VBUS" or "VUSB") return 5.0;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // "3V3" / "1V8" / "5V" — the digit-V-digit silkscreen convention.
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^(\d{1,2})V(\d{0,2})$");
        if (m.Success)
        {
            var frac = m.Groups[2].Value.Length == 0 ? "0" : m.Groups[2].Value;
            return double.Parse($"{m.Groups[1].Value}.{frac}", inv);
        }
        // "3.3V" / "12V"
        m = System.Text.RegularExpressions.Regex.Match(n, @"^(\d{1,2}(?:\.\d{1,2})?)V$");
        return m.Success ? double.Parse(m.Groups[1].Value, inv) : null;
    }

    /// <summary>
    /// True if a pin will accept <paramref name="supplyV"/>. A pin whose name declares a rail accepts only
    /// that rail (±5%, floor 0.15 V); a generic pin (VIN/VCC/VDD) defers to the component-level range the
    /// caller already checked.
    /// </summary>
    internal static bool AcceptsRail(string? pinName, double supplyV) =>
        RailVoltageOf(pinName) is not double declared ||
        Math.Abs(declared - supplyV) <= Math.Max(0.15, declared * 0.05);

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
