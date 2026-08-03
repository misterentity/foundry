using System.Text.Json;
using Foundry.Core.Ai;
using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Core.Generation;

// Firmware enrichment + compile-fix loop (split from ProjectGenerator.cs).
public sealed partial class ProjectGenerator
{
    /// <summary>Ask the model for complete, working firmware for this exact device; inject the derived pin map.</summary>
    private async Task EnrichFirmwareAsync(Project.Project project, string prompt, CancellationToken ct)
    {
        try
        {
            var kb = new ComponentKb(project.Components);
            var entries = PinMap.Build(project.Connections, kb);
            var platform = project.Firmware.Platform;
            var pinmapName = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "pinmap.py" : "pinmap.h";
            var pinmap = platform.Contains("python", StringComparison.OrdinalIgnoreCase)
                ? string.Join("\n", entries.Select(e => $"{e.Macro} = {e.PyEmit}  # {e.Net}: {e.FromPin} <-> {e.ToPin}"))
                : PinMap.RenderHeader(entries);

            var parts = string.Join("\n", project.Components.Select(c =>
                $"- {c.Alias} ({c.Name}): pins {string.Join(", ", c.Pins.Select(p => p.Name))}"));
            var nets = string.Join("\n", project.Connections.Select(c => $"- {c.From} -> {c.To} [{c.Net}]"));

            var macroList = string.Join(", ", entries.Select(e => e.Macro));
            var inc = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "from pinmap import *" : "#include \"pinmap.h\"";
            var user =
                $"Device: {prompt}\n\nPlatform: {platform}\nParts:\n{parts}\n\nNetlist:\n{nets}\n\n" +
                $"Pin map ({pinmapName}) is PRE-DEFINED and supplied — DO NOT redefine pins. In your main file you MUST " +
                $"`{inc}` and use ONLY these exact macro names (do not invent, rename, or alias them):\n{pinmap}\n\n" +
                $"Available pin macros (use verbatim): {macroList}";

            var raw = await _ai.CompleteAsync(FirmwareSystemPrompt, user, _model, ct);
            var json = ExtractJson(raw);
            if (json is null)
            {
                // Don't silently keep the stub: a null result here means unparseable JSON. If it was a token-cap
                // truncation, AnthropicClient already logged the authoritative "TRUNCATED" WARN; make the fallback visible either way.
                Diagnostics.AppLog.Warn("generation", $"firmware pass returned no usable JSON ({raw.Length} chars) — keeping the deterministic firmware.");
                return;
            }

            var fw = MapFirmware(json, project.Firmware.Platform);
            if (fw.Files.Count == 0)
            {
                Diagnostics.AppLog.Warn("generation", "firmware pass returned no files — using deterministic fallback");
                return; // keep the deterministic fallback
            }
            Diagnostics.AppLog.Info("generation", $"firmware pass · {fw.Files.Count} files · {fw.Platform}");

            // guarantee the netlist-derived pin map is present + authoritative
            fw.Files.RemoveAll(f => f.Name.Equals(pinmapName, StringComparison.OrdinalIgnoreCase));
            fw.Files.Add(new FirmwareFile { Name = pinmapName, Path = "/foundry/firmware/", Content = pinmap });
            foreach (var f in fw.Files) f.Active = false;
            if (PickMainFile(fw.Files) is { } mainFile) mainFile.Active = true;
            project.Firmware = fw;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("generation", $"firmware pass failed: {ex.Message} — using deterministic fallback");
        }
    }

