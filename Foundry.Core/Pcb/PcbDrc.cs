using System.Diagnostics;
using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb;

/// <summary>
/// Runs <c>kicad-cli pcb drc</c> on a routed <c>.kicad_pcb</c> as the deterministic GATE (Track B v2.5).
/// DRC is a native CLI verb — this invokes <see cref="KiCadInstaller.Install.KicadCliPath"/> (NOT the
/// bundled python). Writes the JSON report to a temp file (DRC does not stream JSON to stdout), reads it,
/// and parses a <see cref="DrcReport"/>. Returns <see cref="DrcReport.NotInstalled"/> when KiCad is absent
/// — never throws. Mirrors <see cref="PcbRouter"/>'s process-invocation + <see cref="KiCadInstaller"/> usage.
/// No gerbers (v2.6).
/// </summary>
public static class PcbDrc
{
    /// <summary>
    /// DRC <paramref name="kicadPcbPath"/> and return the parsed report. <see cref="DrcReport.NotInstalled"/>
    /// when kicad-cli can't be located; <see cref="DrcReport.Failed"/> when the input is missing or the run
    /// errors. The caller owns the input file (it is not modified or deleted).
    /// </summary>
    public static async Task<DrcReport> CheckAsync(string kicadPcbPath, DrcOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= DrcOptions.Default;

        var kicad = KiCadInstaller.Locate();
        if (kicad is null) return DrcReport.NotInstalled();

        if (!System.IO.File.Exists(kicadPcbPath))
            return DrcReport.Failed($"Input board not found: {kicadPcbPath}");

        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_drc_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(work);
        try
        {
            var reportPath = System.IO.Path.Combine(work, "board.drc.json");
            var args = BuildArgs(kicadPcbPath, reportPath, options);

            AppLog.Info("pcb", $"DRC · checking {System.IO.Path.GetFileName(kicadPcbPath)}{(options.Strict ? " (strict)" : "")}");
            var (stdout, stderr, code) = await RunAsync(kicad.KicadCliPath, args, ct);

            var reportText = System.IO.File.Exists(reportPath) ? await System.IO.File.ReadAllTextAsync(reportPath, ct) : null;
            var report = DrcReport.Parse(reportText, code, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);

            AppLog.Info("pcb", report.Clean ? $"DRC clean · {report.Summary}" : $"DRC · {report.Summary}");
            return report;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("pcb", $"DRC error: {ex.Message}");
            return DrcReport.Failed($"Couldn't run DRC: {ex.Message}");
        }
        finally { try { System.IO.Directory.Delete(work, true); } catch { } }
    }

    /// <summary>
    /// Build the <c>kicad-cli</c> argument string (pure, unit-testable): always
    /// <c>pcb drc --format json --output "&lt;report&gt;" --severity-error --exit-code-violations "&lt;board&gt;"</c>.
    /// <see cref="DrcOptions.Strict"/> adds <c>--severity-warning</c> so warnings gate too; a non-default
    /// <see cref="DrcOptions.Units"/> adds <c>--units &lt;u&gt;</c>. Schematic-parity is deliberately OMITTED
    /// (no .kicad_sch in Track B — enabling it would spuriously false-fail).
    /// </summary>
    public static string BuildArgs(string boardPath, string reportPath, DrcOptions? options = null)
    {
        options ??= DrcOptions.Default;
        var parts = new List<string>
        {
            "pcb", "drc",
            "--format", "json",
            "--output", Quote(reportPath),
            "--severity-error",
        };
        if (options.Strict) parts.Add("--severity-warning");
        if (!string.IsNullOrWhiteSpace(options.Units) && !options.Units.Equals("mm", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("--units");
            parts.Add(options.Units);
        }
        parts.Add("--exit-code-violations");
        parts.Add(Quote(boardPath));
        return string.Join(" ", parts);
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static async Task<(string stdout, string stderr, int code)> RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var o = await p.StandardOutput.ReadToEndAsync(ct);
        var e = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (o, e, p.ExitCode);
    }
}
