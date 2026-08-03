using Foundry.Core.Pcb;

namespace Foundry.Core.Kb;

/// <summary>How a component's pins were established.</summary>
public enum PinAuthority
{
    /// <summary>No authoritative pin table — the model's word is all there is.</summary>
    None,
    /// <summary>The user said where the pin goes (<see cref="ComponentSpec.PinOverrides"/>). Beats everything.</summary>
    Override,
    /// <summary>A curated, KiCad-free table Foundry maintains (<see cref="McuPinMap"/>).</summary>
    Curated,
    /// <summary>KiCad's own symbol library, keyed on the resolved footprint.</summary>
    Symbol,
    /// <summary>KiCad's symbol library, keyed on the PART identity (<see cref="ChipCatalog"/>).</summary>
    ChipIdentity,
}

/// <summary>What Foundry knows about one component's real pinout.</summary>
public sealed record PartIdentity(string Alias, string LibId, PinAuthority Authority)
{
    public bool IsGrounded => Authority != PinAuthority.None;
}

/// <summary>
/// The single place that answers "does this pin actually exist on this part?".
///
/// <para>
/// Foundry owns real, datasheet-grade pin data — a curated table, KiCad's symbol libraries, and a
/// part-identity catalogue for bare chips in generic packages. Until now that chain lived inlined at ONE
/// call site (<c>PcbJob.Build</c>), so only the PCB build could tell a real pin from an invented one.
/// Everywhere else the model's own JSON was treated as fact: <c>ProjectGenerator</c> builds the KB from
/// the model's reply, and the rules engine then grades the model with the model's own answer key.
/// </para>
///
/// <para>
/// Lifting it here lets generation check the model's pin claims against the same authority the board
/// build already refuses on — so a hallucinated pin is caught before it reaches firmware and a
/// breadboard, not after.
/// </para>
/// </summary>
public static class PartResolver
{
    /// <summary>
    /// KiCad's symbol dir, located once per process. Symbol-derived authority needs it; without KiCad
    /// only the curated table applies and everything else is honestly reported as ungrounded.
    /// </summary>
    private static readonly Lazy<string?> DefaultSymbolDir = new(() =>
    {
        try
        {
            var kicad = KiCadInstaller.Locate();
            if (kicad is null) return null;
            return Directory.Exists(kicad.SymbolDir) ? kicad.SymbolDir : null;
        }
        catch { return null; }
    });

    /// <summary>What authority, if any, backs this component's pinout.</summary>
    public static PartIdentity Identify(ComponentSpec spec, string? symbolDir = null)
    {
        symbolDir ??= DefaultSymbolDir.Value;
        var libId = FootprintMap.Resolve(spec, Math.Max(1, spec.Pins.Count)).LibId;

        // A user who has written the pad down for every pin IS the authority — that is the whole point of
        // the override, and treating them as ungrounded would report their own numbers back as unproven.
        if (spec.Pins.Count > 0 && spec.Pins.All(p => spec.PinOverrides.ContainsKey(p.Name)))
            return new PartIdentity(spec.Alias, libId, PinAuthority.Override);

        if (McuPinMap.Has(libId)) return new PartIdentity(spec.Alias, libId, PinAuthority.Curated);

        // Probe with a pin we know the part declares: a symbol that resolves ANY of them is authoritative.
        if (symbolDir is not null)
        {
            if (spec.Pins.Any(p => SymbolPinMap.ResolvePad(libId, p.Name, symbolDir) is not null))
                return new PartIdentity(spec.Alias, libId, PinAuthority.Symbol);

            if (ChipCatalog.Match(spec.Name) is { } chip &&
                spec.Pins.Any(p => SymbolPinMap.ResolvePadBySymbol(chip.SymbolLib, chip.SymbolName, p.Name, symbolDir) is not null))
                return new PartIdentity(spec.Alias, libId, PinAuthority.ChipIdentity);
        }

        return new PartIdentity(spec.Alias, libId, PinAuthority.None);
    }

    /// <summary>
    /// The real pad a logical pin maps to, or null when no authority can place it. This is the same
    /// resolution order <c>PcbJob.Build</c> uses, and the same order the fail-closed gate depends on:
    /// curated → symbol-by-footprint → symbol-by-part-identity → nothing.
    /// </summary>
    public static string? ResolvePad(ComponentSpec spec, string logicalPin, string? symbolDir = null)
    {
        symbolDir ??= DefaultSymbolDir.Value;
        var libId = FootprintMap.Resolve(spec, Math.Max(1, spec.Pins.Count)).LibId;
        return ResolvePad(spec, libId, logicalPin, symbolDir);
    }

    /// <summary>
    /// As <see cref="ResolvePad(ComponentSpec,string,string?)"/>, for callers that already resolved the
    /// footprint. <c>PcbJob.Build</c> uses this so the board build and validation share ONE chain — they
    /// had a copy each, and two copies of a fail-closed rule drift until the engine passes a pin the build
    /// refuses.
    /// </summary>
    public static string? ResolvePad(ComponentSpec spec, string libId, string logicalPin, string? symbolDir)
    {
        // Step 0: the user's own mapping wins over every automatic source.
        if (spec.PinOverrides.TryGetValue(logicalPin, out var forced) && !string.IsNullOrWhiteSpace(forced))
            return forced.Trim();

        return McuPinMap.ResolvePad(libId, logicalPin)
            ?? SymbolPinMap.ResolvePad(libId, logicalPin, symbolDir)
            ?? (ChipCatalog.Match(spec.Name) is { } chip
                ? SymbolPinMap.ResolvePadBySymbol(chip.SymbolLib, chip.SymbolName, logicalPin, symbolDir)
                : null);
    }

    /// <summary>
    /// Pins the model declared that no authority can place on the resolved footprint. Empty for a part
    /// Foundry has no authority over — absence of evidence is not evidence of a bad pin, and saying
    /// otherwise would fail every design carrying an unmapped part.
    /// </summary>
    public static IReadOnlyList<string> UnresolvablePins(ComponentSpec spec, string? symbolDir = null)
    {
        symbolDir ??= DefaultSymbolDir.Value;
        if (!Identify(spec, symbolDir).IsGrounded) return Array.Empty<string>();

        return spec.Pins
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Where(p => ResolvePad(spec, p.Name, symbolDir) is null)
            .Select(p => p.Name)
            .ToList();
    }
}
