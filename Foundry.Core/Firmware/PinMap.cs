using System.Text;
using System.Text.RegularExpressions;
using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Core.Firmware;

/// <summary>One generated pin-map entry: a peripheral signal bound to a concrete MCU GPIO.</summary>
/// <param name="Dir">MCU-side direction: input | output | analog | i2c (drives setup/loop codegen).</param>
public sealed record PinMapEntry(string Macro, int Gpio, string FromPin, string ToPin, string Net, bool Strapping, string Dir = "input");

/// <summary>
/// Derives the firmware pin map directly from the authoritative netlist (PRD §6 invariant, §8.6,
/// F4) — there are no hand-typed pins. Every signal/I²C net that lands on an MCU GPIO becomes a
/// <c>#define</c>. Because it is computed from <see cref="Connection"/>s, it regenerates whenever
/// the wiring changes.
/// </summary>
public static class PinMap
{
    private static readonly Regex GpioNum = new(@"(\d+)\s*$", RegexOptions.Compiled);

    // GPIO-style pin names across the boards Foundry targets: GPIOn / GPn (Pico) / IOn / P0.n (nRF/STM) / Dn / An (Arduino).
    private static readonly Regex McuPinName = new(@"^(GPIO|GP|IO|P\d+[._]|D|A)\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The alias acting as MCU = the netlist component with the MOST GPIO-style pins. Counting (rather
    /// than "has a pin starting with GPIO") catches Pico/AVR/nRF naming and avoids mis-picking a small part that
    /// happens to carry a D1/A0-style pad.</summary>
    public static string? DetectMcuAlias(IReadOnlyList<Connection> connections, ComponentKb kb)
    {
        return connections
            .SelectMany(c => new[] { Alias(c.From), Alias(c.To) })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => (alias: a, gpios: kb.ByAlias(a)?.Pins.Count(p => McuPinName.IsMatch(p.Name)) ?? 0))
            .Where(x => x.gpios > 0)
            .OrderByDescending(x => x.gpios)
            .Select(x => x.alias)
            .FirstOrDefault();
    }

    public static IReadOnlyList<PinMapEntry> Build(IReadOnlyList<Connection> connections, ComponentKb kb)
    {
        bool hasSignalNets = connections.Any(c => c.Net is "signal" or "i2c");
        var mcu = DetectMcuAlias(connections, kb);
        if (mcu is null)
        {
            // Don't fail silently: the "pins derived from the netlist" moat produced nothing.
            if (hasSignalNets)
                Diagnostics.AppLog.Warn("firmware", "no MCU detected in the netlist — the generated pin map is empty (firmware will have no pin defines).");
            return Array.Empty<PinMapEntry>();
        }

        var entries = new List<PinMapEntry>();
        foreach (var c in connections.Where(c => c.Net is "signal" or "i2c"))
        {
            // identify which endpoint is the MCU GPIO and which is the peripheral
            var (mcuEp, periphEp) = MatchMcu(c.From, c.To, mcu);
            if (mcuEp is null || periphEp is null) continue;

            var gpio = ExtractGpio(Pin(mcuEp));
            if (gpio is null) continue;

            var macro = "PIN_" + Sanitize(Alias(periphEp)) + "_" + Sanitize(Pin(periphEp));
            var mcuPin = kb.ByAlias(mcu)?.Pin(Pin(mcuEp));
            var periphPin = kb.ByAlias(Alias(periphEp))?.Pin(Pin(periphEp));
            var strapping = mcuPin?.Strapping ?? false;
            // MCU-side direction: i2c bus, analog input (ADC pin), output if the peripheral end is an input, else digital input.
            var dir = c.Net == "i2c" ? "i2c"
                : mcuPin?.Kind == PinKind.Analog ? "analog"
                : periphPin?.Kind == PinKind.Input ? "output"
                : "input";
            entries.Add(new PinMapEntry(macro, gpio.Value, mcuEp, periphEp, c.Net, strapping, dir));
        }
        if (entries.Count == 0 && hasSignalNets)
            Diagnostics.AppLog.Warn("firmware", $"MCU '{mcu}' detected but no signal/I²C net resolved to a GPIO pin — the generated pin map is empty.");
        // stable order by GPIO number for deterministic output
        return entries.OrderBy(e => e.Gpio).ToList();
    }

    /// <summary>Renders the entries as a C header (the <c>pinmap.h</c> body).</summary>
    public static string RenderHeader(IReadOnlyList<PinMapEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// GENERATED — derived from Project.connections");
        sb.AppendLine("// Do not edit; re-runs on every wiring change.");
        sb.AppendLine();
        sb.AppendLine("#pragma once");
        sb.AppendLine();
        int pad = entries.Count == 0 ? 16 : Math.Max(16, entries.Max(e => e.Macro.Length) + 2);
        foreach (var e in entries)
        {
            var note = $"// from net: {e.Net.ToUpperInvariant()} · {e.FromPin} ↔ {e.ToPin}" +
                       (e.Strapping ? "    [strapping pin — see validation]" : "");
            sb.AppendLine(note);
            sb.AppendLine($"#define {e.Macro.PadRight(pad)}{e.Gpio}");
            sb.AppendLine();
        }
        sb.AppendLine("// ADC reference (ESP32 default attenuation 11dB → ~3.3V)");
        sb.AppendLine("#define ADC_REF_MV        3300");
        return sb.ToString();
    }

    // ---- helpers ----
    private static (string? mcuEp, string? periphEp) MatchMcu(string from, string to, string mcu)
    {
        if (Alias(from).Equals(mcu, StringComparison.OrdinalIgnoreCase)) return (from, to);
        if (Alias(to).Equals(mcu, StringComparison.OrdinalIgnoreCase)) return (to, from);
        return (null, null);
    }

    private static int? ExtractGpio(string pin)
    {
        var m = GpioNum.Match(pin);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    private static string Alias(string endpoint)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0 ? endpoint : endpoint[..dot];
    }

    private static string Pin(string endpoint)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0 ? "" : endpoint[(dot + 1)..];
    }

    private static string Sanitize(string s)
    {
        var chars = s.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_');
        return new string(chars.ToArray());
    }
}
