using System.Diagnostics;
using System.Reflection;
using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb;

/// <summary>
/// Builds a <c>.kicad_pcb</c> from the project's authoritative netlist (Track B v2.2) — the
/// <see cref="Foundry.Core.Firmware.FirmwareBuilder"/> analogue for PCB. Locates KiCad, builds a pure
/// <see cref="PcbJob"/> (nets via <see cref="Fabrication.KiCadNetlist"/>, footprints via
/// <see cref="FootprintMap"/>, grid placement), writes the embedded <c>build_board.py</c> + job JSON to
/// a temp dir, runs the script against KiCad's bundled Python, and parses the result. Returns
/// <see cref="PcbResult.NotInstalled"/> when KiCad is absent — never throws. Grid placement only:
/// no autorouting, no DRC, no gerbers (later phases).
/// </summary>
public static class PcbBuilder
{
    private const string ScriptResource = "Foundry.Core.Pcb.KiCadScripts.build_board.py";

    /// <summary>
    /// Build a <c>.kicad_pcb</c> under <paramref name="outputDir"/> (owned by the caller — NOT deleted).
    /// Reuses the existing netlist nets and assigns a footprint per component, placed on a simple grid.
    /// </summary>
    public static async Task<PcbResult> BuildAsync(Project.Project project, string outputDir,
        Ai.IAnthropicClient? ai = null, string? model = null, CancellationToken ct = default)
    {
        // AI placement is opt-in and never blocks the board: keyed → ask for a plan; otherwise (or on any
        // failure) PlanAsync returns PlacementPlan.Empty and the placer falls back to the tidy grid.
        PlacementPlan? plan = null;
        if (ai is { HasKey: true })
            plan = await new PcbPlanner(ai, model).PlanAsync(project, ct);

        return await BuildAsync(project, outputDir, plan, ct: ct);
    }

    /// <summary>
    /// Build with an EXPLICIT placement plan + placer knobs (no AI call) — used by the v2.5
    /// <see cref="PcbDesigner"/> fix loop so it owns the plan + gap/margin across re-place iterations.
    /// <paramref name="plan"/> null/Empty degrades to the tidy grid (= v2.2). NEVER throws.
    /// </summary>
    public static async Task<PcbResult> BuildAsync(Project.Project project, string outputDir,
        PlacementPlan? plan, double marginMm = 5.0, double gapMm = 1.5, CancellationToken ct = default)
    {
        var kicad = KiCadInstaller.Locate();
        if (kicad is null) return PcbResult.NotInstalled();

        System.IO.Directory.CreateDirectory(outputDir);
        var outPath = System.IO.Path.Combine(outputDir, SafeName(project.Title) + ".kicad_pcb");

        var footprintDirs = System.IO.Directory.Exists(kicad.FootprintDir)
            ? new[] { kicad.FootprintDir }
            : Array.Empty<string>();

        var job = PcbJob.Build(project, outPath, footprintDirs, plan, marginMm, gapMm);

        // Surface job-time diagnostics (unresolved nodes, generic-footprint fallbacks) in the log up front.
        foreach (var d in job.Diagnostics)
            (d.Severity == "error" ? (Action<string, string, string?>)AppLog.Error : AppLog.Warn)("pcb", d.Message, null);

        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_pcb_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(work);
        try
        {
            var scriptPath = System.IO.Path.Combine(work, "build_board.py");
            await System.IO.File.WriteAllTextAsync(scriptPath, ReadScript(), ct);
            var jobPath = System.IO.Path.Combine(work, "job.json");
            await System.IO.File.WriteAllTextAsync(jobPath, job.ToJson(), ct);

            AppLog.Info("pcb", $"building PCB · {job.Components.Count} parts · {job.Nets.Count} nets");
            var (stdout, stderr, code) = await RunAsync(kicad.PythonPath, $"\"{scriptPath}\" \"{jobPath}\"", ct);

            var result = PcbResult.Parse(stdout, stderr, code, outPath);
            // fold the pure job diagnostics into the result notes (script notes already parsed in)
            var notes = job.Diagnostics.Select(d => d.Message).Concat(result.Notes).Distinct().ToList();
            result = result with { Notes = notes };

            AppLog.Info("pcb", result.Ok ? $"PCB built · {outPath}" : $"PCB build failed · {result.Notes.Count} note(s)");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("pcb", $"PCB build error: {ex.Message}");
            return PcbResult.Failed($"Couldn't build the PCB: {ex.Message}");
        }
        finally { try { System.IO.Directory.Delete(work, true); } catch { } }
    }

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

    /// <summary>The embedded build_board.py source.</summary>
    public static string ReadScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException($"Embedded resource '{ScriptResource}' not found.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string SafeName(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "board" : title.Trim();
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Replace(' ', '_');
    }
}
