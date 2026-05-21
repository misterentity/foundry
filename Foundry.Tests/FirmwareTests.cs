using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Tests;

public class FirmwareTests
{
    private static ComponentKb Kb => ComponentKb.Demo();

    [Fact]
    public void PinMap_MatchesNetlist()
    {
        var entries = PinMap.Build(DemoData.SoilMoistureConnections(), Kb);

        // GPIO34 ↔ SENSOR.AOUT and GPIO0 ↔ BTN1.A are the two MCU signal nets.
        Assert.Contains(entries, e => e.Macro == "PIN_SENSOR_AOUT" && e.Gpio == 34);
        Assert.Contains(entries, e => e.Macro == "PIN_BTN1_A" && e.Gpio == 0);
        Assert.Equal(2, entries.Count);
        // GPIO0 is flagged as a strapping pin in the derived map.
        Assert.True(entries.Single(e => e.Gpio == 0).Strapping);
    }

    [Fact]
    public void PinMap_Regenerates_WhenWiringChanges()
    {
        var before = PinMap.Build(DemoData.SoilMoistureConnections(), Kb);
        Assert.Contains(before, e => e.Gpio == 0); // GPIO0 strapping

        // Remap the button from GPIO0 → GPIO13 (the validation auto-fix).
        var rewired = DemoData.SoilMoistureConnections();
        var idx = rewired.FindIndex(c => c.From == "MCU.GPIO0");
        rewired[idx] = new Connection { From = "MCU.GPIO13", To = "BTN1.A", Net = "signal" };

        var after = PinMap.Build(rewired, Kb);
        Assert.Contains(after, e => e.Macro == "PIN_BTN1_A" && e.Gpio == 13);
        Assert.DoesNotContain(after, e => e.Gpio == 0);
    }

    [Fact]
    public void Header_IsGeneratedAndMarkedDerived()
    {
        var entries = PinMap.Build(DemoData.SoilMoistureConnections(), Kb);
        var header = PinMap.RenderHeader(entries);

        Assert.Contains("#pragma once", header);
        Assert.Contains("GENERATED — derived from Project.connections", header);
        Assert.Contains("#define PIN_SENSOR_AOUT", header);
        Assert.Contains("34", header);
    }

    [Fact]
    public void Arduino_Generate_ProducesProjectFiles()
    {
        var fw = FirmwareGenerator.Generate(DemoData.SoilMoistureConnections(), Kb);

        Assert.Equal("Arduino C++", fw.Platform);
        // generic, netlist-driven scaffold: setup()/loop() reference the derived pin macros
        var main = fw.Files.Single(f => f.Name == "main.ino").Content;
        Assert.Contains("#include \"pinmap.h\"", main);
        Assert.Contains("void setup()", main);
        Assert.Contains("void loop()", main);
        Assert.Contains("PIN_SENSOR_AOUT", main);   // the soil sensor's analog pin is read in loop()
        Assert.Contains(fw.Files, f => f.Name == "pinmap.h" && f.Content.Contains("PIN_SENSOR_AOUT"));
        Assert.Contains(fw.Files, f => f.Name == "platformio.ini");
        Assert.All(fw.Files, f => Assert.False(string.IsNullOrWhiteSpace(f.Content)));
    }

    [Fact]
    public void MicroPython_Generate_ProducesPyFiles()
    {
        var fw = FirmwareGenerator.Generate(DemoData.SoilMoistureConnections(), Kb, FirmwarePlatform.MicroPython);

        Assert.Equal("MicroPython", fw.Platform);
        Assert.Contains(fw.Files, f => f.Name == "main.py");
        Assert.Contains(fw.Files, f => f.Name == "pinmap.py" && f.Content.Contains("PIN_SENSOR_AOUT = 34"));
    }

    [Fact]
    public void Exporter_WritesFilesToDisk()
    {
        var fw = FirmwareGenerator.Generate(DemoData.SoilMoistureConnections(), Kb);
        var dir = Path.Combine(Path.GetTempPath(), "foundry_fw_" + Guid.NewGuid().ToString("N"));
        try
        {
            FirmwareExporter.Export(fw, dir);
            Assert.True(File.Exists(Path.Combine(dir, "main.ino")));
            Assert.True(File.Exists(Path.Combine(dir, "pinmap.h")));
            Assert.Contains("PIN_SENSOR_AOUT", File.ReadAllText(Path.Combine(dir, "pinmap.h")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
