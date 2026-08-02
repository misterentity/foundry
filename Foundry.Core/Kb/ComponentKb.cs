namespace Foundry.Core.Kb;

/// <summary>
/// Curated component knowledge base (PRD §9). Maps netlist aliases to structured electrical specs
/// so the validation engine, wiring, and firmware pin maps can all derive from real part data.
/// v1 ships the parts used by the demo project; runtime augmentation from sourcing/Claude is future.
/// </summary>
public sealed class ComponentKb
{
    private readonly Dictionary<string, ComponentSpec> _byAlias;

    public ComponentKb(IEnumerable<ComponentSpec> specs) =>
        _byAlias = specs.ToDictionary(s => s.Alias, StringComparer.OrdinalIgnoreCase);

    public ComponentSpec? ByAlias(string alias) =>
        _byAlias.TryGetValue(alias, out var s) ? s : null;

    public IEnumerable<ComponentSpec> All => _byAlias.Values;

    /// <summary>The soil-moisture demo's parts, with electrical specs for validation.</summary>
    public static ComponentKb Demo() => new(new[]
    {
        new ComponentSpec
        {
            Ref = "esp32_devkit", Alias = "MCU", Name = "ESP32 DevKit v1",
            // accepts the 3.3V regulated rail or USB 5V on its supply pins
            LogicV = 3.3, InputVRange = new[] { 3.0, 5.5 }, CurrentMaActive = 80,
            Pins = new()
            {
                new PinSpec { Name = "3V3", Kind = PinKind.Power },
                new PinSpec { Name = "5V",  Kind = PinKind.Power },
                new PinSpec { Name = "GND", Kind = PinKind.Ground },
                new PinSpec { Name = "GPIO34", Kind = PinKind.Analog, InputOnly = true },
                new PinSpec { Name = "GPIO0",  Kind = PinKind.Bidir, Strapping = true },
                new PinSpec { Name = "GPIO13", Kind = PinKind.Bidir },
            },
        },
        new ComponentSpec
        {
            Ref = "cap_sensor_v1", Alias = "SENSOR", Name = "Capacitive Soil v1.2",
            LogicV = 3.3, InputVRange = new[] { 3.0, 5.5 }, CurrentMaActive = 5,
            Pins = new()
            {
                new PinSpec { Name = "VCC",  Kind = PinKind.Power },
                new PinSpec { Name = "GND",  Kind = PinKind.Ground },
                new PinSpec { Name = "AOUT", Kind = PinKind.Output }, // analog out, sensor drives
            },
        },
        new ComponentSpec
        {
            Ref = "mcp1700_3302e", Alias = "REG", Name = "MCP1700-3302E",
            InputVRange = new[] { 2.3, 6.0 }, OutputV = 3.3, CurrentMaActive = 0,
            Pins = new()
            {
                new PinSpec { Name = "VIN",  Kind = PinKind.Power },
                new PinSpec { Name = "GND",  Kind = PinKind.Ground },
                new PinSpec { Name = "VOUT", Kind = PinKind.Output },
            },
        },
        new ComponentSpec
        {
            // 14500 = AA-sized Li-ion. Same 3.7 V chemistry as an 18650 (so the TP4056 / USB-C charging
            // path is unchanged) in a holder with a 57.5 × 17.4 mm courtyard instead of 88 × 21.75 —
            // the 18650 holder alone made the demo board too long for any pocket-sized case.
            Ref = "li_14500", Alias = "BAT", Name = "14500 Li-ion 800mAh (AA)",
            OutputV = 3.7, CapacityMah = 800,
            Pins = new()
            {
                new PinSpec { Name = "+", Kind = PinKind.Power },
                new PinSpec { Name = "-", Kind = PinKind.Ground },
            },
        },
        new ComponentSpec
        {
            Ref = "tact_switch", Alias = "BTN1", Name = "Tactile Switch 6×6mm",
            Pins = new()
            {
                new PinSpec { Name = "A", Kind = PinKind.Bidir },
                new PinSpec { Name = "B", Kind = PinKind.Ground },
            },
        },
    });
}
