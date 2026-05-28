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
/// A compiled firmware image on disk for the inferred board. Consumed by the emulator (Renode loads the
/// ELF) and the one-click flasher (arduino-cli uploads from <see cref="BuildDir"/>). <see cref="BuildDir"/>
/// is owned by the caller (NOT deleted) so it survives long enough to be flashed/loaded.
/// </summary>
public sealed record CompiledImage(
    bool Ok, string Fqbn, string? ElfPath, string? HexPath, string? BinPath, string BuildDir, IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    public bool HasElf => Ok && !string.IsNullOrEmpty(ElfPath) && System.IO.File.Exists(ElfPath);
    public bool HasHex => Ok && !string.IsNullOrEmpty(HexPath) && System.IO.File.Exists(HexPath);
}

/// <summary>A board arduino-cli sees on a serial port. <see cref="Fqbn"/> is null when the port is connected but unidentified.</summary>
public sealed record DetectedBoard(string Port, string? Fqbn, string Label);

/// <summary>Result of a one-click flash, mirroring <see cref="BuildResult"/>'s installed/ok/summary shape.</summary>
public sealed record UploadResult(bool Installed, bool Ok, string Summary, string Detail)
{
    public static UploadResult NotInstalled() =>
        new(false, false, "arduino-cli isn't installed — install it to flash the board.", "");
    public static UploadResult NoBoard() =>
        new(true, false, "No board detected — plug in your board over USB and try again.", "");
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

    /// <summary>
    /// Best-guess Fully Qualified Board Name. Inference from the actual components is primary (the AI
    /// often parrots the prompt's example board field); the firmware board hint is only a fallback for
    /// chips the keyword inference doesn't recognise.
    /// </summary>
    public static string Fqbn(Project.Project p)
    {
        var hay = (string.Join(" ", p.Components.Select(c => c.Name)) + " " + p.Title + " " + p.Prompt).ToLowerInvariant();
        string? inferred =
            hay.Contains("esp32") ? "esp32:esp32:esp32" :
            (hay.Contains("esp8266") || hay.Contains("nodemcu") || hay.Contains("wemos")) ? "esp8266:esp8266:nodemcuv2" :
            (hay.Contains("rp2040") || hay.Contains("pico")) ? "rp2040:rp2040:rpipico" :
            hay.Contains("mega") ? "arduino:avr:mega" :
            hay.Contains("nano") ? "arduino:avr:nano" :
            (hay.Contains("leonardo") || hay.Contains("32u4")) ? "arduino:avr:leonardo" :
            (hay.Contains("uno") || hay.Contains("atmega328")) ? "arduino:avr:uno" : null;
        if (inferred is not null) return inferred;

        var board = (p.Firmware.Board ?? "").Trim();
        if (board.Count(c => c == ':') == 2) return board;   // explicit FQBN for an unrecognised chip
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

    /// <summary>
    /// Compile the firmware to a real image under <paramref name="outputDir"/> and resolve the produced
    /// ELF/HEX/BIN for the inferred FQBN. Like <see cref="CompileAsync"/> but adds <c>--output-dir</c> and
    /// does NOT delete <paramref name="outputDir"/> — the emulator and flasher consume the artefacts.
    /// </summary>
    public static async Task<CompiledImage> CompileToImageAsync(Project.Project project, string outputDir, CancellationToken ct = default)
    {
        var fqbn = Fqbn(project);
        if (project.Firmware.Platform.Contains("python", StringComparison.OrdinalIgnoreCase))
            return new CompiledImage(false, fqbn, null, null, null, outputDir,
                new[] { new BuildDiagnostic("error", "", 0, "MicroPython firmware has no compiled image — flash to your board to run it.") });

        var cli = Locate();
        if (cli is null)
            return new CompiledImage(false, fqbn, null, null, null, outputDir,
                new[] { new BuildDiagnostic("error", "", 0, "arduino-cli isn't installed.") });

        System.IO.Directory.CreateDirectory(outputDir);
        var sketchRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_image_" + Guid.NewGuid().ToString("N")[..8]);
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

            await EnsureCoreAsync(cli, fqbn, ct);
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = $"compile --fqbn {fqbn} --output-dir \"{outputDir}\" --format json --warnings none \"{sketchDir}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            Diagnostics.AppLog.Info("build", $"building firmware image · {fqbn}");
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var (ok, diags) = Parse(stdout, stderr, proc.ExitCode);
            var elf = ok ? FindArtifact(outputDir, ".ino.elf") : null;
            var hex = ok ? FindArtifact(outputDir, ".ino.hex") : null;
            var bin = ok ? FindArtifact(outputDir, ".ino.bin") : null;
            Diagnostics.AppLog.Info("build", ok ? $"image built · {fqbn} · elf={(elf is not null)}" : $"image build failed · {diags.Count} diagnostics");
            return new CompiledImage(ok, fqbn, elf, hex, bin, outputDir, diags);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("build", $"image build error: {ex.Message}");
            return new CompiledImage(false, fqbn, null, null, null, outputDir,
                new[] { new BuildDiagnostic("error", "", 0, $"Couldn't run the compiler: {ex.Message}") });
        }
        finally { try { System.IO.Directory.Delete(sketchRoot, true); } catch { } }
    }