    /// <summary>Ask the AI to fix firmware that failed to compile, given the compiler errors (PRD v2 G3).</summary>
    public async Task<bool> FixFirmwareAsync(Project.Project project, string compilerErrors, CancellationToken ct = default)
    {
        if (!_ai.HasKey || string.IsNullOrWhiteSpace(compilerErrors)) return false;
        try
        {
            var kb = new ComponentKb(project.Components);
            var entries = PinMap.Build(project.Connections, kb);
            var platform = project.Firmware.Platform;
            var pinmapName = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "pinmap.py" : "pinmap.h";
            var pinmap = platform.Contains("python", StringComparison.OrdinalIgnoreCase)
                ? string.Join("\n", entries.Select(e => $"{e.Macro} = {e.PyEmit}"))
                : PinMap.RenderHeader(entries);
            var inc = platform.Contains("python", StringComparison.OrdinalIgnoreCase) ? "from pinmap import *" : "#include \"pinmap.h\"";
            var current = string.Join("\n\n", project.Firmware.Files
                .Where(f => !f.Name.Equals(pinmapName, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"// ===== FILE: {f.Name} =====\n{f.Content}"));

            var user =
                $"This {platform} firmware fails to compile. Fix the errors and return the FULL corrected firmware as " +
                $"the usual JSON. Keep the same files; change only what's needed.\n\nCOMPILER ERRORS:\n{compilerErrors}\n\n" +
                $"Pin map ({pinmapName}) is PRE-DEFINED — `{inc}` and use these macros verbatim:\n{pinmap}\n\n" +
                $"CURRENT FIRMWARE:\n{current}";

            var raw = await _ai.CompleteAsync(FirmwareSystemPrompt, user, _model, ct);
            var json = ExtractJson(raw);
            if (json is null) return false;
            var fw = MapFirmware(json, platform);
            if (fw.Files.Count == 0) return false;

            fw.Files.RemoveAll(f => f.Name.Equals(pinmapName, StringComparison.OrdinalIgnoreCase));
            fw.Files.Add(new FirmwareFile { Name = pinmapName, Path = "/foundry/firmware/", Content = pinmap });
            foreach (var f in fw.Files) f.Active = false;
            if (PickMainFile(fw.Files) is { } mainFile) mainFile.Active = true;
            project.Firmware = fw;
            Diagnostics.AppLog.Info("build", $"AI build-fix applied · {fw.Files.Count} files");
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("build", $"build-fix failed: {ex.Message}");
            return false;
        }
    }

    private const string FirmwareSystemPrompt = """
You are a senior embedded-firmware engineer. Write COMPLETE, working, COMPILABLE firmware for the exact
device described — real application logic, not a skeleton: initialize every peripheral, implement the
protocols it needs (Wi-Fi/MQTT/HTTP/BLE/I2C/SPI/ADC as appropriate), the main control loop, timing,
debouncing/error handling, and sensible defaults. Code must compile cleanly against the standard core for
the board (no invented APIs, no pseudo-code, no TODO stubs in the control path).

Pin + structure rules:
- The primary sketch MUST be named exactly "main.ino" (Arduino C++) or "main.py" (MicroPython) — nothing else.
- The main file MUST include the supplied pin map (`#include "pinmap.h"` for Arduino, `from pinmap import *`
  for MicroPython) and use its PIN_* macros verbatim for every pin — never hard-code, rename, redefine, or
  invent pin macros not in the supplied map. Do NOT emit the pin-map file yourself; it is supplied.
- Put secrets (Wi-Fi creds, tokens, broker host) as clearly-marked #define/constant placeholders in a SEPARATE
  config file (config.h / config.py), and reference them from main — never inline real credentials.
- Use only widely-available, named libraries; list each in "libraries" with the version you target.

OUTPUT CONTRACT — return ONE strictly-valid JSON object and NOTHING else (no prose, no markdown fences):
{
 "platform": "Arduino C++" | "MicroPython",
 "board": "esp32:esp32:esp32",
 "files": [{"name":"main.ino","content":"<full source>"}, {"name":"config.h","content":"..."}],
 "libraries": [["WiFi","built-in"], ["PubSubClient","2.8"]]
}
Source goes in the "content" string: escape it as valid JSON (\" for quotes, \\ for backslashes, \n for
newlines) — never use raw newlines or unescaped quotes inside a JSON string. No trailing commas, no comments
in the JSON itself (code comments inside "content" are fine). Keep the whole object compact enough to finish;
do not stop mid-file. Include the main sketch plus any helper/config files; omit the supplied pin-map file.
""";

    /// <summary>
    /// The sketch to show/flash first: a main.* file if present, else the largest source file.
    ///
    /// <para>
    /// Returns null for an empty file set. This used to end <c>?? files[0]</c>, which threw
    /// ArgumentOutOfRangeException on an empty list — and CompileAsync's catch-all turned that into
    /// "Couldn't run the compiler: Index was out of range. Must be non-negative and less than the size of
    /// the collection. (Parameter 'index')". It is the single most frequent error in the app log, and it
    /// fires whenever a build starts against firmware that has not been generated yet.
    /// </para>
    /// </summary>
    public static FirmwareFile? PickMainFile(IReadOnlyList<FirmwareFile> files)
    {
        if (files.Count == 0) return null;
        var main = files.FirstOrDefault(f => f.Name.StartsWith("main", StringComparison.OrdinalIgnoreCase));
        if (main is not null) return main;
        bool IsSource(string n) => n.EndsWith(".ino") || n.EndsWith(".py") || n.EndsWith(".cpp") || n.EndsWith(".c");
        return files.Where(f => IsSource(f.Name.ToLowerInvariant())).OrderByDescending(f => f.Content.Length).FirstOrDefault()
               ?? files[0];
    }

    private static Project.Firmware MapFirmware(string json, string fallbackPlatform)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new Project.Firmware
        {
            Platform = Str(root, "platform", fallbackPlatform),
            Board = Str(root, "board", ""),
            Files = Arr(root, "files")
                .Where(f => f.TryGetProperty("name", out _) )
                .Select(f => new FirmwareFile
                {
                    Name = Str(f, "name", "file.txt"),
                    Path = "/foundry/firmware/",
                    Content = Str(f, "content", ""),
                })
                .Where(f => f.Content.Length > 0)
                .ToList(),
            Libraries = Arr(root, "libraries")
                .Where(l => l.ValueKind == JsonValueKind.Array && l.GetArrayLength() >= 2)
                .Select(l => new SpecPair(l[0].GetString() ?? "", l[1].GetString() ?? "")).ToList(),
        };
    }
}
