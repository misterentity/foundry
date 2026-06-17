using System.Text.RegularExpressions;
using Foundry.Core.Kb;

namespace Foundry.Core.Pcb;

/// <summary>
/// Deterministic <see cref="ComponentSpec"/> → KiCad footprint lib id ("Lib:Footprint") mapping —
/// the EE analogue of <see cref="Foundry.Core.Firmware.FirmwareBuilder.Fqbn"/>: keyword-driven,
/// table-based, with a safe generic fallback so a board is always producible. Priority:
/// (1) explicit <see cref="ComponentSpec.Footprint"/>; (2) this keyword heuristic; (3) generic
/// pin-count-correct header. Pure and fully unit-testable — no KiCad required.
///
/// Also produces the per-component <c>padNets</c> (pad name → net name) the build script needs to
/// assign each pad to its electrical net, reusing the netlist endpoint model (alias.pin).
/// </summary>
public static class FootprintMap
{
    /// <summary>Imperial size token → metric body code (KLC R_/C_/LED_ naming, e.g. 0805 → 2012Metric).</summary>
    private static readonly Dictionary<string, string> SizeMetric = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0402"] = "1005", ["0603"] = "1608", ["0805"] = "2012", ["1206"] = "3216",
    };

    private static readonly Regex SizeToken = new(@"\b(0402|0603|0805|1206)\b", RegexOptions.Compiled);

    /// <summary>
    /// The chosen footprint lib id for a component plus a flag indicating it fell through to the generic
    /// header fallback (the caller emits a warning diagnostic when <see cref="IsFallback"/> is true).
    /// </summary>
    public sealed record FootprintChoice(string LibId, bool IsFallback, string Reason);

    /// <summary>
    /// Resolve a component to a footprint lib id. <paramref name="pinCount"/> sizes headers and the
    /// generic fallback (≥1). Explicit <see cref="ComponentSpec.Footprint"/> wins; then keyword
    /// heuristics; else a pin-count-correct pin header so every net node still lands on a pad.
    /// </summary>
    public static FootprintChoice Resolve(ComponentSpec spec, int pinCount)
    {
        if (!string.IsNullOrWhiteSpace(spec.Footprint))
            return new FootprintChoice(spec.Footprint!.Trim(), false, "explicit");

        var n = Math.Max(1, pinCount);
        var hay = (spec.Name + " " + spec.Ref).ToLowerInvariant();
        var sizeId = SizeMetric.TryGetValue(SizeToken.Match(hay).Value, out var m) ? m : null;

        bool Has(params string[] words) => words.Any(w => hay.Contains(w));
        bool Smd() => Has("smd", "sod", "sot", "0402", "0603", "0805", "1206");

        // ---- passives ----
        if (Has("resistor", "ohm") || Regex.IsMatch(hay, @"\bres\b"))
        {
            if (Has("through-hole", "thru", "axial") && !Smd())
                return Heur("Resistor_THT:R_Axial_DIN0207_L6.3mm_D2.5mm_P10.16mm_Horizontal");
            return Heur(SwapSize("Resistor_SMD:R_0805_2012Metric", "2012", sizeId));
        }
        if (Has("capacitor", "cap ", " cap", "uf", "nf", "pf") || hay.EndsWith("cap"))
        {
            if (Has("electrolytic"))
                return Heur("Capacitor_THT:CP_Radial_D5.0mm_P2.50mm");
            return Heur(SwapSize("Capacitor_SMD:C_0603_1608Metric", "1608", sizeId));
        }
        if (Has("led"))
        {
            if (Smd())
                return Heur(SwapSize("LED_SMD:LED_0805_2012Metric", "2012", sizeId));
            return Heur(Has("3mm", "3.0mm", "d3") ? "LED_THT:LED_D3.0mm" : "LED_THT:LED_D5.0mm");
        }
        if (Has("diode", "1n400", "rectifier"))
            return Heur(Smd() ? "Diode_SMD:D_SOD-123" : "Diode_THT:D_DO-41_SOD81_P10.16mm_Horizontal");

        // ---- active / packages (before generic connector check) ----
        if (Has("esp32", "wroom"))
            return Heur("RF_Module:ESP32-WROOM-32");
        if (Has("esp8266", "esp-12", "esp12", "nodemcu"))
            return Heur("RF_Module:ESP-12E");
        if (Has("pico", "rp2040"))
            return Heur("Module:RaspberryPi_Pico_Common_SMD");   // KiCad 10 id (was RPi_Pico:RPi_Pico_SMD_TH)
        // Arduino Uno R3 (ATmega328 dev board) — a real KiCad module footprint whose 1..32 pads match the
        // MCU_Module:Arduino_UNO_R3 symbol, so SymbolPinMap resolves D13/A0/3V3/5V/GND to authoritative pads.
        if (Has("arduino uno", "uno r3", "uno r2"))
            return Heur("Module:Arduino_UNO_R3");
        // Arduino Nano (ATmega328) — checked AFTER esp32/rp2040 so "Nano ESP32"/"Nano RP2040" map to their MCU.
        // Pads via the MCU_Module:Arduino_Nano_v3.x symbol (which extends v2.x — SymbolPinMap follows extends).
        if (Has("arduino nano", "nano v3", "nano v2"))
            return Heur("Module:Arduino_Nano");

        if (Has("regulator", "ldo", "7805"))
        {
            if (Has("sot-223", "sot223", "sot-23", "sot23"))
                return Heur("Package_TO_SOT_SMD:SOT-223-3_TabPin2");
            return Heur("Package_TO_SOT_THT:TO-220-3_Vertical");
        }
        if (Has("transistor", "mosfet", "bjt"))
        {
            if (Has("to-92", "to92") || (Has("thru", "through-hole") && !Smd()))
                return Heur("Package_TO_SOT_THT:TO-92_Inline");
            return Heur("Package_TO_SOT_SMD:SOT-23");
        }

        // ---- SolarPool exotic parts: modules/sensors mounted via connectors. Map to REAL, manufacturable
        //      KiCad-10 footprints (all verified to exist + measured) so they stop hitting the generic
        //      fallback and pad counts are explicit. Checked before the generic connector/header rules. ----

        // DFRobot Gravity analog sensors (pH / ORP) — 3-wire (signal/VCC/GND) → 1x03 JST-PH connector.
        if (Has("gravity", "ph sensor", "ph probe", "orp", "analog sensor"))
            return Heur("Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical");

        // DS18B20 waterproof temperature probe — 3-wire → TO-92 inline (matches the bare sensor pinout).
        if (Has("ds18b20", "1-wire", "1wire", "temperature probe", "temp probe"))
            return Heur("Package_TO_SOT_THT:TO-92_Inline");

        // 6V solar panel — 2-wire lead → 2-pin JST-PH connector.
        if (Has("solar", "panel", "photovoltaic"))
            return Heur("Connector_JST:JST_PH_B2B-PH-K_1x02_P2.00mm_Vertical");

        // 18650 Li-ion cell → single-cell battery holder.
        if (Has("18650", "li-ion cell", "lithium cell", "battery holder"))
            return Heur("Battery:BatteryHolder_Keystone_1042_1x18650");

        // MT3608 boost / CN3791 MPPT charger modules — soldered onto via header strips. Keep a header but
        // size it to the module's pin count (no longer the generic fallback diagnostic; pad count explicit).
        if (Has("mt3608", "boost converter module", "boost module", "step-up module"))
            return Heur(Header(Math.Max(n, 4)));
        if (Has("cn3791", "mppt", "charge controller module", "charger module", "solar charger"))
            return Heur(Header(Math.Max(n, 6)));

        if (Has("qfp", "lqfp", "tqfp"))
            return Heur($"Package_QFP:LQFP-{n}_7x7mm_P0.5mm");
        if (Has("soic", "so-8", "so8"))
            return Heur($"Package_SO:SOIC-{n}_3.9x4.9mm_P1.27mm");
        if (Has("dip", "pdip"))
            return Heur($"Package_DIP:DIP-{n}_W7.62mm");

        // ---- connectors / headers ----
        if (Has("terminal block", "screw terminal"))
            return Heur("TerminalBlock_Phoenix:TerminalBlock_Phoenix_MKDS-1,5-2_1x02_P5.00mm_Horizontal");
        if (Has("header", "connector", "pins"))
            return Heur(Header(n));

        // ---- generic fallback: pin-count-correct header so every node resolves to a pad ----
        return new FootprintChoice(Header(n), true,
            $"no footprint match for '{spec.Name}' — using {n}-pin header so the board stays producible");

        static FootprintChoice Heur(string id) => new(id, false, "keyword");
    }

    private static string Header(int n) =>
        $"Connector_PinHeader_2.54mm:PinHeader_1x{n:00}_P2.54mm_Vertical";

    private static readonly Regex CountToken = new(@"(\d{1,3})", RegexOptions.Compiled);

    /// <summary>
    /// Approximate courtyard size (W×H mm) for a footprint lib id — the placer uses this to keep parts
    /// from overlapping WITHOUT KiCad. Coarse, table-driven match on the lib id (covering the ids
    /// <see cref="Resolve"/> actually produces), with count-scaled sizes for SOIC/DIP/QFP/PinHeader and a
    /// generous 10×10 default for anything unmatched (never 0). Pure and string-assert testable.
    /// </summary>
    public static (double WMm, double HMm) CourtyardOf(string libId)
    {
        var id = libId ?? "";
        bool Has(string token) => id.Contains(token, StringComparison.OrdinalIgnoreCase);

        // ---- modules / dev boards (check before generic package matches) ----
        if (Has("ESP32-WROOM")) return (18.0, 25.5);
        if (Has("ESP-12")) return (16.0, 24.0);
        if (Has("RPi_Pico") || Has("Pico")) return (21.0, 51.0);

        // ---- imperial-size passive bodies (R_/C_/LED_) ----
        if (Has("_0402")) return (1.0, 0.5);
        if (Has("_0603")) return (1.6, 0.8);
        if (Has("_0805")) return (2.0, 1.25);
        if (Has("_1206")) return (3.2, 1.6);

        // ---- THT passives / discrete LEDs ----
        if (Has("R_Axial") || Has("CP_Radial") || Has("D_DO-41") || Has("LED_D5.0mm") || Has("LED_D3.0mm"))
            return (7.0, 3.0);

        // ---- discrete packages ----
        if (Has("SOD-123")) return (2.7, 1.6);
        if (Has("SOT-223")) return (7.0, 7.0);
        if (Has("SOT-23")) return (3.0, 3.0);
        if (Has("TO-220")) return (10.0, 4.5);
        if (Has("TO-92")) return (5.0, 5.0);

        // ---- count-scaled ICs (parse the pin/row count out of the id) ----
        if (Has("SOIC-"))
        {
            int n = CountAfter(id, "SOIC-", 8);
            return ((n / 2) * 1.27 + 2, 6.0);
        }
        if (Has("DIP-"))
        {
            int n = CountAfter(id, "DIP-", 8);
            return ((n / 2) * 2.54 + 3, 9.0);
        }
        if (Has("LQFP-") || Has("TQFP-") || Has("QFP-"))
        {
            int n = CountAfter(id, "QFP-", 32);
            double side = Math.Sqrt(n) * 1.6 + 8;
            return (side, side);
        }

        // ---- pin headers (generic fallback + explicit headers) ----
        if (Has("PinHeader_2x"))
        {
            int n = CountAfter(id, "2x", 2);
            return (n * 2.54, 5.08);
        }
        if (Has("PinHeader_1x"))
        {
            int n = CountAfter(id, "1x", 2);
            return (n * 2.54, 2.54);
        }

        // ---- connectors / battery holders used for SolarPool exotic parts (offline approximations) ----
        if (Has("BatteryHolder") && Has("18650")) return (88.0, 21.75);
        if (Has("BatteryHolder")) return (40.0, 20.0);
        if (Has("JST_PH_B3B") || Has("1x03") && Has("JST")) return (9.0, 5.6);
        if (Has("JST_PH_B2B") || Has("1x02") && Has("JST")) return (7.0, 5.6);
        if (Has("JST")) return (8.5, 6.0);

        if (Has("TerminalBlock")) return (10.0, 8.0);

        // ---- generous safe default (never 0) ----
        return (10.0, 10.0);
    }

    /// <summary>Parse the first integer appearing after <paramref name="marker"/> in the lib id.</summary>
    private static int CountAfter(string id, string marker, int fallback)
    {
        var i = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return fallback;
        var m = CountToken.Match(id, i + marker.Length);
        return m.Success && int.TryParse(m.Value, out var n) && n > 0 ? n : fallback;
    }

    /// <summary>Swap the metric body token in an R_/C_/LED_ id to match an imperial size hint (no-op if none).</summary>
    private static string SwapSize(string defaultId, string defaultToken, string? metric) =>
        metric is null || metric == defaultToken ? defaultId : defaultId.Replace(defaultToken, metric);

    /// <summary>
    /// Pad name → net name for one component. Foundry endpoints are <c>alias.pin</c>; KiCad pads are
    /// addressed by <c>pad.GetName()</c> (numeric like "1", or named like "VCC"). v2.2 maps by pin
    /// name as-is (numeric pins = pad number identity; named pins = direct silkscreen match). The
    /// actual reconciliation against real footprint pads happens in build_board.py at load time.
    /// </summary>
    /// <param name="alias">Component alias / reference (e.g. "U1").</param>
    /// <param name="endpointNets">All (endpoint, netName) pairs from the netlist for this alias's nodes.</param>
    public static IReadOnlyDictionary<string, string> PadNets(string alias, IEnumerable<(string Endpoint, string Net)> endpointNets)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ep, net) in endpointNets)
        {
            if (!RefOf(ep).Equals(alias, StringComparison.OrdinalIgnoreCase)) continue;
            var pad = PinOf(ep);
            if (pad.Length == 0) continue;
            map[pad] = net;   // last write wins; a pad belongs to exactly one net
        }
        return map;
    }

    /// <summary>
    /// Ordered (pin, net) assignments for one component, in netlist order (deduped by pin, first-seen kept).
    /// build_board.py matches these to the real footprint's pads by name first, then falls back to ordinal
    /// pad position — so generic fallback headers (pads "1".."N") still get every net even though the
    /// netlist addresses pins by name (e.g. VCC/AOUT/GND).
    /// </summary>
    public static IReadOnlyList<PcbPadNet> PadNetList(string alias, IEnumerable<(string Endpoint, string Net)> endpointNets)
    {
        var list = new List<PcbPadNet>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ep, net) in endpointNets)
        {
            if (!RefOf(ep).Equals(alias, StringComparison.OrdinalIgnoreCase)) continue;
            var pad = PinOf(ep);
            if (pad.Length == 0 || !seen.Add(pad)) continue;
            list.Add(new PcbPadNet(pad, net));
        }
        return list;
    }

    internal static string RefOf(string endpoint)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0 ? endpoint.Trim() : endpoint[..dot].Trim();
    }

    internal static string PinOf(string endpoint)
    {
        var dot = endpoint.IndexOf('.');
        return dot < 0 ? "1" : endpoint[(dot + 1)..].Trim();
    }
}
