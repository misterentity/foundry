using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Core.Validation;

/// <summary>
/// Deterministic electrical rules engine (PRD §8.8, §11). Evaluates the assembled netlist against
/// the component KB and returns <see cref="Finding"/>s — power budget, voltage/logic-level
/// mismatches, pin conflicts, input-only/strapping-pin misuse, power/ground sanity, and I²C
/// collisions. AI never decides verdicts; this is pure computation over the Project.
/// </summary>
public static class RulesEngine
{
    private readonly record struct Endpoint(string Alias, string Pin, string Net)
    {
        public string Full => $"{Alias}.{Pin}";
    }

    public static List<Finding> Validate(IReadOnlyList<Connection> connections, ComponentKb kb, int batteryGoalDays = 0)
    {
        var findings = new List<Finding>();
        var eps = connections
            .SelectMany(c => new[] { Parse(c.From, c.Net), Parse(c.To, c.Net) })
            .ToList();
        var aliases = eps.Select(e => e.Alias).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        PinConflicts(connections, kb, findings);
        StrappingPins(eps, kb, findings);
        InputOnlyMisuse(connections, kb, findings);
        VoltageLevels(connections, kb, findings);
        PowerGround(aliases, eps, kb, findings);
        PowerBudget(aliases, kb, batteryGoalDays, findings);
        I2cCollisions(aliases, kb, findings);

        return Order(findings);
    }

