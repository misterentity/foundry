using System.Reflection;
using Foundry.Core.Firmware;
using Foundry.Core.Generation;
using Foundry.Core.Project;

namespace Foundry.Tests;

// Regressions for the defects found by mining %AppData%\Foundry\logs. Counts are occurrences across the
// retained log history (2026-05-21 .. 2026-08-03), all still live on the last day.
public class FirmwareBuilderDefectTests
{
    // ---- 66x  "image build error: Index was out of range ... (Parameter 'index')" ----
    //
    // PickMainFile ended `?? files[0]`, which throws ArgumentOutOfRangeException on an empty list.
    // CompileAsync's catch-all reported it as "Couldn't run the compiler: Index was out of range. Must be
    // non-negative and less than the size of the collection. (Parameter 'index')" -- the single most
    // frequent error in the log, shown verbatim to the user, and triggered whenever a build ran before the
    // firmware pass had produced anything.

    [Fact]
    public void PickMainFile_OnAnEmptySet_ReturnsNullInsteadOfThrowing()
    {
        var ex = Record.Exception(() => ProjectGenerator.PickMainFile(Array.Empty<FirmwareFile>()));
        Assert.Null(ex);
        Assert.Null(ProjectGenerator.PickMainFile(Array.Empty<FirmwareFile>()));
    }

    [Fact]
    public void PickMainFile_StillPrefersMain_ThenTheLargestSource()
    {
        var files = new[]
        {
            new FirmwareFile { Name = "config.h", Content = "x" },
            new FirmwareFile { Name = "main.ino", Content = "y" },
        };
        Assert.Equal("main.ino", ProjectGenerator.PickMainFile(files)!.Name);

        var noMain = new[]
        {
            new FirmwareFile { Name = "small.ino", Content = "x" },
            new FirmwareFile { Name = "big.ino", Content = "xxxxxxxxxx" },
        };
        Assert.Equal("big.ino", ProjectGenerator.PickMainFile(noMain)!.Name);
    }

    // A file set with no recognised source extension still resolves to something rather than throwing.
    [Fact]
    public void PickMainFile_WithNoSourceFiles_FallsBackToTheFirst() =>
        Assert.Equal("notes.txt", ProjectGenerator.PickMainFile(
            new[] { new FirmwareFile { Name = "notes.txt", Content = "x" } })!.Name);

    [Fact]
    public async Task CompilingWithNoFirmware_SaysSo_RatherThanReportingAnIndexError()
    {
        var p = new Project { Title = "T" };
        p.Firmware = new Foundry.Core.Project.Firmware { Platform = "Arduino C++", Files = new List<FirmwareFile>() };

        var r = await FirmwareBuilder.CompileAsync(p);

        Assert.DoesNotContain("Index was out of range", r.Summary);
        Assert.Contains("No firmware to compile", r.Summary);
    }

    [Fact]
    public async Task BuildingAnImageWithNoFirmware_SaysSo_RatherThanReportingAnIndexError()
    {
        var p = new Project { Title = "T" };
        p.Firmware = new Foundry.Core.Project.Firmware { Platform = "Arduino C++", Files = new List<FirmwareFile>() };

        var img = await FirmwareBuilder.CompileToImageAsync(p, Path.Combine(Path.GetTempPath(), "foundry-img-test"));

        Assert.False(img.Ok);
        Assert.DoesNotContain(img.Diagnostics, d => d.Message.Contains("Index was out of range"));
        Assert.Contains(img.Diagnostics, d => d.Message.Contains("No firmware to compile"));
    }

    // ---- 57x  "board core STM32:stm32 install failed - Platform 'STM32:stm32' not found" ----
    //
    // STM32 was supported everywhere else (ChipCatalog, SimulatorFactory, RenodeReplGenerator) but absent
    // from Fqbn's inference list, so an STM32 design fell through to the model's own board hint. The model
    // returns "STM32:stm32", a vendor id that does not exist -- STM32duino publishes as "STMicroelectronics".

    private static Project WithPrompt(string prompt) => new() { Title = "T", Prompt = prompt };

    [Theory]
    [InlineData("an stm32 data logger")]
    [InlineData("a Blue Pill based controller")]
    [InlineData("bluepill temperature sensor")]
    [InlineData("STM32F103C8 motor driver")]
    public void AnStm32Design_InfersTheRealVendorId(string prompt)
    {
        var fqbn = FirmwareBuilder.Fqbn(WithPrompt(prompt));

        Assert.StartsWith("STMicroelectronics:stm32:", fqbn);
        Assert.DoesNotContain("STM32:stm32", fqbn);
        Assert.True(FirmwareBuilder.IsValidFqbn(fqbn), $"inferred FQBN must stay exec-safe: {fqbn}");
    }

    // Inferring the core is useless without its package index -- the platform is not in arduino-cli's
    // built-in index, so `core install` could never resolve it either way.
    [Fact]
    public void TheStm32CoreHasAPackageIndex() =>
        Assert.Contains("stm32duino", FirmwareBuilder.AdditionalUrlsArg("STMicroelectronics:stm32:GenF1"),
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EveryVendorFoundryInfers_HasAnIndexOrIsBuiltIn()
    {
        foreach (var prompt in new[] { "esp32 sensor", "esp8266 nodemcu", "rp2040 pico", "stm32 logger",
                                       "arduino uno", "arduino mega", "arduino nano" })
        {
            var fqbn = FirmwareBuilder.Fqbn(WithPrompt(prompt));
            var vendor = fqbn.Split(':')[0];
            var hasIndex = FirmwareBuilder.AdditionalUrlsArg(fqbn).Length > 0;
            Assert.True(hasIndex || vendor.Equals("arduino", StringComparison.OrdinalIgnoreCase),
                $"'{prompt}' infers {fqbn} but vendor '{vendor}' has no board-manager index registered.");
        }
    }

    // ESP32 must keep winning when both tokens appear -- STM32 was inserted into an ordered chain.
    [Fact]
    public void TheInferenceOrderIsUnchangedForExistingBoards()
    {
        Assert.Equal("esp32:esp32:esp32", FirmwareBuilder.Fqbn(WithPrompt("esp32 talking to an stm32 coprocessor")));
        Assert.Equal("arduino:avr:uno", FirmwareBuilder.Fqbn(WithPrompt("a plain arduino uno blinker")));
        Assert.Equal("rp2040:rp2040:rpipico", FirmwareBuilder.Fqbn(WithPrompt("raspberry pi pico project")));
    }
}

// AppInfo.Version was a hardcoded const ("2.6.0") while Foundry.App.csproj stamped the exe 2.7.1. The
// class comment called itself "single source of truth for the updater and UI" while being the drifted
// copy. App.xaml.cs feeds this value to the update check, so a frozen constant makes the updater compare
// the wrong version against the latest release and offer an update the user already has -- every launch.
public class AppVersionTests
{
    [Fact]
    public void TheReportedVersionMatchesTheAssemblyItWasBuiltFrom()
    {
        var asm = typeof(Foundry.Core.AppInfo).Assembly;
        var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = info.Contains('+') ? info[..info.IndexOf('+')] : info;

        Assert.Equal(expected, Foundry.Core.AppInfo.Version);
    }

    [Fact]
    public void ItIsAPlainThreePartVersion_WithNoBuildMetadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", Foundry.Core.AppInfo.Version);
        Assert.DoesNotContain("+", Foundry.Core.AppInfo.Version);
    }

    // The specific regression: a version that no longer tracks the build.
    [Fact]
    public void ItIsNotTheStaleHardcodedConstant() =>
        Assert.NotEqual("2.6.0", Foundry.Core.AppInfo.Version);
}
