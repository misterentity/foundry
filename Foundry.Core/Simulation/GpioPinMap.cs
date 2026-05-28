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
public sealed record SimPin(int Gpio, string LedName, string Endpoint, string Net);

/// <summary>
/// Maps the authoritative netlist (Project.Connections + the component KB) onto the set of MCU GPIO
/// lines worth wiring an emulated LED to. Mirrors <see cref="Firmware.PinMap"/>'s MCU detection so the
/// emulated line numbers agree with the generated firmware's pin assignments. Pure / deterministic.
/// </summary>
public static class GpioPinMap
{
    private static readonly Regex GpioNum = new(@"(\d+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Build the list of simulated GPIO lines from signal/i2c nets that land on the detected MCU. One
    /// <see cref="SimPin"/> per used GPIO, ordered by GPIO number for deterministic .repl/.resc output.
    /// </summary>
    public static IReadOnlyList<SimPin> Build(IReadOnlyList<Connection> connections, ComponentKb kb)
    {
        var mcu = Firmware.PinMap.DetectMcuAlias(connections, kb);
        if (mcu is null) return Array.Empty<SimPin>();

        var pins = new List<SimPin>();
        var seen = new HashSet<int>();
        foreach (var c in connections.Where(c => c.Net is "signal" or "i2c"))
        {
            var (mcuEp, periphEp) = MatchMcu(c.From, c.To, mcu);
            if (mcuEp is null || periphEp is null) continue;

            var gpio = ExtractGpio(Pin(mcuEp));
            if (gpio is null || !seen.Add(gpio.Value)) continue;

            pins.Add(new SimPin(gpio.Value, "led" + gpio.Value, periphEp, c.Net));
        }
        return pins.OrderBy(p => p.Gpio).ToList();
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
}