    private static Endpoint Parse(string endpoint, string net)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0
            ? new Endpoint(endpoint, "", net)
            : new Endpoint(endpoint[..dot], endpoint[(dot + 1)..], net);
    }

    // ---- Rule: two distinct signal nets claim the same MCU pin ----
    private static void PinConflicts(IReadOnlyList<Connection> connections, ComponentKb kb, List<Finding> findings)
    {
        var signalUse = connections
            .Where(c => c.Net is "signal" or "i2c")
            .SelectMany(c => new[] { Parse(c.From, c.Net), Parse(c.To, c.Net) })
            .Where(e => kb.ByAlias(e.Alias)?.Pin(e.Pin) is { Kind: PinKind.Bidir or PinKind.Output or PinKind.Analog or PinKind.Input })
            .GroupBy(e => e.Full, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var g in signalUse)
            findings.Add(new Finding
            {
                Severity = "fail", Code = "PIN-CONF",
                Title = $"Pin {g.Key} is claimed by multiple nets",
                Description = $"{g.Count()} signal connections drive {g.Key}; a pin can carry only one net. Reassign one of them to a free GPIO.",
                Refs = new() { g.Key },
                Fix = "Reassign pin",
            });
    }

    // ---- Rule: boot-strapping pin used for I/O ----
    private static void StrappingPins(List<Endpoint> eps, ComponentKb kb, List<Finding> findings)
    {
        foreach (var e in eps.Where(e => e.Net is "signal" or "i2c").DistinctBy(e => e.Full))
        {
            if (kb.ByAlias(e.Alias)?.Pin(e.Pin) is { Strapping: true })
                findings.Add(new Finding
                {
                    Severity = "warn", Code = "PIN-04",
                    Title = $"{e.Pin} used as I/O — boot strapping pin",
                    Description = $"{e.Pin} on {kb.ByAlias(e.Alias)!.Name} is a strapping pin; driving it during boot can change the boot mode. Move to a non-strapping GPIO (e.g. GPIO13) or add a pull-up.",
                    Refs = new() { e.Full },
                    Fix = "Remap to GPIO13",
                });
        }
    }

    // ---- Rule: input-only pin forced to drive an input on the other end ----
    private static void InputOnlyMisuse(IReadOnlyList<Connection> connections, ComponentKb kb, List<Finding> findings)
    {
        foreach (var c in connections.Where(c => c.Net is "signal"))
        {
            var a = Parse(c.From, c.Net);
            var b = Parse(c.To, c.Net);
            CheckInputOnly(a, b);
            CheckInputOnly(b, a);
        }

        void CheckInputOnly(Endpoint self, Endpoint other)
        {
            var selfPin = kb.ByAlias(self.Alias)?.Pin(self.Pin);
            var otherPin = kb.ByAlias(other.Alias)?.Pin(other.Pin);
            if (selfPin is { InputOnly: true } && otherPin is { Kind: PinKind.Input })
                findings.Add(new Finding
                {
                    Severity = "fail", Code = "PIN-IO",
                    Title = $"{self.Pin} is input-only but must drive {other.Full}",
                    Description = $"{self.Pin} on {kb.ByAlias(self.Alias)!.Name} cannot be used as an output. Use a normal GPIO to drive {other.Full}.",
                    Refs = new() { self.Full, other.Full },
                    Fix = "Use an output-capable GPIO",
                });
        }
    }

    // ---- Rule: logic-level / supply-voltage mismatch ----
    private static void VoltageLevels(IReadOnlyList<Connection> connections, ComponentKb kb, List<Finding> findings)
    {
        bool mismatch = false;

        foreach (var c in connections)
        {
            var a = Parse(c.From, c.Net);
            var b = Parse(c.To, c.Net);
            var ca = kb.ByAlias(a.Alias);
            var cb = kb.ByAlias(b.Alias);
            if (ca is null || cb is null) continue;

            if (c.Net is "signal" or "i2c")
            {
                // logic-level: differing logic voltages, low side not 5V-tolerant
                if (ca.LogicV is double la && cb.LogicV is double lb && Math.Abs(la - lb) > 0.5)
                {
                    var lowEp = la < lb ? a : b;
                    var lowPin = kb.ByAlias(lowEp.Alias)!.Pin(lowEp.Pin);
                    if (lowPin is not { FiveVoltTolerant: true })
                    {
                        mismatch = true;
                        findings.Add(new Finding
                        {
                            Severity = "fail", Code = "VLT-LVL",
                            Title = $"Logic-level mismatch on {a.Full} ↔ {b.Full}",
                            Description = $"{Math.Max(la, lb):0.#}V logic drives a {Math.Min(la, lb):0.#}V-only input. Add a level shifter or choose a 5V-tolerant pin.",
                            Refs = new() { a.Full, b.Full },
                            Fix = "Add level shifter",
                        });
                    }
                }
            }
            else if (c.Net is "power")
            {
                // supply voltage: source voltage must fall within sink's input range
                double? sourceV = ca.OutputV ?? (kb.ByAlias(a.Alias)!.Pin(a.Pin)?.Kind == PinKind.Power ? null : null);
                var (src, sink) = ca.OutputV is not null ? (ca, cb) : (cb, ca);
                if (src.OutputV is double v && sink.InputVRange is { Length: 2 } r && (v < r[0] || v > r[1]))
                {
                    mismatch = true;
                    findings.Add(new Finding
                    {
                        Severity = "fail", Code = "VLT-SUP",
                        Title = $"Supply voltage out of range: {src.Name} → {sink.Name}",
                        Description = $"{src.Name} supplies {v:0.#}V but {sink.Name} accepts {r[0]:0.#}–{r[1]:0.#}V. Add a regulator or pick a compatible part.",
                        Refs = new() { a.Full, b.Full },
                        Fix = "Add regulator",
                    });
                }
            }
        }

        if (!mismatch)
            findings.Add(new Finding
            {
                Severity = "pass", Code = "VLT-00",
                Title = "Voltage / logic levels consistent",
                Description = "No supply-voltage or logic-level mismatches detected across the netlist.",
                Refs = new(),
            });
    }

    // ---- Rule: every active component is powered and grounded ----
    private static void PowerGround(List<string> aliases, List<Endpoint> eps, ComponentKb kb, List<Finding> findings)
    {
        foreach (var alias in aliases)
        {
            var spec = kb.ByAlias(alias);
            if (spec is null) continue;
            bool needsPower = spec.Pins.Any(p => p.Kind == PinKind.Power);
            bool needsGround = spec.Pins.Any(p => p.Kind == PinKind.Ground);
            bool hasPower = eps.Any(e => e.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && e.Net == "power");
            bool hasGround = eps.Any(e => e.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && e.Net == "ground");

            if (needsPower && !hasPower)
                findings.Add(new Finding
                {
                    Severity = "warn", Code = "PWR-NC",
                    Title = $"{spec.Name} has no power connection",
                    Description = $"No power net reaches {alias}. Connect a supply pin.",
                    Refs = new() { alias }, Fix = "Connect power",
                });
            if (needsGround && !hasGround)
                findings.Add(new Finding
                {
                    Severity = "warn", Code = "GND-NC",
                    Title = $"{spec.Name} has no ground connection",
                    Description = $"No ground net reaches {alias}. Connect GND.",
                    Refs = new() { alias }, Fix = "Connect ground",
                });
        }
    }

    // ---- Rule: power budget + battery life ----
    private static void PowerBudget(List<string> aliases, ComponentKb kb, int goalDays, List<Finding> findings)
    {
        int peakMa = aliases.Sum(a => kb.ByAlias(a)?.CurrentMaActive ?? 0);
        var battery = aliases.Select(kb.ByAlias).FirstOrDefault(s => s is { CapacityMah: > 0 });
        if (peakMa <= 0 || battery is null) return;

        double hoursAtPeak = (double)battery.CapacityMah / peakMa;
        // Advisory: battery life is governed by the firmware sleep/duty cycle, not the netlist. Only a
        // stated battery goal that the continuous draw can't meet escalates it to a warning.
        var sev = goalDays > 0 && hoursAtPeak / 24.0 < goalDays ? "warn" : "info";
        findings.Add(new Finding
        {
            Severity = sev, Code = "PWR-02",
            Title = "Battery life depends on duty cycle",
            Description =
                $"Active draw is {peakMa} mA. At {peakMa} mA continuous, {battery.CapacityMah} mAh lasts ~{hoursAtPeak:0.#} h — " +
                $"but a duty-cycled device sleeps most of the time, so real life depends on your sleep schedule. " +
                $"Lower the Wi-Fi duty cycle or TX power to extend it" +
                (goalDays > 0 ? $" past the {goalDays}-day goal." : "."),
            Refs = new() { "BAT.+" },
            Fix = "Reduce active duty cycle / TX power in firmware, or fit a larger cell.",
        });
    }

    // ---- Rule: I²C address collisions ----
    private static void I2cCollisions(List<string> aliases, ComponentKb kb, List<Finding> findings)
    {
        var addressed = aliases
            .Select(kb.ByAlias)
            .Where(s => s?.I2cAddress is not null)
            .ToList();

        if (addressed.Count == 0)
        {
            findings.Add(new Finding
            {
                Severity = "pass", Code = "I2C-00",
                Title = "No I²C address collisions",
                Description = "Project does not use I²C — check skipped.",
                Refs = new(),
            });
            return;
        }

        var dupes = addressed.GroupBy(s => s!.I2cAddress).Where(g => g.Count() > 1).ToList();
        if (dupes.Count == 0)
            findings.Add(new Finding
            {
                Severity = "pass", Code = "I2C-00",
                Title = "No I²C address collisions",
                Description = $"{addressed.Count} I²C devices have distinct addresses.",
                Refs = new(),
            });
        else
            foreach (var g in dupes)
                findings.Add(new Finding
                {
                    Severity = "fail", Code = "I2C-DUP",
                    Title = $"I²C address 0x{g.Key:X2} used by multiple devices",
                    Description = $"{string.Join(", ", g.Select(s => s!.Name))} share address 0x{g.Key:X2}. Re-strap one or add a bus multiplexer.",
                    Refs = g.Select(s => s!.Alias).ToList(),
                    Fix = "Re-strap address",
                });
    }

    // Issue codes that can repeat across a large netlist — collapsed into one summarized finding each
    // so validation stays readable (a 160-net design shouldn't produce 40 identical "add level shifter" rows).
    private static readonly Dictionary<string, string> GroupNoun = new()
    {
        ["VLT-LVL"] = "logic-level mismatches",
        ["VLT-SUP"] = "supply-voltage mismatches",
        ["PIN-04"] = "strapping pins used as I/O",
        ["PIN-IO"] = "input-only pins driving an output",
        ["PIN-CONF"] = "pins claimed by multiple nets",
        ["PWR-NC"] = "components with no power connection",
        ["GND-NC"] = "components with no ground connection",
        ["I2C-DUP"] = "I²C address collisions",
    };

    /// <summary>Collapse repeated findings of the same code into one summarized finding (refs aggregated).</summary>
    private static List<Finding> Collapse(List<Finding> findings)
    {
        var result = new List<Finding>();
        foreach (var grp in findings.GroupBy(f => f.Code))
        {
            var items = grp.ToList();
            if (items.Count == 1 || !GroupNoun.TryGetValue(grp.Key, out var noun))
            {
                result.AddRange(items);
                continue;
            }
            var first = items[0];
            var refs = items.SelectMany(i => i.Refs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shown = string.Join(", ", refs.Take(12)) + (refs.Count > 12 ? $", +{refs.Count - 12} more" : "");
            result.Add(new Finding
            {
                Severity = first.Severity,
                Code = first.Code,
                Title = $"{items.Count} {noun}",
                Description = $"{first.Description} Affected ({refs.Count}): {shown}.",
                Refs = refs,
                Fix = first.Fix,
            });
        }
        return result;
    }

    // ---- ordering + numbering (fail → warn → info → pass), to match the UI ----
    private static List<Finding> Order(List<Finding> findings)
    {
        int Rank(string s) => s switch { "fail" => 0, "warn" => 1, "info" => 2, _ => 3 };
        var ordered = Collapse(findings).OrderBy(f => Rank(f.Severity)).ToList();

        int w = 0, fail = 0, info = 0;
        foreach (var f in ordered)
        {
            f.Num = f.Severity switch
            {
                "fail" => $"F·{++fail:00}",
                "warn" => $"W·{++w:00}",
                "info" => $"i·{++info:00}",
                _ => "OK",
            };
        }
        return ordered;
    }
}
