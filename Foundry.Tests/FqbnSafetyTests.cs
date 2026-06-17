using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Tests;

public class FqbnSafetyTests
{
    private static Project Neutral(string board) => new()
    {
        Title = "Gadget",
        Prompt = "a small device",   // no MCU keyword -> the board hint passthrough path is exercised
        Components = new() { new ComponentSpec { Alias = "U1", Ref = "u1", Name = "Acme Controller" } },
        Firmware = new Firmware { Platform = "Arduino C++", Board = board },
    };

    // AI-controlled Firmware.Board reaches `arduino-cli compile --fqbn {fqbn}`. A 2-colon board hint carrying an
    // injected flag (e.g. --additional-urls / --build-path) must NOT pass through — it could run attacker code at
    // compile time. Fqbn() must hand back only a clean, valid FQBN (no spaces, no flags).
    [Fact]
    public void Fqbn_InjectedFlagInBoardHint_IsRejected()
    {
        var fqbn = FirmwareBuilder.Fqbn(Neutral("acme:avr:custom --build-path /tmp/evil"));
        Assert.True(FirmwareBuilder.IsValidFqbn(fqbn), $"Fqbn returned an unsafe value: {fqbn}");
        Assert.DoesNotContain(" ", fqbn);
        Assert.DoesNotContain("--", fqbn);
    }

    [Fact]
    public void Fqbn_AdditionalUrlsInjection_IsRejected()
    {
        var fqbn = FirmwareBuilder.Fqbn(Neutral("acme:avr:custom --additional-urls=http_evil"));
        Assert.True(FirmwareBuilder.IsValidFqbn(fqbn));
        Assert.DoesNotContain("additional-urls", fqbn);
    }

    [Fact]
    public void Fqbn_LegitExplicitFqbn_PassesThrough()
    {
        // A clean explicit FQBN for a chip the keyword inference doesn't know must still be honored.
        Assert.Equal("teensy:avr:teensy40", FirmwareBuilder.Fqbn(Neutral("teensy:avr:teensy40")));
    }

    [Fact]
    public void Fqbn_KnownChip_StillInfers()
    {
        var p = Neutral("");
        p.Components.Add(new ComponentSpec { Alias = "U2", Ref = "esp", Name = "ESP32-WROOM-32" });
        Assert.Equal("esp32:esp32:esp32", FirmwareBuilder.Fqbn(p));
    }
}
