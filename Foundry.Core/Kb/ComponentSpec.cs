namespace Foundry.Core.Kb;

public enum PinKind { Power, Ground, Input, Output, Bidir, Analog }

/// <summary>One pin on a component, with the electrical attributes validation needs (PRD §8.8/§9).</summary>
public sealed class PinSpec
{
    public required string Name { get; init; }
    public required PinKind Kind { get; init; }
    /// <summary>Pin can only be an input — driving it as an output is invalid (e.g. ESP32 GPIO34–39).</summary>
    public bool InputOnly { get; init; }
    /// <summary>Boot strapping pin — using it for I/O risks affecting boot mode (e.g. ESP32 GPIO0).</summary>
    public bool Strapping { get; init; }
    /// <summary>True if a 3.3V-logic input tolerates 5V drive.</summary>
    public bool FiveVoltTolerant { get; init; }
}

/// <summary>
/// Structured electrical + physical spec for a maker component (PRD §6 components / §9 KB).
/// Keyed in the KB by its netlist <see cref="Alias"/> (the prefix used in connection endpoints,
/// e.g. "MCU" for "MCU.GPIO34").
/// </summary>
public sealed class ComponentSpec
{
    public required string Ref { get; init; }
    public required string Alias { get; init; }
    public required string Name { get; init; }

    /// <summary>Operating logic level in volts (e.g. 3.3), or null if not a logic device.</summary>
    public double? LogicV { get; init; }
    /// <summary>Acceptable supply-voltage range on power-input pins, [min,max] volts.</summary>
    public double[]? InputVRange { get; init; }
    /// <summary>Voltage this part sources on its power-output pins (e.g. regulator VOUT, battery +).</summary>
    public double? OutputV { get; init; }
    /// <summary>Active current draw in mA.</summary>
    public int CurrentMaActive { get; init; }
    /// <summary>Battery capacity in mAh (only for cells).</summary>
    public int CapacityMah { get; init; }
    /// <summary>I²C 7-bit address if this part sits on an I²C bus, else null.</summary>
    public int? I2cAddress { get; init; }

    /// <summary>
    /// Explicit KiCad footprint lib id ("Lib:Footprint", e.g. "Resistor_SMD:R_0805_2012Metric").
    /// Authoritative when set; otherwise <see cref="Foundry.Core.Pcb.FootprintMap"/> infers one
    /// from keyword heuristics. Mirrors how <see cref="Ref"/>/MPN already carry sourcing identity.
    /// </summary>
    public string? Footprint { get; init; }

    public List<PinSpec> Pins { get; init; } = new();

    public PinSpec? Pin(string name) =>
        Pins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
