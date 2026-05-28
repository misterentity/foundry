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
    /// <summary>Maps an FQBN to the GPIO controller node name + the bundled base platform repl include.</summary>
    public static (string gpioNode, string? include) Platform(string fqbn)
    {
        var f = (fqbn ?? "").ToLowerInvariant();
        if (f.StartsWith("stm32") || f.Contains(":stm32"))
            return ("gpioPortA", "platforms/cpus/stm32f4.repl");
        if (f.Contains("rp2040") || f.Contains("pico"))
            return ("gpio", "platforms/cpus/rp2040.repl");
        // Fallback: a generic Cortex-M GPIO node; the .resc may still load a known platform.
        return ("gpio", null);
    }

    /// <summary>
    /// Build the .repl text. <paramref name="fqbn"/> selects the base platform; one LED is wired per
    /// <see cref="SimPin"/>. Returns a self-contained description that the .resc loads.
    /// </summary>
    public static string Build(string fqbn, IReadOnlyList<SimPin> pins)
    {
        var (gpioNode, include) = Platform(fqbn);
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
            // Connect the GPIO line to the LED's input, then declare the LED on the GPIO controller.
            sb.AppendLine($"{gpioNode}:");
            sb.AppendLine($"    {p.Gpio} -> {p.LedName}@0");
            sb.AppendLine();
            sb.AppendLine($"{p.LedName}: Miscellaneous.LED @ {gpioNode} {p.Gpio}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
