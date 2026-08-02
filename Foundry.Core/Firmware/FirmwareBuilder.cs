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
/// A compiled firmware image on disk for a resolved board FQBN. Consumed by the emulator (Renode loads the
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
    public const string ArduinoCliVersion = "1.5.1";
    public const string ArduinoCliZipName = "arduino-cli_1.5.1_Windows_64bit.zip";
    public const string ArduinoCliSha256 = "FABE42E0EB04D00E776A66178299FF95A46C623DBC260F997E58FD514853DD40";
    public const string ArduinoCliUrl =
        "https://github.com/arduino/arduino-cli/releases/download/v1.5.1/arduino-cli_1.5.1_Windows_64bit.zip";

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
        // Honor an explicit FQBN for an unrecognised chip, but ONLY when it's a clean, valid FQBN — the board
        // hint is AI-controlled and flows into `arduino-cli compile --fqbn {fqbn}`, so a value with embedded
        // spaces/flags (e.g. "...uno --additional-urls http://evil") could inject an attacker package index and
        // run code at compile time. IsValidFqbn rejects anything but vendor:arch:board[:menu] tokens.
        if (board.Count(c => c == ':') == 2 && IsValidFqbn(board)) return board;
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
            // Defense in depth: the FQBN is interpolated into the arduino-cli command line — never pass an
            // unvalidated value (Fqbn already sanitises, but guard the exec site against any future regression).
            if (!IsValidFqbn(fqbn))
                return new BuildResult(true, true, false, $"Refusing to compile — unsafe board id '{fqbn}'.", Array.Empty<BuildDiagnostic>());
            if (await EnsureCoreAsync(cli, fqbn, ct) is { } coreError)   // board core, incl. third-party indexes
                return new BuildResult(true, true, false, coreError, Array.Empty<BuildDiagnostic>());
            await EnsureLibrariesAsync(cli, project, ct);   // the sketch's declared #include dependencies
            Diagnostics.AppLog.Info("build", $"compiling firmware · {fqbn}");
            var run = await Diagnostics.ProcessRunner.RunAsync(cli,
                $"compile --fqbn {fqbn} --format json --warnings none \"{sketchDir}\"",
                Diagnostics.ProcessRunner.ArduinoTimeout, ct);

            var (ok, diags) = Parse(run.Stdout, run.Stderr, run.ExitCode);
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
    /// ELF/HEX/BIN for the resolved FQBN. Like <see cref="CompileAsync"/> but adds <c>--output-dir</c> and
    /// does NOT delete <paramref name="outputDir"/> — the emulator and flasher consume the artefacts.
    /// </summary>
    public static async Task<CompiledImage> CompileToImageAsync(Project.Project project, string outputDir,
        CancellationToken ct = default, string? fqbnOverride = null)
    {
        var fqbn = string.IsNullOrWhiteSpace(fqbnOverride) ? Fqbn(project) : fqbnOverride.Trim();
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

            if (!IsValidFqbn(fqbn))   // defense in depth: never interpolate an unvalidated board id into the CLI
                return new CompiledImage(false, fqbn, null, null, null, outputDir,
                    new[] { new BuildDiagnostic("error", "", 0, $"Refusing to compile — unsafe board id '{fqbn}'.") });
            if (await EnsureCoreAsync(cli, fqbn, ct) is { } coreError)
                return new CompiledImage(false, fqbn, null, null, null, outputDir,
                    new[] { new BuildDiagnostic("error", "", 0, coreError) });
            await EnsureLibrariesAsync(cli, project, ct);
            Diagnostics.AppLog.Info("build", $"building firmware image · {fqbn}");
            var run = await Diagnostics.ProcessRunner.RunAsync(cli,
                $"compile --fqbn {fqbn} --output-dir \"{outputDir}\" --format json --warnings none \"{sketchDir}\"",
                Diagnostics.ProcessRunner.ArduinoTimeout, ct);

            var (ok, diags) = Parse(run.Stdout, run.Stderr, run.ExitCode);
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

    /// <summary>Where the resolved flash FQBN came from — for the confirm dialog and tests.</summary>
    public enum FqbnSource { Inferred, ExactMatch, PortPreferredOverInferred }

    /// <summary>A vetted, ready-to-confirm flash plan: the exact port + resolved FQBN that will be written,
    /// whether the connected board is a DIFFERENT family than the firmware (brick risk), and the human text
    /// the UI must show before the (irreversible) write.</summary>
    public sealed record FlashPlan(string Port, string Fqbn, FqbnSource Source, bool VendorMismatch,
        string ConfirmText, string? MismatchWarning);

    public static string VendorOf(string fqbn) =>
        !string.IsNullOrEmpty(fqbn) && fqbn.Split(':') is { Length: >= 1 } p ? p[0] : "";

    /// <summary>Strict FQBN shape (vendor:arch:board[:opts]) — rejects spaces/metacharacters so the value is
    /// safe to pass to arduino-cli and can't smuggle extra arguments.</summary>
    public static bool IsValidFqbn(string fqbn) =>
        !string.IsNullOrWhiteSpace(fqbn) &&
        System.Text.RegularExpressions.Regex.IsMatch(fqbn, @"^[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+:[A-Za-z0-9_.:=,-]+$");

    /// <summary>Strict serial-port shape (COMn or /dev/...).</summary>
    public static bool IsValidPort(string port) =>
        !string.IsNullOrWhiteSpace(port) &&
        System.Text.RegularExpressions.Regex.IsMatch(port, @"^(COM\d+|/dev/[A-Za-z0-9/._-]+)$");

    /// <summary>
    /// Resolve which FQBN will actually be written to <paramref name="board"/> and whether that's a
    /// cross-family mismatch. Rule: when the connected board reports a CONCRETE FQBN, the PHYSICAL board wins
    /// (you can't safely flash an ESP32 image onto an AVR); the inferred FQBN is only a fallback for an
    /// unidentified port. A different vendor family than the firmware was written for is flagged as a brick risk.
    /// </summary>
    public static FlashPlan BuildFlashPlan(Project.Project project, DetectedBoard board)
    {
        var inferred = Fqbn(project);
        string fqbn; FqbnSource source; bool mismatch = false; string? warn = null;

        if (board.Fqbn is null)
        {
            fqbn = inferred; source = FqbnSource.Inferred;
        }
        else if (board.Fqbn.Equals(inferred, StringComparison.OrdinalIgnoreCase))
        {
            fqbn = board.Fqbn; source = FqbnSource.ExactMatch;
        }
        else
        {
            fqbn = board.Fqbn; source = FqbnSource.PortPreferredOverInferred;   // physical board wins
            mismatch = !VendorOf(board.Fqbn).Equals(VendorOf(inferred), StringComparison.OrdinalIgnoreCase);
            if (mismatch)
                warn = $"The connected board is a {VendorOf(board.Fqbn)} but the firmware was written for {VendorOf(inferred)}. " +
                       "Flashing the wrong family can BRICK a board — only proceed if you're sure.";
        }

        var confirm = $"Flash firmware to:\n  {board.Label}\n  port {board.Port}\n  board {fqbn}\n\nThis writes to the device now and cannot be undone.";
        return new FlashPlan(board.Port, fqbn, source, mismatch, confirm, warn);
    }

    /// <summary>
    /// One-click flash: compile + <c>arduino-cli upload</c> to a target board. <paramref name="target"/> must
    /// be supplied when more than one board is connected (no silent first-port auto-flash). A cross-family
    /// FQBN/port mismatch is refused unless <paramref name="forceMismatch"/> is set (after a user confirm).
    /// Returns a structured result mirroring <see cref="BuildResult"/>.
    /// </summary>
    public static async Task<UploadResult> UploadAsync(Project.Project project, DetectedBoard? target,
        bool forceMismatch = false, CancellationToken ct = default)
    {
        var cli = Locate();
        if (cli is null) return UploadResult.NotInstalled();

        if (project.Firmware.Platform.Contains("python", StringComparison.OrdinalIgnoreCase))
            return new UploadResult(true, false, "MicroPython firmware — copy the .py to your board instead of flashing.", "");

        var board = target;
        if (board is null)
        {
            var detected = await DetectPortsAsync(project, ct);
            if (detected.Count == 0) return UploadResult.NoBoard();
            if (detected.Count > 1)   // never silently flash the first of several boards
                return new UploadResult(true, false, "Multiple boards detected — choose which port to flash.",
                    string.Join(", ", detected.Select(d => $"{d.Port} ({d.Label})")));
            board = detected[0];
        }

        var plan = BuildFlashPlan(project, board);
        if (!IsValidPort(plan.Port) || !IsValidFqbn(plan.Fqbn))
            return new UploadResult(true, false, "Refusing to flash — invalid port or board identifier.",
                $"{plan.Port} / {plan.Fqbn}");
        if (plan.VendorMismatch && !forceMismatch)
            return new UploadResult(true, false, "Refusing to flash — connected board family doesn't match the firmware.",
                plan.MismatchWarning ?? "");

        var fqbn = plan.Fqbn;
        var buildDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_flash_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var image = await CompileToImageAsync(project, buildDir, ct: ct, fqbnOverride: fqbn);
            if (!image.Ok)
            {
                var firstErr = image.Diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message ?? "compile failed";
                return new UploadResult(true, false, $"Won't flash — firmware didn't compile for {fqbn}.", firstErr);
            }
            if (!image.Fqbn.Equals(fqbn, StringComparison.OrdinalIgnoreCase))
                return new UploadResult(true, false, "Won't flash — compiled image does not match the selected board.",
                    $"compiled {image.Fqbn}, selected {fqbn}");

            // CompileToImageAsync above already installed (and verified) the core for this exact FQBN, so a
            // failure here would be a fresh environment change — surface it rather than flashing blind.
            if (await EnsureCoreAsync(cli, fqbn, ct) is { } coreError)
                return new UploadResult(true, false, "Won't flash — the board core isn't available.", coreError);
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

    /// <summary>
    /// Board-manager package indexes for the third-party cores Foundry's OWN <see cref="Fqbn"/> inference
    /// targets. Without these, <c>core install esp32:esp32</c> can never resolve — the platform simply is not
    /// in arduino-cli's built-in index — so the README's flagship ESP32 device could not compile or flash.
    /// <para>
    /// These are BUILD-TIME constants and never AI- or user-supplied. That distinction is the whole point:
    /// <see cref="Fqbn"/> deliberately refuses a model-supplied board hint containing flags
    /// (see <see cref="IsValidFqbn"/>) precisely so an attacker-chosen package index can't reach this
    /// command line. Adding a URL here is a deliberate trust decision; forwarding one is not.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> BoardIndexUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esp32"]   = "https://espressif.github.io/arduino-esp32/package_esp32_index.json",
        ["esp8266"] = "https://arduino.esp8266.com/stable/package_esp8266com_index.json",
        ["rp2040"]  = "https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json",
    };

    /// <summary>The <c>--additional-urls</c> argument for an FQBN's vendor, or "" for the built-in index.</summary>
    internal static string AdditionalUrlsArg(string fqbn)
    {
        var vendor = fqbn.Split(':').FirstOrDefault() ?? "";
        return BoardIndexUrls.TryGetValue(vendor, out var url) ? $" --additional-urls {url}" : "";
    }

    /// <summary>
    /// Ensure the platform core for an FQBN is installed (e.g. esp32:esp32). Returns null on success, or a
    /// user-facing reason on failure. Previously this swallowed every error, so a core that could not be
    /// installed surfaced only as an opaque compiler error much later.
    /// </summary>
    private static async Task<string?> EnsureCoreAsync(string cli, string fqbn, CancellationToken ct)
    {
        var parts = fqbn.Split(':');
        if (parts.Length < 2) return null;
        var platform = $"{parts[0]}:{parts[1]}";   // vendor:arch
        var extra = AdditionalUrlsArg(fqbn);
        try
        {
            var list = await RunAsync(cli, "core list" + extra, ct);
            if (list.stdout.Contains(platform, StringComparison.OrdinalIgnoreCase)) return null;

            Diagnostics.AppLog.Info("build", $"installing board core {platform} (one-time)…");
            await RunAsync(cli, "core update-index" + extra, ct);
            var install = await RunAsync(cli, $"core install {platform}{extra}", ct);
            if (install.code != 0)
            {
                var detail = Detail(install.stdout, install.stderr);
                Diagnostics.AppLog.Error("build", $"board core {platform} install failed · {detail}");
                return $"Couldn't install the {platform} board core — {detail}";
            }
            Diagnostics.AppLog.Info("build", $"board core {platform} installed");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("build", $"board core {platform} install error: {ex.Message}");
            return $"Couldn't install the {platform} board core — {ex.Message}";
        }
    }

    // A library name/version reaches a command line, so it is validated as strictly as the FQBN is.
    private static readonly System.Text.RegularExpressions.Regex SafeLibName =
        new(@"^[A-Za-z0-9 _.\-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SafeLibVersion =
        new(@"^[A-Za-z0-9.\-]{1,32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Libraries that ship WITH a core and are not in the library index — installing them fails.</summary>
    private static readonly HashSet<string> CoreBundledLibs = new(StringComparer.OrdinalIgnoreCase)
    {
        "arduino core", "wire", "wire (i2c)", "spi", "eeprom", "serial", "softwareserial",
        "wifi", "wificlientsecure", "webserver", "httpclient", "preferences", "fs", "spiffs",
        "littlefs", "esp8266wifi", "esp8266httpclient", "esp8266webserver", "sd", "math",
    };

    /// <summary>
    /// The <c>lib install</c> argument for one declared library, or null when it must be SKIPPED: a
    /// core-bundled/"built-in" entry (not in the library index — installing it just fails) or a name that
    /// fails validation. A pinned version is only appended when it too validates, so a malformed version
    /// degrades to "latest" instead of reaching the shell. Pure + unit-testable.
    /// </summary>
    internal static string? LibraryInstallSpec(string? name, string? version)
    {
        var n = (name ?? "").Trim();
        var v = (version ?? "").Trim();
        if (n.Length == 0) return null;
        if (CoreBundledLibs.Contains(n) || v.Equals("built-in", StringComparison.OrdinalIgnoreCase)) return null;
        if (!SafeLibName.IsMatch(n)) return null;
        return v.Length > 0 && SafeLibVersion.IsMatch(v) ? $"{n}@{v}" : n;
    }

    /// <summary>
    /// Install the libraries the generated sketch declares (<see cref="Project.Firmware.Libraries"/>) before
    /// compiling. Nothing did this before, so any AI sketch doing what its prompt instructs — real Wi-Fi/MQTT/
    /// sensor code — failed on its first #include. Core-bundled and "built-in" entries are skipped (they are
    /// not in the library index); a name that fails validation is skipped rather than passed to the shell.
    /// Returns the names that could not be installed, so the caller can report them honestly.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> EnsureLibrariesAsync(
        string cli, Project.Project project, CancellationToken ct)
    {
        var failed = new List<string>();
        foreach (var lib in project.Firmware.Libraries)
        {
            var name = (lib.Key ?? "").Trim();
            if (LibraryInstallSpec(name, lib.Value) is not { } spec) continue;
            try
            {
                var r = await RunAsync(cli, $"lib install \"{spec}\"", ct);
                if (r.code == 0) { Diagnostics.AppLog.Info("build", $"library installed · {spec}"); continue; }

                // A pinned version that does not exist is common in generated designs — retry unpinned
                // before giving up, so one bad version string doesn't fail an otherwise good sketch.
                if (spec != name)
                {
                    var retry = await RunAsync(cli, $"lib install \"{name}\"", ct);
                    if (retry.code == 0)
                    {
                        Diagnostics.AppLog.Warn("build", $"library {name}: pinned version unavailable ({spec}) — installed latest");
                        continue;
                    }
                }
                Diagnostics.AppLog.Warn("build", $"library install failed · {spec} · {Detail(r.stdout, r.stderr)}");
                failed.Add(name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Warn("build", $"library install error · {spec} · {ex.Message}");
                failed.Add(name);
            }
        }
        return failed;
    }

    private static string Detail(string stdout, string stderr) =>
        !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim().Split('\n')[0].Trim()
        : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim().Split('\n')[^1].Trim()
        : "no output";

    // Thin adapter over the shared ProcessRunner: concurrent stdout/stderr drain (no pipe-buffer deadlock),
    // a timeout, and process-tree kill on timeout/cancel. arduino-cli core/compile/upload → ArduinoTimeout.
    private static async Task<(string stdout, string stderr, int code)> RunAsync(string cli, string args, CancellationToken ct)
    {
        var r = await Diagnostics.ProcessRunner.RunAsync(cli, args, Diagnostics.ProcessRunner.ArduinoTimeout, ct);
        return (r.Stdout, r.Stderr, r.ExitCode);
    }

    /// <summary>Download arduino-cli to the app-local tools folder (PRD v2 G1 on-demand install). Returns the path.</summary>
    public static async Task<string> DownloadCliAsync(CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(LocalToolPath)!;
        System.IO.Directory.CreateDirectory(dir);
        var zip = System.IO.Path.Combine(dir, "arduino-cli.zip");
        // The release ZIP is pinned by SHA-256 — that IS the integrity anchor. Arduino does NOT
        // Authenticode-sign arduino-cli.exe (verified: Get-AuthenticodeSignature reports NotSigned), so the
        // signature gate that used to follow this could never pass and made the tool uninstallable.
        // Extraction is quarantined so a rejected payload never lands in the shared tools dir.
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, ArduinoCliUrl, zip, ArduinoCliSha256, ct);
        try
        {
            Provisioning.DownloadVerifier.ExtractVerifiedFile(zip, "arduino-cli.exe", LocalToolPath);
        }
        finally { try { System.IO.File.Delete(zip); } catch { } }

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
