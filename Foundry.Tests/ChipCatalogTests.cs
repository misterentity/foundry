using Foundry.Core.Pcb;

namespace Foundry.Tests;

public class ChipCatalogTests
{
    [Theory]
    [InlineData("STM32F103C8T6")]
    [InlineData("STM32F103C8 (Blue Pill)")]
    [InlineData("Blue Pill board")]
    public void Match_KnownChip_ReturnsItsSymbolAndPackage(string name)
    {
        var chip = ChipCatalog.Match(name);
        Assert.NotNull(chip);
        Assert.Equal("STM32F103C8Tx", chip!.SymbolName);
        Assert.Contains("LQFP-48", chip.FootprintLibId);
    }

    // The SAFETY property: a DIFFERENT chip that merely shares a package must NOT be mapped onto the F103
    // pinout. Generic keywords ("stm32") are deliberately not in the catalog — only part-specific ones — so a
    // non-cataloged part falls through to the fail-closed gate, never mis-wired.
    [Theory]
    [InlineData("STM32F407VGT6")]       // a different STM32 (LQFP-100) — must NOT match F103
    [InlineData("STM32F411")]           // different STM32 — no match
    [InlineData("ATSAMD21G18 (LQFP-48)")] // a different chip in the SAME LQFP-48 package — must NOT match F103
    [InlineData("ACME-1234 op-amp")]
    [InlineData("")]
    [InlineData(null)]
    public void Match_UnknownOrDifferentChip_ReturnsNull(string? name)
    {
        Assert.Null(ChipCatalog.Match(name));
    }
}
