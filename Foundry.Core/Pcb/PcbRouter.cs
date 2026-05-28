using System.Diagnostics;
using System.Reflection;
using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb;

/// <summary>
/// Routes a placed <c>.kicad_pcb</c> (output of <see cref="PcbBuilder.BuildAsync"/>) by exporting a
/// Specctra DSN, running FreeRouting headless, and importing the routed SES back — Track B v2.4.
/// Mirrors <see cref="PcbBuilder"/>'s process-invocation + <see cref="KiCadInstaller"/> usage and the
/// embedded-script-to-temp-dir pattern. Returns <see cref="RouteResult.NotInstalled"/> when KiCad, Java
/// (JRE 21+), or the FreeRouting jar is absent — never throws. No DRC (v2.5), no gerbers (v2.6).
/// </summary>
public static class PcbRouter
{
    private const string ExportScriptResource = "Foundry.Core.Pcb.KiCadScripts.export_dsn.py";
    private const string ImportScriptResource = "Foundry.Core.Pcb.KiCadScripts.import_ses.py";

    /// <summary>
    /// Route <paramref name="kicadPcbPath"/> in place-of-output: writes a <c>.routed.kicad_pcb</c> beside it.
    /// Caller owns the input and output files (neither is deleted). The jar must already be downloaded
    /// (<see cref="FreeRoutingInstaller.DownloadJarAsync"/>) — this method only locates, it does not fetch.
    /// </summary>
    public static async Task<RouteResult> RouteAsync(string kicadPcbPath, RouteOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= RouteOptions.Default;

        var kicad = KiCadInstaller.Locate();
        var routing = FreeRoutingInstaller.Locate();
        if (kicad is null || routing is null) return RouteResult.NotInstalled();

        if (!System.IO.File.Exists(kicadPcbPath))
            return RouteResult.Failed($"Input board not found: {kicadPcbPath}");

        var dir = System.IO.Path.GetDirectoryName(kicadPcbPath) ?? ".";
        var stem = System.IO.Path.GetFileNameWithoutExtension(kicadPcbPath);
        var outPath = System.IO.Path.Combine(dir, stem + ".routed.kicad_pcb");

        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_route_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(work);
        try
        {
            var dsnPath = System.IO.Path.Combine(work, "board.dsn");
            var sesPath = System.IO.Path.Combine(work, "board.ses");

            // 1. export DSN via pcbnew python
            var exportScript = System.IO.Path.Combine(work, "export_dsn.py");
            await System.IO.File.WriteAllTextAsync(exportScript, ReadScript(ExportScriptResource), ct);
            var exportJob = System.IO.Path.Combine(work, "export.json");
            await System.IO.File.WriteAllTextAsync(exportJob,
                Json(new { inPcb = kicadPcbPath, dsn = dsnPath }), ct);

            AppLog.Info("pcb", $"routing · exporting DSN from {System.IO.Path.GetFileName(kicadPcbPath)}");
            var (exo, exe, exc) = await RunAsync(kicad.PythonPath, $"\"{exportScript}\" \"{exportJob}\"", ct);
            if (exc != 0 || !System.IO.File.Exists(dsnPath))
                return RouteResult.Failed("Couldn't export Specctra DSN.",
                    new[] { Trimmed(exo, exe) });

            // 2. run FreeRouting headless
            var args = $"-jar \"{routing.JarPath}\" --gui.enabled=false --logging.console.level=INFO " +
                       $"-de \"{dsnPath}\" -do \"{sesPath}\" -mp {options.Passes} -mt {options.Threads}";
            AppLog.Info("pcb", $"routing · FreeRouting {FreeRoutingInstaller.Version} ({options.Passes} passes, {options.Threads} thread(s))");
            var (fro, fre, frc) = await RunAsync(routing.JavaPath, args, ct);
            if (!System.IO.File.Exists(sesPath))
                return RouteResult.Failed("FreeRouting produced no SES output.",
                    new[] { Trimmed(fro, fre) });
            var routerLog = fro + "\n" + fre;

            // 3. import SES back + save via pcbnew python
            var importScript = System.IO.Path.Combine(work, "import_ses.py");
            await System.IO.File.WriteAllTextAsync(importScript, ReadScript(ImportScriptResource), ct);
            var importJob = System.IO.Path.Combine(work, "import.json");
            await System.IO.File.WriteAllTextAsync(importJob,
                Json(new { inPcb = kicadPcbPath, ses = sesPath, outPcb = outPath }), ct);

            AppLog.Info("pcb", "routing · importing SES");
            var (imo, ime, imc) = await RunAsync(kicad.PythonPath, $"\"{importScript}\" \"{importJob}\"", ct);

            var result = RouteResult.Parse(imo, ime, imc, routerLog, outPath);
            AppLog.Info("pcb", result.Ok ? $"PCB routed · {result.Summary}" : $"PCB routing failed · {result.Notes.Count} note(s)");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("pcb", $"PCB routing error: {ex.Message}");
            return RouteResult.Failed($"Couldn't route the PCB: {ex.Message}");
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

    /// <summary>The named embedded pcbnew script source.</summary>
    public static string ReadScript(string resource)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string Json(object o) =>
        System.Text.Json.JsonSerializer.Serialize(o);

    private static string Trimmed(string stdout, string stderr) =>
        !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
        : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
        : "(no output)";
}
