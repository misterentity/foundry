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
            return Heur("RPi_Pico:RPi_Pico_SMD_TH");

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
