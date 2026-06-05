using System.Diagnostics;
using System.IO.Compression;
using Foundry.Core.Diagnostics;
using Foundry.Core.Pcb;

namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Exports the standard 2-layer fab file set from a routed/DRC-clean <c>.kicad_pcb</c> (Track B v2.6 capstone)
/// and bundles it into a single ZIP in the format board houses (JLCPCB/PCBWay) expect — review before ordering, not a manufacturability guarantee. Gerbers and drill are
/// both native <c>kicad-cli pcb export</c> verbs — this mirrors <see cref="PcbDrc"/>'s kicad-cli invocation
/// (NOT pcbnew python) + pure <c>BuildArgs</c> pattern, and <see cref="DrcReport"/>'s NotInstalled/Parse
/// degrade. Protel extensions + X2 are KEPT (no <c>--no-protel-ext</c>/<c>--no-x2</c>) per the JLCPCB KiCad-9
/// guide. The origin invariant: gerbers <c>--use-drill-file-origin</c> pairs with drill <c>--drill-origin
/// plot</c> so copper and holes align. Returns <see cref="FabExportResult.NotInstalled"/> when kicad-cli is
/// absent — never throws on the normal degrade.
/// </summary>
public static class GerberExporter
{
    /// <summary>
    /// Build the pure <c>kicad-cli pcb export gerbers</c> argument string (unit-testable, exactly per recipe):
    /// the 2-layer layer set into <paramref name="outDir"/>, subtracting soldermask and using the drill-file
    /// origin so the gerbers share the drill's origin. Protel ext + X2 are kept (no opt-out flags emitted).
    /// </summary>
    public static string BuildGerberArgs(string boardPath, string outDir, FabOptions? options = null)
    {
        options ??= FabOptions.Default;
        var parts = new List<string>
        {
            "pcb", "export", "gerbers",
            "--output", Quote(outDir),
            "--layers", Quote(options.Layers),
            "--subtract-soldermask",
            "--use-drill-file-origin",
            Quote(boardPath),
        };
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Build the pure <c>kicad-cli pcb export drill</c> argument string (unit-testable, exactly per recipe):
    /// Excellon, mm, decimal zeros, drill origin <c>plot</c> (shares the gerbers' origin), plated/non-plated
    /// split, with gerberX2 drill maps. The output dir is quoted WITHOUT a trailing separator: a quoted path
    /// ending in a backslash (<c>"...\"</c>) escapes the closing quote under Windows arg parsing, mangling
    /// the argument so kicad-cli rejects it. KiCad 10's drill verb treats the plain dir as a directory fine.
    /// </summary>
    public static string BuildDrillArgs(string boardPath, string outDir, FabOptions? options = null)
    {
        options ??= FabOptions.Default;
        // Strip any trailing separator: a quoted path ending in '\' escapes the closing quote on Windows.
        var dir = outDir.TrimEnd('/', '\\');
        var parts = new List<string>
        {
            "pcb", "export", "drill",
            "--output", Quote(dir),
            "--format", "excellon",
            "--drill-origin", "plot",
            "--excellon-units", "mm",
            "--excellon-zeros-format", "decimal",
        };
        if (options.SeparateTh) parts.Add("--excellon-separate-th");
        if (options.GenerateDrillMap)
        {
            parts.Add("--generate-map");
            parts.Add("--map-format");
            parts.Add("gerberx2");
        }
        parts.Add(Quote(boardPath));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Export gerbers + drill from <paramref name="kicadPcbPath"/> into a temp dir, validate the produced set,
    /// then ZIP the whole dir into <c>&lt;name&gt;-fab.zip</c> under <paramref name="outputDir"/>. Two runs
    /// (gerbers then drill), cancellation propagated. <see cref="FabExportResult.NotInstalled"/> when kicad-cli
    /// can't be located; <see cref="FabExportResult.Failed"/> when the input is missing or a run errors. The
    /// caller owns the input board (it is not modified or deleted). Never throws on the normal degrade.
    /// </summary>
    public static async Task<FabExportResult> ExportAsync(string kicadPcbPath, string outputDir,
        FabOptions? options = null, bool drcClean = false, DrcOptions? drcOptions = null,
        CancellationToken ct = default)
    {
        options ??= FabOptions.Default;

        var kicad = KiCadInstaller.Locate();
        if (kicad is null) return FabExportResult.NotInstalled();

        if (!System.IO.File.Exists(kicadPcbPath))
            return FabExportResult.Failed($"Input board not found: {kicadPcbPath}");

        // Fab gate (defense in depth): never package a board that hasn't passed DRC. Callers that already
        // ran DRC on THIS board (the orchestrator) pass drcClean:true to skip a redundant kicad-cli run;
        // the standalone "Export Gerbers" path leaves it false, so ExportAsync verifies before exporting.
        if (!drcClean)
        {
            var drc = await PcbDrc.CheckAsync(kicadPcbPath, drcOptions, ct);
            if (!drc.Installed) return FabExportResult.NotInstalled();
            if (!drc.Clean)
                return FabExportResult.Failed($"Refusing to export — board is not DRC-clean: {drc.Summary}", drc.Notes);
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(kicadPcbPath);
        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "foundry_fab_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(work);
        try
        {
            AppLog.Info("pcb", $"Fab · exporting gerbers + drill for {System.IO.Path.GetFileName(kicadPcbPath)}");

            var (gOut, gErr, gCode) = await RunAsync(kicad.KicadCliPath, BuildGerberArgs(kicadPcbPath, work, options), ct);
            var (dOut, dErr, dCode) = await RunAsync(kicad.KicadCliPath, BuildDrillArgs(kicadPcbPath, work, options), ct);

            var produced = System.IO.Directory.Exists(work)
                ? System.IO.Directory.GetFiles(work).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            // Package only when both exits are 0 and the produced set validates.
            string? zipPath = null;
            int zipEntries = 0;
            bool exitsOk = gCode == 0 && dCode == 0;
            if (exitsOk && FabFileSet.Validate(produced).Ok)
            {
                System.IO.Directory.CreateDirectory(outputDir);
                zipPath = System.IO.Path.Combine(outputDir, $"{name}-fab.zip");
                if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                ZipFile.CreateFromDirectory(work, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                using (var zip = ZipFile.OpenRead(zipPath)) zipEntries = zip.Entries.Count;
            }

            // The final zip lives in outputDir; report its files by name, not the temp paths.
            var reportFiles = produced.Select(System.IO.Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
            var result = FabExportResult.Parse(
                gCode, string.IsNullOrWhiteSpace(gErr) ? gOut : gErr,
                dCode, string.IsNullOrWhiteSpace(dErr) ? dOut : dErr,
                reportFiles, zipPath, zipEntries);

            AppLog.Info("pcb", result.Ok ? $"Fab ready · {result.Summary}" : $"Fab · {result.Summary}");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("pcb", $"Fab export error: {ex.Message}");
            return FabExportResult.Failed($"Couldn't export fab files: {ex.Message}");
        }
        finally { try { System.IO.Directory.Delete(work, true); } catch { } }
    }

    private static string Quote(string path) => $"\"{path}\"";

    // Thin adapter over the shared ProcessRunner: concurrent stdout/stderr drain (no pipe-buffer deadlock),
    // a timeout, and process-tree kill on timeout/cancel. kicad-cli export is fast → KicadTimeout.
    private static async Task<(string stdout, string stderr, int code)> RunAsync(string exe, string args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, ProcessRunner.KicadTimeout, ct);
        return (r.Stdout, r.Stderr, r.ExitCode);
    }
}
