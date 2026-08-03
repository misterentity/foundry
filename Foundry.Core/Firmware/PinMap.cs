using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Core.Firmware;

/// <summary>One generated pin-map entry: a peripheral signal bound to a concrete MCU GPIO.</summary>
/// <param name="Dir">MCU-side direction: input | output | analog | i2c (drives setup/loop codegen).</param>
/// <param name="Token">
/// The pin's symbolic identity, when its trailing number is NOT its identity — "PA5", "A0". Empty when
/// the number is the identity (GPIO4, GP25, D13), which is the common case. See <see cref="Emit"/>.
/// </param>
public sealed record PinMapEntry(string Macro, int Gpio, string FromPin, string ToPin, string Net, bool Strapping, string Dir = "input", string Token = "")
{
    /// <summary>
    /// The literal written into generated C. Emitting <see cref="Gpio"/> unconditionally was a
    /// wrong-pin bug: on STM32duino "PA5" is port A bit 5 and the bare 5 selects an unrelated pad, and
    /// on AVR "A0" is 14 while 0 is the serial TX line.
    /// </summary>
    public string Emit => Token.Length == 0 ? Gpio.ToString(CultureInfo.InvariantCulture) : Token;

    /// <summary>
    /// The MicroPython literal. Symbolic pins are strings to <c>machine.Pin</c>, and the STM32 port lives
    /// there as "A5" rather than Arduino's "PA5".
    /// </summary>
    public string PyEmit => Token.Length == 0
        ? Gpio.ToString(CultureInfo.InvariantCulture)
        : "'" + (Token.Length > 2 && char.ToUpperInvariant(Token[0]) == 'P' && char.IsLetter(Token[1]) ? Token[1..] : Token) + "'";
}

/// <summary>
/// Derives the firmware pin map directly from the authoritative netlist (PRD §6 invariant, §8.6,
/// F4) — there are no hand-typed pins. Every signal/I²C net that lands on an MCU GPIO becomes a
/// <c>#define</c>. Because it is computed from <see cref="Connection"/>s, it regenerates whenever
/// the wiring changes.
/// </summary>
public static class PinMap
{
    private static readonly Regex GpioNum = new(@"(\d+)\s*$", RegexOptions.Compiled);

    // GPIO-style pin names across the boards Foundry targets: GPIOn / GPn (Pico) / IOn / P0.n (nRF) /
    // PA5,PB5… (STM32 port+pin) / Dn / An (Arduino).
    private static readonly Regex McuPinName = new(@"^(GPIO|GP|IO|P[A-K]|P\d+[._]|D|A)\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            var (token, gpio) = ResolvePin(Pin(mcuEp));
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
            entries.Add(new PinMapEntry(macro, gpio.Value, mcuEp, periphEp, c.Net, strapping, dir, token));
        }
        if (entries.Count == 0 && hasSignalNets)
            Diagnostics.AppLog.Warn("firmware", $"MCU '{mcu}' detected but no signal/I²C net resolved to a GPIO pin — the generated pin map is empty.");
        WarnOnAliasedPins(entries);
        // stable order by emitted pin for deterministic output
        return entries.OrderBy(e => e.Gpio).ThenBy(e => e.Token, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Two distinct MCU pins that emit the same literal drive the same physical pad: one peripheral in
    /// the netlist silently does nothing. Resolving Token kills the common cause (PA5/PB5 both reading
    /// as 5), so anything left is a naming form we cannot tell apart — say so rather than ship it quietly.
    /// </summary>
    private static void WarnOnAliasedPins(List<PinMapEntry> entries)
    {
        foreach (var g in entries.GroupBy(e => e.Emit, StringComparer.OrdinalIgnoreCase))
        {
            var pins = g.Select(e => e.FromPin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (pins.Count > 1)
                Diagnostics.AppLog.Warn("firmware",
                    $"pin map collision: {string.Join(", ", pins)} all emit '{g.Key}' — they will drive the same pad.");
        }
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
            sb.AppendLine($"#define {e.Macro.PadRight(pad)}{e.Emit}");
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

    // Pin names whose trailing number is NOT the pin's identity. Mirrors GpioPinMap.ExtractPortGpio,
    // which already had to tell PA5 from PB5 to keep the simulator's LEDs apart.
    private static readonly Regex PortPin = new(@"^(P[A-K])(\d{1,2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnalogPin = new(@"^A(\d{1,2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PortDotPin = new(@"^P(\d)[._](\d{1,2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// (symbolic token, number) for an MCU pin name. The token is empty when the number alone identifies
    /// the pin — GPIO4, GP25, D13 — which is what every core we target expects there.
    /// </summary>
    private static (string token, int? gpio) ResolvePin(string pin)
    {
        // STM32: "PA5" is port A bit 5. STM32duino defines PA0..PK15; the bare integer is a different pad.
        var st = PortPin.Match(pin);
        if (st.Success && int.TryParse(st.Groups[2].Value, out var sp))
            return (st.Groups[1].Value.ToUpperInvariant() + sp, sp);

        // Arduino: "A0" is a defined constant (14 on an Uno). Bare 0 is the serial TX line.
        var an = AnalogPin.Match(pin);
        if (an.Success && int.TryParse(an.Groups[1].Value, out var ap))
            return ("A" + ap, ap);

        // nRF "P0.13" / "P1.13": the port digit matters and the trailing number alone aliases the two
        // ports together. No Arduino core symbol is portable here, so keep the number and be loud.
        var np = PortDotPin.Match(pin);
        if (np.Success && int.TryParse(np.Groups[2].Value, out var npn))
        {
            if (np.Groups[1].Value != "0")
                Diagnostics.AppLog.Warn("firmware",
                    $"pin '{pin}' is on port {np.Groups[1].Value}; the generated map carries only {npn} and cannot distinguish it from P0.{npn}.");
            return ("", npn);
        }

        var m = GpioNum.Match(pin);
        return ("", m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : (int?)null);
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
