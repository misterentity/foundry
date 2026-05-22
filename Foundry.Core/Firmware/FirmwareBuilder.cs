using System.Diagnostics;
using System.Text.Json;

namespace Foundry.Core.Firmware;

public sealed record BuildDiagnostic(string Severity, string File, int Line, string Message)
{
    public string Display => string.IsNullOrEmpty(File) ? Message : $"{File}:{Line}  {Message}";
}

public sealed record BuildResult(bool Installed, bool Compiled, bool Ok, string Summary, IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    public static BuildResult NotInstalled() =>
        new(false, false, false, "arduino-cli isn't installed — install it to verify the build.", Array.Empty<BuildDiagnostic>());
    public static BuildResult Skipped(string why) => new(true, false, true, why, Array.Empty<BuildDiagnostic>());
}

/// <summary>
/// Compiles the generated Arduino firmware with arduino-cli to prove it builds (PRD v2 G1). Locates a
/// PATH or app-local arduino-cli, writes the sketch to a temp folder, compiles for the inferred board,
/// and parses diagnostics. MicroPython is skipped (no compiler). Deterministic; no AI.
/// </summary>
public static class FirmwareBuilder
{
    public static string LocalToolPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry", "tools", "arduino-cli.exe");

    public static string? Locate()
    {
        if (System.IO.File.Exists(LocalToolPath)) return LocalToolPath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try { var p = System.IO.Path.Combine(dir.Trim(), "arduino-cli.exe"); if (System.IO.File.Exists(p)) return p; }
            catch { }
        }
        return null;
    }

    /// <summary>Best-guess Fully Qualified Board Name from the firmware board hint / components.</summary>
    public static string Fqbn(Project.Project p)
    {
        var board = (p.Firmware.Board ?? "").Trim();
        if (board.Count(c => c == ':') == 2) return board;   // already an FQBN

        var hay = (string.Join(" ", p.Components.Select(c => c.Name)) + " " + p.Title + " " + p.Prompt).ToLowerInvariant();
        if (hay.Contains("esp32")) return "esp32:esp32:esp32";
        if (hay.Contains("esp8266") || hay.Contains("nodemcu") || hay.Contains("wemos")) return "esp8266:esp8266:nodemcuv2";
        if (hay.Contains("rp2040") || hay.Contains("pico")) return "rp2040:rp2040:rpipico";
        if (hay.Contains("mega")) return "arduino:avr:mega";
        if (hay.Contains("nano")) return "arduino:avr:nano";
        if (hay.Contains("leonardo") || hay.Contains("32u4")) return "arduino:avr:leonardo";
        return "arduino:avr:uno";   // safe default (smallest core)
    }

    public static async Task<BuildResult> CompileAsync(Project.Project project, CancellationToken ct = default)
    {
        if (project.Firmware.Platform.Contains("python", StringComparison.OrdinalIgnoreCase))
            return BuildResult.Skipped("MicroPython firmware — no compile step (flash to your board to run it).");

        var cli = Locate();
        if (cli is null) return BuildResult.NotInstalled();

        var sketchRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_build_" + Guid.NewGuid().ToString("N")[..8]);
        var sketchDir = System.IO.Path.Combine(sketchRoot, "foundrybuild");
        System.IO.Directory.CreateDirectory(sketchDir);
        try
        {
            // arduino-cli requires the primary .ino to share the sketch folder's name.
            var main = Generation.ProjectGenerator.PickMainFile(project.Firmware.Files);
            foreach (var f in project.Firmware.Files)
            {
                if (!f.Name.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    var name = ReferenceEquals(f, main) ? "foundrybuild.ino" : Safe(f.Name);
                    await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(sketchDir, name), f.Content, ct);
                }
            }

            var fqbn = Fqbn(project);
            await EnsureCoreAsync(cli, fqbn, ct);   // install the board core on first use for this vendor
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = $"compile --fqbn {fqbn} --format json --warnings none \"{sketchDir}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            Diagnostics.AppLog.Info("build", $"compiling firmware · {fqbn}");
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var (ok, diags) = Parse(stdout, stderr, proc.ExitCode);
            var summary = ok
                ? $"Compiled clean for {fqbn}."
                : $"{diags.Count(d => d.Severity == "error")} error(s) compiling for {fqbn}.";
            Diagnostics.AppLog.Info("build", ok ? $"compile OK · {fqbn}" : $"compile failed · {diags.Count} diagnostics");
            return new BuildResult(true, true, ok, summary, diags);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("build", $"compile error: {ex.Message}");
            return new BuildResult(true, true, false, $"Couldn't run the compiler: {ex.Message}", Array.Empty<BuildDiagnostic>());
        }
        finally { try { System.IO.Directory.Delete(sketchRoot, true); } catch { } }
    }

    /// <summary>Ensure the platform core for an FQBN is installed (e.g. esp32:esp32). One-time per vendor.</summary>
    private static async Task EnsureCoreAsync(string cli, string fqbn, CancellationToken ct)
    {
        var parts = fqbn.Split(':');
        if (parts.Length < 2) return;
        var platform = $"{parts[0]}:{parts[1]}";   // vendor:arch
        try
        {
            var list = await RunAsync(cli, "core list", ct);
            if (list.stdout.Contains(platform, StringComparison.OrdinalIgnoreCase)) return;
            Diagnostics.AppLog.Info("build", $"installing board core {platform} (one-time)…");
            await RunAsync(cli, "core update-index", ct);
            await RunAsync(cli, $"core install {platform}", ct);
        }
        catch { /* compile will surface a clear "platform not installed" error if this fails */ }
    }

    private static async Task<(string stdout, string stderr, int code)> RunAsync(string cli, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = cli, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = await p.StandardOutput.ReadToEndAsync(ct);
        var e = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (o, e, p.ExitCode);
    }

    /// <summary>Download arduino-cli to the app-local tools folder (PRD v2 G1 on-demand install). Returns the path.</summary>
    public static async Task<string> DownloadCliAsync(CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(LocalToolPath)!;
        System.IO.Directory.CreateDirectory(dir);
        var zip = System.IO.Path.Combine(dir, "arduino-cli.zip");
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            var bytes = await http.GetByteArrayAsync("https://downloads.arduino.cc/arduino-cli/arduino-cli_latest_Windows_64bit.zip", ct);
            await System.IO.File.WriteAllBytesAsync(zip, bytes, ct);
        }
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        try { System.IO.File.Delete(zip); } catch { }
        if (!System.IO.File.Exists(LocalToolPath)) throw new InvalidOperationException("arduino-cli.exe not found after download.");
        await RunAsync(LocalToolPath, "core update-index", ct);
        Diagnostics.AppLog.Info("build", "arduino-cli installed to app tools folder");
        return LocalToolPath;
    }

    /// <summary>Parse arduino-cli compile output (json when available, else stderr) into diagnostics.</summary>
    public static (bool ok, List<BuildDiagnostic> diags) Parse(string stdout, string stderr, int exitCode)
    {
        var diags = new List<BuildDiagnostic>();
        bool ok = exitCode == 0;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdout) ? "{}" : stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ok = s.GetBoolean();
            JsonElement br = root.TryGetProperty("builder_result", out var b) ? b : root;
            if (br.TryGetProperty("diagnostics", out var da) && da.ValueKind == JsonValueKind.Array)
                foreach (var d in da.EnumerateArray())
                {
                    var sev = (Str(d, "severity") ?? "error").ToLowerInvariant();
                    diags.Add(new BuildDiagnostic(sev.Contains("err") ? "error" : sev.Contains("warn") ? "warning" : sev,
                        ShortFile(Str(d, "file")), d.TryGetProperty("line", out var ln) && ln.TryGetInt32(out var l) ? l : 0,
                        (Str(d, "message") ?? "").Trim()));
                }
        }
        catch { /* not json — fall back to stderr scrape */ }

        if (diags.Count == 0 && !ok)
        {
            foreach (var line in (stderr ?? "").Split('\n'))
            {
                var t = line.Trim();
                if (t.Contains(": error:", StringComparison.OrdinalIgnoreCase))
                    diags.Add(new BuildDiagnostic("error", "", 0, t));
            }
            if (diags.Count == 0) diags.Add(new BuildDiagnostic("error", "", 0, string.IsNullOrWhiteSpace(stderr) ? "Compile failed." : stderr.Trim()));
        }
        return (ok, diags);
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string ShortFile(string? f) => string.IsNullOrEmpty(f) ? "" : System.IO.Path.GetFileName(f);
    private static string Safe(string name)
    {
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.h" : name;
    }
}