    /// <summary>Resolve the first artefact in <paramref name="dir"/> matching <c>*{suffix}</c> (e.g. ".ino.elf"), or null.</summary>
    private static string? FindArtifact(string dir, string suffix)
    {
        try
        {
            return System.IO.Directory.EnumerateFiles(dir, "*" + suffix, System.IO.SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Parse <c>arduino-cli board list --format json</c> into the connected ports/boards. Pure.</summary>
    public static IReadOnlyList<DetectedBoard> ParseBoardList(string json)
    {
        var boards = new List<DetectedBoard>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            // Newer arduino-cli wraps the array as { "detected_ports": [...] }; older versions return a bare array.
            JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("detected_ports", out var dp) && dp.ValueKind == JsonValueKind.Array ? dp
                : default;
            if (arr.ValueKind != JsonValueKind.Array) return boards;

            foreach (var entry in arr.EnumerateArray())
            {
                var portEl = entry.TryGetProperty("port", out var pe) ? pe : entry;
                var address = Str(portEl, "address") ?? Str(portEl, "label");
                if (string.IsNullOrEmpty(address)) continue;
                var portLabel = Str(portEl, "label") ?? address;

                string? fqbn = null, boardName = null;
                if (entry.TryGetProperty("matching_boards", out var mb) && mb.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mb.EnumerateArray())
                    {
                        fqbn = Str(m, "fqbn"); boardName = Str(m, "name");
                        if (fqbn is not null) break;
                    }
                }

                var label = boardName is not null ? $"{boardName} ({portLabel})" : $"Unknown board ({portLabel})";
                boards.Add(new DetectedBoard(address, fqbn, label));
            }
        }
        catch { /* malformed json — return whatever parsed */ }
        return boards;
    }

    /// <summary>List the boards arduino-cli currently sees on serial ports. Empty when none / not installed.</summary>
    public static async Task<IReadOnlyList<DetectedBoard>> ListBoardsAsync(CancellationToken ct = default)
    {
        var cli = Locate();
        if (cli is null) return Array.Empty<DetectedBoard>();
        try
        {
            var (stdout, _, _) = await RunAsync(cli, "board list --format json", ct);
            return ParseBoardList(stdout);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("flash", $"board list error: {ex.Message}");
            return Array.Empty<DetectedBoard>();
        }
    }

    /// <summary>
    /// The candidate ports/boards a picker can show. Boards whose vendor matches the inferred FQBN are
    /// ordered first, then identified boards, then bare ports — so the UI's default selection is the
    /// most-likely target for this project.
    /// </summary>
    public static async Task<IReadOnlyList<DetectedBoard>> DetectPortsAsync(Project.Project project, CancellationToken ct = default)
    {
        var boards = await ListBoardsAsync(ct);
        if (boards.Count <= 1) return boards;

        var wantVendor = Fqbn(project).Split(':') is { Length: >= 1 } parts ? parts[0] : "";
        return boards
            .OrderByDescending(b => b.Fqbn is not null && wantVendor.Length > 0 && b.Fqbn.StartsWith(wantVendor + ":", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(b => b.Fqbn is not null)
            .ToList();
    }

    /// <summary>
    /// One-click flash: pick a target board (caller-supplied or auto-detected), compile, and run
    /// <c>arduino-cli upload</c> against it. Returns a structured result mirroring <see cref="BuildResult"/>.
    /// When <paramref name="target"/> is null the most-likely connected board is used; ambiguity should be
    /// resolved up front via <see cref="DetectPortsAsync"/>.
    /// </summary>
    public static async Task<UploadResult> UploadAsync(Project.Project project, DetectedBoard? target, CancellationToken ct = default)
    {
        var cli = Locate();
        if (cli is null) return UploadResult.NotInstalled();

        if (project.Firmware.Platform.Contains("python", StringComparison.OrdinalIgnoreCase))
            return new UploadResult(true, false, "MicroPython firmware — copy the .py to your board instead of flashing.", "");

        var board = target;
        if (board is null)
        {
            var detected = await DetectPortsAsync(project, ct);
            board = detected.FirstOrDefault();
            if (board is null) return UploadResult.NoBoard();
        }

        // Prefer the project's inferred FQBN; fall back to whatever the port reported.
        var fqbn = Fqbn(project);
        if (fqbn == "arduino:avr:uno" && board.Fqbn is not null) fqbn = board.Fqbn;   // trust the port over the safe default

        var buildDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_flash_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var image = await CompileToImageAsync(project, buildDir, ct);
            if (!image.Ok)
            {
                var firstErr = image.Diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message ?? "compile failed";
                return new UploadResult(true, false, $"Won't flash — firmware didn't compile for {fqbn}.", firstErr);
            }

            await EnsureCoreAsync(cli, fqbn, ct);
            // --input-dir points arduino-cli at the artefacts CompileToImageAsync already produced (no recompile).
            var args = $"upload -p {board.Port} --fqbn {fqbn} --input-dir \"{buildDir}\" --format json";
            Diagnostics.AppLog.Info("flash", $"flashing {fqbn} → {board.Port}");
            var (stdout, stderr, code) = await RunAsync(cli, args, ct);

            if (code == 0)
                return new UploadResult(true, true, $"Flashed {fqbn} to {board.Port}.", board.Label);

            var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : "upload failed";
            Diagnostics.AppLog.Error("flash", $"upload failed · {detail}");
            return new UploadResult(true, false, $"Flash failed for {board.Port}.", detail);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("flash", $"upload error: {ex.Message}");
            return new UploadResult(true, false, $"Couldn't flash the board: {ex.Message}", "");
        }
        finally { try { System.IO.Directory.Delete(buildDir, true); } catch { } }
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
