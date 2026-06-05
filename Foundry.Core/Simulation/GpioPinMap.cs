using System.Text.RegularExpressions;
using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Core.Simulation;

/// <summary>
/// One simulated GPIO line of interest: an MCU output pin that the emulator terminates in a
/// <c>Miscellaneous.LED</c> peripheral so its level can be streamed out. <see cref="LedName"/> is the
/// per-line peripheral name baked into the generated .repl/.resc (e.g. "led13"); <see cref="Endpoint"/>
/// is the peripheral netlist endpoint ("alias.pin") this GPIO drives, and <see cref="Net"/> its net class.
/// </summary>
public sealed record SimPin(int Gpio, string LedName, string Endpoint, string Net, string Port = "");

/// <summary>
/// Maps the authoritative netlist (Project.Connections + the component KB) onto the set of MCU GPIO
/// lines worth wiring an emulated LED to. Mirrors <see cref="Firmware.PinMap"/>'s MCU detection so the
/// emulated line numbers agree with the generated firmware's pin assignments. Pure / deterministic.
/// </summary>
public static class GpioPinMap
{
    private static readonly Regex GpioNum = new(@"(\d+)\s*$", RegexOptions.Compiled);
    // STM32 port+pin, e.g. "PB5" -> port "B", pin 5. (nRF "P0.28" is handled by the trailing-number path.)
    private static readonly Regex Stm32Pin = new(@"^P([A-K])(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Build the list of simulated GPIO lines from signal/i2c nets that land on the detected MCU. One
    /// <see cref="SimPin"/> per used GPIO, ordered by GPIO number for deterministic .repl/.resc output.
    /// </summary>
    public static IReadOnlyList<SimPin> Build(IReadOnlyList<Connection> connections, ComponentKb kb)
    {
        var mcu = Firmware.PinMap.DetectMcuAlias(connections, kb);
        if (mcu is null) return Array.Empty<SimPin>();

        var pins = new List<SimPin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections.Where(c => c.Net is "signal" or "i2c"))
        {
            var (mcuEp, periphEp) = MatchMcu(c.From, c.To, mcu);
            if (mcuEp is null || periphEp is null) continue;

            var (port, gpio) = ExtractPortGpio(Pin(mcuEp));
            if (gpio is null || !seen.Add($"{port}:{gpio}")) continue;   // dedup by (port,pin) so PA5 ≠ PB5

            var led = port.Length == 0 ? "led" + gpio.Value : "led" + port + gpio.Value;
            pins.Add(new SimPin(gpio.Value, led, periphEp, c.Net, port));
        }
        return pins.OrderBy(p => p.Port).ThenBy(p => p.Gpio).ToList();
    }

    /// <summary>Find the <see cref="SimPin"/> for a GPIO number, or null when that line isn't simulated.</summary>
    public static SimPin? Resolve(IReadOnlyList<SimPin> pins, int gpio) =>
        pins.FirstOrDefault(p => p.Gpio == gpio);

    // ---- helpers (mirror PinMap's endpoint parsing) ----
    private static (string? mcuEp, string? periphEp) MatchMcu(string from, string to, string mcu)
    {
        if (Alias(from).Equals(mcu, StringComparison.OrdinalIgnoreCase)) return (from, to);
        if (Alias(to).Equals(mcu, StringComparison.OrdinalIgnoreCase)) return (to, from);
        return (null, null);
    }

    /// <summary>(port, pin) for an MCU pin name: STM32 "PB5" → ("B", 5); everything else → ("", trailing number).</summary>
    private static (string port, int? gpio) ExtractPortGpio(string pin)
    {
        var st = Stm32Pin.Match(pin);
        if (st.Success && int.TryParse(st.Groups[2].Value, out var sp))
            return (st.Groups[1].Value.ToUpperInvariant(), sp);
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
}
