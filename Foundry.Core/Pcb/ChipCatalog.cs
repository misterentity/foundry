namespace Foundry.Core.Pcb;

/// <summary>
/// Part-IDENTITY → (footprint + KiCad symbol) for bare chips that live in a GENERIC package footprint shared by
/// many different parts (e.g. an STM32F103C8 in Package_QFP:LQFP-48). For these, the footprint alone can't
/// identify the chip — so unlike <see cref="McuPinMap"/>/<see cref="SymbolPinMap"/> (keyed on the footprint),
/// the pin map MUST be keyed on the PART. We match the component's name to a known chip, give it the correct
/// package footprint, and point <see cref="SymbolPinMap.ResolvePadBySymbol"/> at that chip's authoritative KiCad
/// symbol so logical pins (PA0, PB1, …) resolve to real pads. A part NOT in the catalog falls through to the
/// fail-closed gate — never mis-mapped onto another chip's pinout just because they share a package.
/// </summary>
public static class ChipCatalog
{
    public sealed record Chip(string FootprintLibId, string SymbolLib, string SymbolName, string Display);

    // Keyword (substring, lower-cased) → chip. Keep keywords SPECIFIC to one part — a generic token like "stm32"
    // must NOT map every STM32 to one variant (that's the mis-map risk this whole mechanism exists to avoid).
    private static readonly (string[] Keywords, Chip Chip)[] Entries =
    {
        (new[] { "stm32f103c8", "stm32f103", "blue pill", "bluepill" },
            new Chip("Package_QFP:LQFP-48_7x7mm_P0.5mm", "MCU_ST_STM32F1", "STM32F103C8Tx", "STM32F103C8 (Blue Pill)")),
    };

    /// <summary>The known chip whose identity matches <paramref name="name"/> (component name/MPN), or null.</summary>
    public static Chip? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var hay = name.ToLowerInvariant();
        foreach (var (keywords, chip) in Entries)
            if (keywords.Any(k => hay.Contains(k))) return chip;
        return null;
    }
}
