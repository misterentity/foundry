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

    // ---- third-party board indexes ----------------------------------------------------------------
    //
    // Fqbn() infers esp32:esp32:esp32 for the README's flagship device, but that platform is NOT in
    // arduino-cli's built-in index — so `core install esp32:esp32` could never succeed and every
    // compile/flash/simulate path for ESP32, ESP8266 and RP2040 was dead. These are the URLs that fix it.

    [Theory]
    [InlineData("esp32:esp32:esp32", "package_esp32_index.json")]
    [InlineData("esp8266:esp8266:nodemcuv2", "package_esp8266com_index.json")]
    [InlineData("rp2040:rp2040:rpipico", "package_rp2040_index.json")]
    public void ThirdPartyVendors_GetTheirBoardManagerIndex(string fqbn, string expectedIndex)
    {
        var arg = FirmwareBuilder.AdditionalUrlsArg(fqbn);
        Assert.StartsWith(" --additional-urls https://", arg);
        Assert.Contains(expectedIndex, arg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("arduino:avr:uno")]
    [InlineData("arduino:avr:nano")]
    public void BuiltInVendor_GetsNoAdditionalIndex(string fqbn) =>
        Assert.Equal("", FirmwareBuilder.AdditionalUrlsArg(fqbn));

    // The index URLs are OURS. A model-supplied board hint must never be able to introduce one — that is
    // exactly the injection Fqbn() refuses, and it stays refused now that real indexes are in play.
    [Fact]
    public void ModelSuppliedIndexUrl_StillCannotReachTheCommandLine()
    {
        var fqbn = FirmwareBuilder.Fqbn(
            Neutral("acme:avr:board --additional-urls https://evil.example/package_index.json"));
        Assert.DoesNotContain("additional-urls", fqbn, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", fqbn, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", FirmwareBuilder.AdditionalUrlsArg(fqbn));   // and the vendor resolves to no index
    }

    // ---- declared library installation -------------------------------------------------------------

    [Theory]
    [InlineData("PubSubClient", "2.8", "PubSubClient@2.8")]
    [InlineData("Adafruit BME280 Library", "", "Adafruit BME280 Library")]
    [InlineData("ArduinoJson", "6.21.3", "ArduinoJson@6.21.3")]
    public void RealLibraries_ResolveToAnInstallSpec(string name, string version, string expected) =>
        Assert.Equal(expected, FirmwareBuilder.LibraryInstallSpec(name, version));

    // Core-bundled and "built-in" entries are not in the library index — installing them just errors.
    [Theory]
    [InlineData("Wire", "built-in")]
    [InlineData("Wire (I2C)", "built-in")]
    [InlineData("Arduino core", "built-in")]
    [InlineData("SPI", "")]
    [InlineData("EEPROM", "1.0")]
    [InlineData("", "1.0")]
    public void BundledLibraries_AreSkipped(string name, string version) =>
        Assert.Null(FirmwareBuilder.LibraryInstallSpec(name, version));

    // A library name reaches a command line, so it is validated as strictly as the FQBN is.
    [Theory]
    [InlineData("Evil; rm -rf /", "1.0")]
    [InlineData("Lib --additional-urls https://evil.example/x.json", "1.0")]
    [InlineData("Lib\"quote", "1.0")]
    [InlineData("Lib&whoami", "1.0")]
    public void UnsafeLibraryNames_AreRefused(string name, string version) =>
        Assert.Null(FirmwareBuilder.LibraryInstallSpec(name, version));

    // A malformed VERSION must degrade to "latest", never smuggle arguments through the @ suffix.
    [Fact]
    public void UnsafeLibraryVersion_DegradesToLatest() =>
        Assert.Equal("PubSubClient", FirmwareBuilder.LibraryInstallSpec("PubSubClient", "2.8 --config-file evil.yaml"));
}
