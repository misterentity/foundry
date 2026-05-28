using System.Text;

namespace Foundry.Core.Simulation;

/// <summary>
/// Generates the <c>foundry.resc</c> run script: creates the machine, loads the generated .repl and the
/// ELF the arduino-cli build already produced, then sets up GPIO readout over the SAME <c>pin=level\n</c>
/// socket contract via one of two interchangeable mechanisms:
/// (B, preferred) <paramref name="usepython"/> attaches a Python <c>StateChanged</c> lambda per LED that
/// pushes edges out a TCP socket the host owns; (A, fallback) Monitor <c>watch</c> polling re-emits
/// <c>sysbus.&lt;led&gt; State</c> every 100 ms. Pure / deterministic — no I/O.
/// </summary>
public static class RenodeRescGenerator
{
    /// <summary>
    /// Build the .resc text.
    /// </summary>
    /// <param name="elfPath">ELF to load (the arduino-cli build artefact).</param>
    /// <param name="pins">The simulated GPIO lines (must match the .repl's LED names).</param>
    /// <param name="hostPort">Host TCP port the .resc's Python socket connects to (Mechanism B).</param>
    /// <param name="usepython">Use Mechanism B (python push) when true; else Mechanism A (watch poll).</param>
    public static string Build(string elfPath, IReadOnlyList<SimPin> pins, int hostPort, bool usepython)
    {
        var elf = (elfPath ?? "").Replace('\\', '/');
        var sb = new StringBuilder();
        sb.AppendLine("using sysbus");
        sb.AppendLine("mach create \"foundry\"");
        sb.AppendLine("machine LoadPlatformDescription @foundry.repl");
        sb.AppendLine($"sysbus LoadELF @{elf}");
        sb.AppendLine();

        if (usepython)
        {
            // Mechanism B — embedded Python opens a socket to the host and pushes every LED edge.
            sb.AppendLine("python \"import socket\"");
            sb.AppendLine("python \"sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)\"");
            sb.AppendLine($"python \"sock.connect(('127.0.0.1', {hostPort}))\"");
            foreach (var p in pins)
            {
                var gpio = p.Gpio;
                sb.AppendLine($"python \"led{gpio} = monitor.Machine['sysbus.{p.LedName}']\"");
                sb.AppendLine(
                    $"python \"led{gpio}.StateChanged += lambda l, s: sock.send(('{gpio}=%d\\n' % (1 if s else 0)).encode())\"");
            }
            sb.AppendLine();
        }
        else
        {
            // Mechanism A — re-emit each LED level every 100 ms over the Monitor socket; the host parses.
            foreach (var p in pins)
                sb.AppendLine($"watch \"sysbus.{p.LedName} State\" 100");
            sb.AppendLine();
        }

        sb.AppendLine("start");
        return sb.ToString();
    }
}
