using System.Text;

namespace Foundry.Core.Simulation;

/// <summary>
/// Generates the <c>foundry.repl</c> platform description: it picks the chip's base .repl by FQBN and
/// terminates each simulated GPIO line in a <c>Miscellaneous.LED</c> so its level can be read/streamed.
/// Pattern per line: <c>&lt;gpio&gt; N -&gt; ledX@0</c> then <c>ledX: Miscellaneous.LED @ gpio N</c>.
/// Pure / deterministic — no I/O.
/// </summary>
public static class RenodeReplGenerator
{
    /// <summary>Maps an FQBN to the GPIO-controller node + the base platform repl include. <c>perPort</c> means
    /// the node name is per-pin (<c>gpioNode</c> + the pin's port letter, e.g. STM32 PB5 → <c>gpioPortB</c>);
    /// otherwise <c>gpioNode</c> is a single fixed controller for every line.</summary>
    public static (string gpioNode, string? include, bool perPort) Platform(string fqbn)
    {
        var f = (fqbn ?? "").ToLowerInvariant();
        if (f.StartsWith("stm32") || f.Contains(":stm32"))
            return ("gpioPort", "platforms/cpus/stm32f4.repl", true);   // node = gpioPort + <PortLetter> per pin
        if (f.Contains("rp2040") || f.Contains("pico"))
            // Stock Renode does NOT ship rp2040.repl (it's the community matgla model, not bundled). Emit no
            // include so a future un-gated run fails loudly with "no platform" rather than silently mis-wiring.
            return ("gpio", null, false);
        return ("gpio", null, false);
    }

    /// <summary>
    /// Build the .repl text. <paramref name="fqbn"/> selects the base platform; one LED is wired per
    /// <see cref="SimPin"/> on the pin's own GPIO controller (per-port for STM32). Self-contained for the .resc.
    /// </summary>
    public static string Build(string fqbn, IReadOnlyList<SimPin> pins)
    {
        var (gpioNode, include, perPort) = Platform(fqbn);
        var sb = new StringBuilder();
        sb.AppendLine("// GENERATED — Foundry simulation platform");
        sb.AppendLine($"// fqbn: {fqbn}");
        if (include is not null)
        {
            sb.AppendLine($"using \"{include}\"");
            sb.AppendLine();
        }
        foreach (var p in pins)
        {
            // Per-port node for STM32 (gpioPortB), single fixed node otherwise — so PA5 and PB5 don't collide.
            var node = perPort ? gpioNode + p.Port : gpioNode;
            sb.AppendLine($"{node}:");
            sb.AppendLine($"    {p.Gpio} -> {p.LedName}@0");
            sb.AppendLine();
            sb.AppendLine($"{p.LedName}: Miscellaneous.LED @ {node} {p.Gpio}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
