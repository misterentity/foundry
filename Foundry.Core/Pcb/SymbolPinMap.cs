using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Foundry.Core.Pcb;

/// <summary>
/// Derives authoritative logical-pin → footprint-pad maps from KiCad's SYMBOL libraries at build time — the
/// scalable, transcription-free complement to the curated <see cref="McuPinMap"/>. A KiCad symbol lists every
/// pin's NAME and NUMBER, and the footprint's pads ARE those numbers, so the symbol is the ground truth for
/// "GPIO34 lives on pad 6". Given a resolved footprint we find its symbol (via a small footprint→symbol name
/// pointer, since the two often differ — e.g. RPi_Pico:RPi_Pico_SMD_TH ↔ MCU_Module:RaspberryPi_Pico — else an
/// identity guess), parse the symbol's pins, canonicalize the names (IO34→GPIO34, GPIO26_ADC0→GPIO26, VDD→3V3,
/// alternates split on '/'), and index them to pad numbers.
///
/// Only resolves when KiCad is installed (Track B already requires it); otherwise it no-ops and the curated
/// map / fail-closed gate take over. Anything unresolved still fails closed — never mis-wired. Determinism
/// boundary intact: pure file parsing, no AI, no guessed numbers.
/// </summary>
public static class SymbolPinMap
{
    // footprint libId -> (symbol lib, symbol name). Just a NAME pointer; the pad numbers come from the symbol.
    private static readonly Dictionary<string, (string Lib, string Name)> FootprintToSymbol =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Module:RaspberryPi_Pico_Common_SMD"] = ("MCU_Module", "RaspberryPi_Pico"),
            ["RF_Module:ESP32-WROOM-32"] = ("RF_Module", "ESP32-WROOM-32"),
        };

    // "<symbolDir>|<lib>|<name>" -> (canonical pin -> pad). Parsed once per symbol per session.
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>?> Cache = new();

    /// <summary>The footprint pad for a logical pin, derived from the part's KiCad symbol, or null when the
    /// symbol/pin can't be resolved (caller then falls through to the fail-closed gate).</summary>
    public static string? ResolvePad(string footprintLibId, string logicalPin, string? symbolDir)
    {
        if (string.IsNullOrEmpty(symbolDir) || string.IsNullOrEmpty(footprintLibId) || string.IsNullOrEmpty(logicalPin))
            return null;
        var map = MapFor(footprintLibId, symbolDir);
        return map is not null && map.TryGetValue(Canonical(logicalPin), out var pad) ? pad : null;
    }

    private static IReadOnlyDictionary<string, string>? MapFor(string footprintLibId, string symbolDir)
    {
        (string Lib, string Name) sym;
        if (FootprintToSymbol.TryGetValue(footprintLibId, out var s)) sym = s;
        else if (footprintLibId.Contains(':')) { var parts = footprintLibId.Split(':', 2); sym = (parts[0], parts[1]); }
        else return null;
        return Cache.GetOrAdd($"{symbolDir}|{sym.Lib}|{sym.Name}", _ => Parse(symbolDir, sym.Lib, sym.Name));
    }

    private static IReadOnlyDictionary<string, string>? Parse(string symbolDir, string lib, string name)
    {
        try
        {
            var path = System.IO.Path.Combine(symbolDir, lib + ".kicad_sym");
            if (!System.IO.File.Exists(path)) return null;
            var s = System.IO.File.ReadAllText(path);
            // Top-level symbols are indented one tab; the block includes the nested (deeper-indented) unit
            // sub-symbols that hold the pins. Bound it at the next ONE-tab (symbol so we don't run past it.
            var marker = "\t(symbol \"" + name + "\"";
            var start = s.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            var nxt = s.IndexOf("\n\t(symbol \"", start + marker.Length, StringComparison.Ordinal);
            var block = s[start..(nxt > 0 ? nxt : s.Length)];

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in block.Split("(pin ").Skip(1))
            {
                var nm = Regex.Match(chunk, "\\(name \"([^\"]+)\"");
                var nu = Regex.Match(chunk, "\\(number \"([^\"]+)\"");
                if (!nm.Success || !nu.Success) continue;
                var pad = nu.Groups[1].Value;
                foreach (var alt in nm.Groups[1].Value.Split('/'))   // "RXD0/IO3" → index both alternates
                {
                    var key = Canonical(alt);
                    if (key.Length > 0 && !map.ContainsKey(key)) map[key] = pad;   // first-seen pad wins
                }
            }
            return map.Count > 0 ? map : null;
        }
        catch { return null; }
    }

    /// <summary>Universal pin-name canonicalization shared by indexing + lookup (NOT chip-specific — those
    /// aliases live in the curated <see cref="McuPinMap"/>): IOnn→GPIOnn, GPIOnn_suffix→GPIOnn, VDD/VCC→3V3,
    /// VSS→GND.</summary>
    internal static string Canonical(string raw)
    {
        var p = (raw ?? "").Trim();
        if (p.Length == 0) return "";
        var io = Regex.Match(p, @"^IO(\d+)$", RegexOptions.IgnoreCase);
        if (io.Success) return "GPIO" + io.Groups[1].Value;
        var gp = Regex.Match(p, @"^(GPIO\d+)(?:[_/].*)?$", RegexOptions.IgnoreCase);   // GPIO26_ADC0 → GPIO26
        if (gp.Success) return gp.Groups[1].Value.ToUpperInvariant();
        var gpn = Regex.Match(p, @"^GP(\d+)$", RegexOptions.IgnoreCase);   // Pico silkscreen GP0 → GPIO0
        if (gpn.Success) return "GPIO" + gpn.Groups[1].Value;
        return p.ToUpperInvariant() switch
        {
            "VDD" or "VCC" or "3.3V" or "+3V3" => "3V3",
            "VSS" or "GROUND" => "GND",
            _ => p.ToUpperInvariant(),
        };
    }
}
