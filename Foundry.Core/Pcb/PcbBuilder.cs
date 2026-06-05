using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
    public static Task<PcbResult> BuildAsync(Project.Project project, string outputDir,
        PlacementPlan? plan, double marginMm = 5.0, double gapMm = 1.5, CancellationToken ct = default) =>
        BuildAsync(project, outputDir, plan, marginMm, gapMm, null, ct);

    /// <summary>
    /// Build with an explicit plan + knobs AND a pre-measured courtyard map (lib id → real W×H mm). The
    /// <see cref="PcbDesigner"/> fix loop measures ONCE (<see cref="MeasureAsync"/>) and passes the same
    /// <paramref name="realSizes"/> across re-place iterations — footprint geometry doesn't change, only
    /// gap/margin do. When <paramref name="realSizes"/> is null, KiCad is measured here on demand.
    /// </summary>
    public static async Task<PcbResult> BuildAsync(Project.Project project, string outputDir,
        PlacementPlan? plan, double marginMm, double gapMm,
        IReadOnlyDictionary<string, (double WMm, double HMm)>? realSizes, CancellationToken ct = default)
    {
        var kicad = KiCadInstaller.Locate();
        if (kicad is null) return PcbResult.NotInstalled();

        System.IO.Directory.CreateDirectory(outputDir);
        var outPath = System.IO.Path.Combine(outputDir, SafeName(project.Title) + ".kicad_pcb");

        var footprintDirs = System.IO.Directory.Exists(kicad.FootprintDir)
            ? new[] { kicad.FootprintDir }
            : Array.Empty<string>();

        // Real-geometry placement: measure the footprints once (unless the caller already did) so the
        // placer packs using true courtyards instead of CourtyardOf approximations.
        realSizes ??= await MeasureAsync(project, footprintDirs, ct);

        var job = PcbJob.Build(project, outPath, footprintDirs, plan, marginMm, gapMm, realSizes);

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

    /// <summary>
    /// Measure the REAL courtyard size (W×H mm) of every footprint the job will use, via
    /// <c>build_board.py measure</c>. Resolves the distinct lib ids with the same
    /// <see cref="PcbJob.ResolvedLibIds"/> pass the build uses, runs the measure subcommand against
    /// KiCad's Python, and parses <c>sizes</c>. Returns an EMPTY map when KiCad is absent or no footprint
    /// dir is available (offline path) — callers then fall back to <see cref="FootprintMap.CourtyardOf"/>.
    /// A footprint missing from <c>sizes</c> (recorded in the script's <c>notes</c>) is simply absent from
    /// the map, so that one id falls back per-part. Never throws.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, (double WMm, double HMm)>> MeasureAsync(
        Project.Project project, IReadOnlyList<string> footprintDirs, CancellationToken ct = default)
    {
        var empty = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        var kicad = KiCadInstaller.Locate();
        if (kicad is null || footprintDirs.Count == 0) return empty;

        var libIds = PcbJob.ResolvedLibIds(project);
        if (libIds.Count == 0) return empty;

        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_measure_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(work);
        try
        {
            var scriptPath = System.IO.Path.Combine(work, "build_board.py");
            await System.IO.File.WriteAllTextAsync(scriptPath, ReadScript(), ct);

            var jobObj = new { mode = "measure", footprintDirs, libIds };
            var jobPath = System.IO.Path.Combine(work, "measure_job.json");
            await System.IO.File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(jobObj), ct);

            // python build_board.py measure <measure_job.json>
            var (stdout, _, _) = await RunAsync(kicad.PythonPath, $"\"{scriptPath}\" measure \"{jobPath}\"", ct);
            return ParseSizes(stdout);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("pcb", $"footprint measure failed (using approximations): {ex.Message}", null);
            return empty;
        }
        finally { try { System.IO.Directory.Delete(work, true); } catch { } }
    }

    private static IReadOnlyDictionary<string, (double WMm, double HMm)> ParseSizes(string stdout)
    {
        var map = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("{"));
        if (line is null) return map;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("sizes", out var sizes)) return map;
            foreach (var p in sizes.EnumerateObject())
            {
                if (p.Value.TryGetProperty("wMm", out var w) && p.Value.TryGetProperty("hMm", out var h))
                    map[p.Name] = (w.GetDouble(), h.GetDouble());
            }
        }
        catch (JsonException) { /* malformed → fall back to approximations */ }
        return map;
    }

    // Thin adapter over the shared ProcessRunner: concurrent stdout/stderr drain (no pipe-buffer deadlock),
    // a timeout, and process-tree kill on timeout/cancel. kicad/pcbnew are fast → KicadTimeout.
    private static async Task<(string stdout, string stderr, int code)> RunAsync(string exe, string args, CancellationToken ct)
    {
        var r = await Diagnostics.ProcessRunner.RunAsync(exe, args, Diagnostics.ProcessRunner.KicadTimeout, ct);
        return (r.Stdout, r.Stderr, r.ExitCode);
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
