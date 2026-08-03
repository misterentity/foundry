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

        Referential(eps, kb, findings);
        Grounding(eps, kb, findings);
        PinConflicts(connections, kb, findings);
        StrappingPins(eps, kb, findings);
        InputOnlyMisuse(connections, kb, findings);
        VoltageLevels(connections, kb, findings);
        PowerGround(aliases, eps, kb, findings);
        PowerBudget(aliases, kb, batteryGoalDays, findings);
        I2cCollisions(aliases, kb, findings);
        PassivesPresent(connections, kb, findings);   // v2: I²C pull-ups + LED current-limit resistors

        return Order(findings);
    }

    private static Endpoint Parse(string endpoint, string net)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0
            ? new Endpoint(endpoint, "", net)
            : new Endpoint(endpoint[..dot], endpoint[(dot + 1)..], net);
    }

    // ---- Rule: the model's declared pins, checked against a real pinout ----
    //
    // Every other rule reasons over ComponentSpec.Pins, and those come from the model's own JSON reply
    // (ProjectGenerator builds the KB from it). So the "deterministic" engine was grading the model with
    // the model's own answer key: a hallucinated pin passed every check, reached pinmap.h, and got
    // flashed to real hardware — the PCB build was the only thing that refused it, long after the user
    // had wired it on a breadboard.
    //
    // PartResolver applies the SAME authority the board build refuses on. A part it can place tells us
    // the design's pins are real; a pin it cannot place on such a part is invented. A part it has no
    // authority over is reported UNPROVEN, never failed — absence of evidence is not evidence.
    private static void Grounding(List<Endpoint> eps, ComponentKb kb, List<Finding> findings,
        string? symbolDir = null)
    {
        var ungrounded = new List<string>();

        // Only pins the netlist actually USES can do harm. A part may legitimately declare pins the
        // resolved footprint lacks — the demo's "ESP32 DevKit v1" carries a 5V pin while its footprint is
        // the bare WROOM module, which has none. That is a modelling wart, not a build-breaker: nothing
        // is wired to it and the PCB step never sees it. Failing on it would punish designs for being
        // descriptive.
        var wired = eps
            .Where(e => e.Pin.Length > 0)
            .ToLookup(e => e.Alias, e => e.Pin, StringComparer.OrdinalIgnoreCase);

        foreach (var spec in kb.All.Where(s => s.Pins.Count > 0))
        {
            if (!Kb.PartResolver.Identify(spec, symbolDir).IsGrounded)
            {
                ungrounded.Add(spec.Name);
                continue;
            }

            var used = new HashSet<string>(wired[spec.Alias], StringComparer.OrdinalIgnoreCase);
            var bad = Kb.PartResolver.UnresolvablePins(spec, symbolDir)
                .Where(used.Contains)
                .ToList();
            if (bad.Count == 0) continue;

            findings.Add(new Finding
            {
                Severity = "fail", Code = "PIN-UNK",
                Title = $"{spec.Name} has no pin {string.Join(", ", bad.Take(3).Select(b => $"“{b}”"))}",
                Description =
                    $"The netlist wires {bad.Count} pin(s) that do not exist on the real part: " +
                    string.Join(", ", bad.Take(8)) + (bad.Count > 8 ? " …" : "") +
                    ". Those connections cannot be built, and the PCB step will refuse the board.",
                Refs = bad.Take(6).Select(b => $"{spec.Alias}.{b}").ToList(),
                Fix = "Use pins the part actually has",
            });
        }

        if (ungrounded.Count > 0)
            findings.Add(new Finding
            {
                Severity = "unproven", Code = "PIN-UNVERIFIED",
                Title = $"{ungrounded.Count} part(s) have no authoritative pinout",
                Description =
                    "Foundry has no curated table or KiCad symbol for these, so their pin names are taken " +
                    "from the design description and were not checked against a real part: " +
                    string.Join(", ", ungrounded.Take(6)) +
                    (ungrounded.Count > 6 ? $" (+{ungrounded.Count - 6} more)" : "") + ".",
                Refs = ungrounded.Take(6).ToList(),
                Fix = "Supply a footprint for these parts",
            });
    }

    // ---- Rule: a net references a part or a pin that the design never declared ----
    // Every other rule reaches the KB via kb.ByAlias(..)?.Pin(..) and silently skips a miss, so a netlist
    // naming parts or pins that don't exist used to produce ZERO findings and roll up to "pass" — the engine
    // reporting a clean bill of health precisely where it had nothing to check. An endpoint that cannot be
    // resolved is not a passing endpoint; it is an unvalidatable one, and it fails.
    private static void Referential(List<Endpoint> eps, ComponentKb kb, List<Finding> findings)
    {
        foreach (var alias in eps.Select(e => e.Alias)
                     .Where(a => a.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(a => kb.ByAlias(a) is null))
            findings.Add(new Finding
            {
                Severity = "fail", Code = "NET-REF",
                Title = $"Net references undeclared part “{alias}”",
                Description = $"The netlist wires {alias}, but no component with that alias is declared, so none of "
                            + "its connections can be checked for voltage, direction or current. Declare the part or "
                            + "correct the alias.",
                Refs = new() { alias },
                Fix = "Declare the missing part",
            });

        foreach (var e in eps.Where(e => e.Pin.Length > 0).DistinctBy(e => e.Full, StringComparer.OrdinalIgnoreCase))
        {
            var spec = kb.ByAlias(e.Alias);
            // A part declared with NO pin table makes no pin claims to contradict (common for passives), so it
            // is not evidence of an error — only a populated table that lacks this pin is.
            if (spec is null || spec.Pins.Count == 0 || spec.Pin(e.Pin) is not null) continue;
            findings.Add(new Finding
            {
                Severity = "fail", Code = "NET-PIN",
                Title = $"{e.Alias} has no pin “{e.Pin}”",
                Description = $"The netlist wires {e.Full}, but {spec.Name} declares no such pin. Its level and "
                            + "direction cannot be checked, and the PCB build will refuse to place it.",
                Refs = new() { e.Full },
                Fix = "Correct the pin",
            });
        }
    }

    // ---- Rule: two distinct signal nets claim the same MCU pin ----
    private static void PinConflicts(IReadOnlyList<Connection> connections, ComponentKb kb, List<Finding> findings)
    {
        // Only point-to-point SIGNAL nets are single-driver. An I²C bus is SHARED by design — the MCU's SDA/SCL
        // fan out to every device on the bus, so counting those endpoints would false-fail the engine's own
        // primary use case (a multi-device I²C design). Bus nets are excluded here.
        var signalUse = connections
            .Where(c => c.Net is "signal")
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
                // logic-level: a mismatch is only a hazard in ONE direction — a HIGHER-voltage driver into a
                // LOWER-voltage receiver that isn't 5V-tolerant. The reverse (e.g. a 3.3V output into a 5V input)
                // is normally safe, so flagging it (the old direction-blind rule did) just nags with bogus fixes.
                if (ca.LogicV is double la && cb.LogicV is double lb && Math.Abs(la - lb) > 0.5)
                {
                    var highEp = la > lb ? a : b;
                    var lowEp = la > lb ? b : a;
                    var highPin = kb.ByAlias(highEp.Alias)?.Pin(highEp.Pin);
                    var lowPin = kb.ByAlias(lowEp.Alias)?.Pin(lowEp.Pin);
                    bool highDrives = highPin is { Kind: PinKind.Output or PinKind.Bidir };
                    bool lowReceives = lowPin is { Kind: PinKind.Input or PinKind.Bidir or PinKind.Analog };
                    if (highDrives && lowReceives && lowPin is not { FiveVoltTolerant: true })
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
                // Identify SOURCE vs SINK by who actually supplies a voltage (OutputV), not by endpoint order:
                // when exactly one side has OutputV it is the source; otherwise fall back to the From side.
                var (src, sink) =
                    ca.OutputV is not null && cb.OutputV is null ? (ca, cb) :
                    cb.OutputV is not null && ca.OutputV is null ? (cb, ca) :
                    ca.OutputV is not null ? (ca, cb) : (cb, ca);
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

    // ---- Rule (v2 G7): required passives — I²C pull-ups + LED current-limit resistors ----
    private static bool IsResistor(ComponentSpec? s) =>
        s is not null && (s.Name.Contains("resistor", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains('Ω') || s.Name.Contains(" ohm", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains("kΩ", StringComparison.OrdinalIgnoreCase));

    private static bool IsBareLed(ComponentSpec? s)
    {
        if (s is null || !s.Name.Contains("led", StringComparison.OrdinalIgnoreCase)) return false;
        // smart/driven LEDs have their own current control — exclude them
        string[] driven = { "strip", "matrix", "neopixel", "ws2812", "sk6812", "apa10", "module", "ring", "panel", "7-seg", "segment", "backlight", "driver" };
        return !driven.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static void PassivesPresent(IReadOnlyList<Connection> connections, ComponentKb kb, List<Finding> findings)
    {
        string PinUp(string ep) { var d = ep.IndexOf('.'); return (d < 0 ? "" : ep[(d + 1)..]).ToUpperInvariant(); }
        string Ref(string ep) { var d = ep.IndexOf('.'); return d < 0 ? ep : ep[..d]; }

        // ---- I²C pull-ups ----
        var i2c = connections.Where(c => c.Net == "i2c").ToList();
        if (i2c.Count > 0)
        {
            bool pullup = connections.Any(c =>
            {
                bool ra = IsResistor(kb.ByAlias(Ref(c.From))), rb = IsResistor(kb.ByAlias(Ref(c.To)));
                bool sclSdaA = PinUp(c.From) is "SDA" or "SCL", sclSdaB = PinUp(c.To) is "SDA" or "SCL";
                return (ra && sclSdaB) || (rb && sclSdaA);
            });
            if (!pullup)
                findings.Add(new Finding
                {
                    Severity = "warn", Code = "PULL-I2C",
                    Title = "I²C bus has no pull-up resistors",
                    Description = "SDA and SCL need pull-ups to VCC (typically 4.7 kΩ) for the bus to work. " +
                                  "Some breakout boards include them — check yours; otherwise add a pair.",
                    Refs = i2c.Select(c => c.From).Concat(i2c.Select(c => c.To)).Where(e => PinUp(e) is "SDA" or "SCL").Distinct().ToList(),
                    Fix = "Add 4.7 kΩ pull-up resistors from SDA and SCL to VCC.",
                });
        }

        // ---- LED current-limit resistors ----
        var ledAliases = connections
            .SelectMany(c => new[] { Ref(c.From), Ref(c.To) })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => IsBareLed(kb.ByAlias(a)))
            .ToList();
        var unprotected = new List<string>();
        foreach (var led in ledAliases)
        {
            bool drivenBySignal = connections.Any(c => c.Net == "signal" &&
                (Ref(c.From).Equals(led, StringComparison.OrdinalIgnoreCase) || Ref(c.To).Equals(led, StringComparison.OrdinalIgnoreCase)));
            if (!drivenBySignal) continue;
            bool hasResistor = connections.Any(c =>
            {
                bool touchesLed = Ref(c.From).Equals(led, StringComparison.OrdinalIgnoreCase) || Ref(c.To).Equals(led, StringComparison.OrdinalIgnoreCase);
                bool otherIsR = IsResistor(kb.ByAlias(Ref(c.From))) || IsResistor(kb.ByAlias(Ref(c.To)));
                return touchesLed && otherIsR;
            });
            if (!hasResistor) unprotected.Add(led);
        }
        if (unprotected.Count > 0)
            findings.Add(new Finding
            {
                Severity = "warn", Code = "LED-R",
                Title = unprotected.Count == 1 ? $"LED {unprotected[0]} has no current-limit resistor"
                                               : $"{unprotected.Count} LEDs have no current-limit resistor",
                Description = "An LED driven directly from a GPIO without a series resistor can over-current the " +
                              "pin and the LED. Add a series resistor (≈220–470 Ω at 3.3–5 V).",
                Refs = unprotected,
                Fix = "Add a series current-limit resistor (≈330 Ω) between the GPIO and each LED.",
            });
    }

    // ---- ordering + numbering (fail → warn → info → pass), to match the UI ----
    internal static List<Finding> Order(List<Finding> findings)
    {
        int Rank(string s) => s switch { "fail" => 0, "warn" => 1, "unproven" => 2, "info" => 3, _ => 4 };
        // (internal so callers that ADD findings after Validate — e.g. mechanical fit — can re-sort and
        //  re-number the combined set; appending afterwards otherwise leaves them unranked and unnumbered)
        var ordered = Collapse(findings).OrderBy(f => Rank(f.Severity)).ToList();

        int w = 0, fail = 0, info = 0, unproven = 0;
        foreach (var f in ordered)
        {
            f.Num = f.Severity switch
            {
                "fail" => $"F·{++fail:00}",
                "warn" => $"W·{++w:00}",
                "unproven" => $"?·{++unproven:00}",
                "info" => $"i·{++info:00}",
                _ => "OK",
            };
        }
        return ordered;
    }
}
