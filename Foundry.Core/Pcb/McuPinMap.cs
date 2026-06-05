using System.Text.RegularExpressions;

namespace Foundry.Core.Pcb;

/// <summary>
/// Authoritative logical-pin-name → footprint-pad-name maps for resolved MCU/module footprints whose pads
/// are NUMERIC ("1".."N") but whose pins are addressed in the netlist by LOGICAL name (e.g. an ESP32's
/// GPIO34 physically lives on pad 6). Without a map the P0-1 fail-closed gate correctly REFUSES such a board
/// (a logical name matches no pad); with one, the net resolves to the real pad and the board builds verified.
///
/// Maps are DERIVED from the authoritative KiCad symbol library (symbol pin name→number) plus a documented
/// alias normalization — never hand-guessed pad numbers. Coverage is incremental and SAFE: any footprint or
/// pin not covered still falls through to the fail-closed gate (refused), never silently mis-wired.
///
/// Determinism boundary: a static deterministic lookup; the AI never supplies pad numbers.
/// </summary>
public static class McuPinMap
{
    // libId -> (canonical logical pin name -> pad name). Pin-name lookups are case-insensitive.
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Maps =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["RF_Module:ESP32-WROOM-32"] = Esp32Wroom32(),
        };

    /// <summary>True when Foundry has an authoritative pin map for this footprint.</summary>
    public static bool Has(string libId) => libId is not null && Maps.ContainsKey(libId);

    /// <summary>The footprint pad name for a logical pin, or null when unknown (caller then fails closed).</summary>
    public static string? ResolvePad(string libId, string logicalPin)
    {
        if (libId is null || logicalPin is null) return null;
        if (!Maps.TryGetValue(libId, out var m)) return null;
        return m.TryGetValue(Normalize(logicalPin), out var pad) ? pad : null;
    }

    /// <summary>Normalize a Foundry-emitted pin name to the map's canonical key: IOnn→GPIOnn (KiCad-symbol
    /// style → Foundry/datasheet style), supply/UART aliases folded to their canonical net names.</summary>
    internal static string Normalize(string pin)
    {
        var p = (pin ?? "").Trim();
        var io = Regex.Match(p, @"^IO(\d+)$", RegexOptions.IgnoreCase);
        if (io.Success) return "GPIO" + io.Groups[1].Value;
        return p.ToUpperInvariant() switch
        {
            "VDD" or "VCC" or "3.3V" or "+3V3" or "3V3" => "3V3",
            "GND" or "VSS" or "GROUND" => "GND",
            "RST" or "RESET" or "EN" or "CHIP_PU" => "EN",
            "VP" or "SENSOR_VP" => "GPIO36",
            "VN" or "SENSOR_VN" => "GPIO39",
            "RX" or "RXD" or "RXD0" or "U0RXD" => "GPIO3",
            "TX" or "TXD" or "TXD0" or "U0TXD" => "GPIO1",
            _ => p.ToUpperInvariant(),
        };
    }

    /// <summary>
    /// ESP32-WROOM-32 (38-pin module + thermal pad). Pad numbers are from KiCad's RF_Module symbol (the
    /// authoritative pin name→number table); the GPIO equivalences for the module's functional pin names
    /// (SENSOR_VP=GPIO36, SD2/3=GPIO9/10, CMD=GPIO11, CLK=GPIO6, SD0/1=GPIO7/8, RXD0/TXD0=GPIO3/1) are the
    /// standard Espressif ESP32 datasheet mappings. GND uses pad 1 (also on 15/38/39 — one tie is sufficient).
    /// </summary>
    private static IReadOnlyDictionary<string, string> Esp32Wroom32() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GND"] = "1",
            ["3V3"] = "2",
            ["EN"] = "3",
            ["GPIO36"] = "4",   // SENSOR_VP (input-only ADC1_CH0)
            ["GPIO39"] = "5",   // SENSOR_VN (input-only ADC1_CH3)
            ["GPIO34"] = "6",
            ["GPIO35"] = "7",
            ["GPIO32"] = "8",
            ["GPIO33"] = "9",
            ["GPIO25"] = "10",
            ["GPIO26"] = "11",
            ["GPIO27"] = "12",
            ["GPIO14"] = "13",
            ["GPIO12"] = "14",
            ["GPIO13"] = "16",
            ["GPIO9"] = "17",   // SD2 (flash — avoid)
            ["GPIO10"] = "18",  // SD3 (flash — avoid)
            ["GPIO11"] = "19",  // CMD (flash — avoid)
            ["GPIO6"] = "20",   // CLK (flash — avoid)
            ["GPIO7"] = "21",   // SD0 (flash — avoid)
            ["GPIO8"] = "22",   // SD1 (flash — avoid)
            ["GPIO15"] = "23",
            ["GPIO2"] = "24",
            ["GPIO0"] = "25",
            ["GPIO4"] = "26",
            ["GPIO16"] = "27",
            ["GPIO17"] = "28",
            ["GPIO5"] = "29",
            ["GPIO18"] = "30",
            ["GPIO19"] = "31",
            ["GPIO21"] = "33",
            ["GPIO3"] = "34",   // RXD0
            ["GPIO1"] = "35",   // TXD0
            ["GPIO22"] = "36",
            ["GPIO23"] = "37",
        };
}
