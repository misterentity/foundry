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

    // ---- the emitted literal is the pin's identity, which is not always its trailing number ----
    //
    // ExtractGpio took the trailing digits of any pin name, so an STM32 "PA5" became `#define PIN_X 5`.
    // That compiles, flashes, and drives an unrelated pad. Worse, PA5 and PB5 both became 5, so two
    // peripherals shared one define and one of them silently did nothing.

    private static ComponentKb McuKb(params string[] mcuPins) => new(new[]
    {
        new ComponentSpec
        {
            Ref = "u1", Alias = "MCU", Name = "MCU", LogicV = 3.3,
            Pins = mcuPins.Select(p => new PinSpec { Name = p, Kind = PinKind.Bidir }).ToList(),
        },
        new ComponentSpec
        {
            Ref = "d1", Alias = "LED", Name = "LED",
            Pins = new() { new PinSpec { Name = "A", Kind = PinKind.Input },
                           new PinSpec { Name = "B", Kind = PinKind.Input } },
        },
    });

    private static List<Connection> Wire(params (string mcu, string periph)[] nets) =>
        nets.Select(n => new Connection { From = $"MCU.{n.mcu}", To = $"LED.{n.periph}", Net = "signal" }).ToList();

    [Theory]
    [InlineData("PA5", "PA5")]     // STM32 port A bit 5 — never the integer 5
    [InlineData("PB0", "PB0")]
    [InlineData("PK15", "PK15")]
    [InlineData("A0", "A0")]       // Arduino analog 0 is 14 on an Uno; 0 is the serial TX line
    [InlineData("A7", "A7")]
    [InlineData("GPIO34", "34")]   // ESP32 — the number IS the identity
    [InlineData("GP25", "25")]     // Pico
    [InlineData("D13", "13")]      // Arduino digital
    [InlineData("IO4", "4")]
    public void EmittedLiteral_IsThePinsIdentity(string pinName, string expected)
    {
        var entry = Assert.Single(PinMap.Build(Wire((pinName, "A")), McuKb(pinName)));
        Assert.Equal(expected, entry.Emit);
        Assert.Contains($"{entry.Macro} ".TrimEnd(), PinMap.RenderHeader(new[] { entry }));
        Assert.Contains(expected, PinMap.RenderHeader(new[] { entry }));
    }

    [Fact]
    public void TwoPortsOfTheSameBit_DoNotCollapseOntoOnePin()
    {
        var entries = PinMap.Build(Wire(("PA5", "A"), ("PB5", "B")), McuKb("PA5", "PB5"));

        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "PA5", "PB5" }, entries.Select(e => e.Emit).OrderBy(x => x).ToArray());
        // ...and the header defines two different pads, not the same one twice.
        var header = PinMap.RenderHeader(entries);
        Assert.Contains("PA5", header);
        Assert.Contains("PB5", header);
    }

    [Fact]
    public void MicroPython_QuotesSymbolicPins_AndDropsTheArduinoPPrefix()
    {
        var stm = Assert.Single(PinMap.Build(Wire(("PA5", "A")), McuKb("PA5")));
        Assert.Equal("'A5'", stm.PyEmit);        // machine.Pin('A5')

        var ard = Assert.Single(PinMap.Build(Wire(("A0", "A")), McuKb("A0")));
        Assert.Equal("'A0'", ard.PyEmit);

        var esp = Assert.Single(PinMap.Build(Wire(("GPIO34", "A")), McuKb("GPIO34")));
        Assert.Equal("34", esp.PyEmit);          // machine.Pin(34) — no quotes
    }

    [Fact]
    public void NumericPins_KeepTheirBareLiteral_SoExistingBoardsAreUnaffected()
    {
        foreach (var e in PinMap.Build(DemoData.SoilMoistureConnections(), Kb))
        {
            Assert.Equal("", e.Token);
            Assert.Equal(e.Gpio.ToString(), e.Emit);
        }
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

    [Theory]
    [InlineData("GP5")]      // Raspberry Pi Pico silkscreen
    [InlineData("D13")]      // Arduino digital
    [InlineData("A0")]       // Arduino analog
    [InlineData("P0.28")]    // nRF port.pin
    [InlineData("PA5")]      // STM32 port A pin 5
    [InlineData("PB5")]      // STM32 port B pin 5 (shares the pin number with PA5 — must still detect)
    public void DetectMcuAlias_FindsNonGpioNamedMcus(string mcuPinName)
    {
        // Before the fix only pins literally starting with "GPIO" were recognized, so Pico/AVR/nRF MCUs went
        // undetected and Build() silently returned an empty pin map.
        var mcu = new ComponentSpec
        {
            Ref = "mcu", Alias = "MCU", Name = "Board",
            Pins = new()
            {
                new PinSpec { Name = mcuPinName, Kind = PinKind.Bidir },
                new PinSpec { Name = "GND", Kind = PinKind.Ground },
            },
        };
        var dev = new ComponentSpec { Ref = "d", Alias = "DEV", Name = "Sensor",
            Pins = new() { new PinSpec { Name = "OUT", Kind = PinKind.Output } } };
        var conns = new List<Connection> { new() { From = $"MCU.{mcuPinName}", To = "DEV.OUT", Net = "signal" } };
        Assert.Equal("MCU", PinMap.DetectMcuAlias(conns, new ComponentKb(new[] { mcu, dev })));
    }

    [Fact]
    public void DetectMcuAlias_PicksThePartWithTheMostGpioPins()
    {
        var mcu = new ComponentSpec { Ref = "mcu", Alias = "MCU", Name = "ESP32",
            Pins = Enumerable.Range(0, 20).Select(i => new PinSpec { Name = $"GPIO{i}", Kind = PinKind.Bidir }).ToList() };
        var conn = new ComponentSpec { Ref = "j", Alias = "J1", Name = "Header",   // incidental D1-style pad, not the MCU
            Pins = new() { new PinSpec { Name = "D1", Kind = PinKind.Bidir } } };
        var conns = new List<Connection> { new() { From = "MCU.GPIO5", To = "J1.D1", Net = "signal" } };
        Assert.Equal("MCU", PinMap.DetectMcuAlias(conns, new ComponentKb(new[] { mcu, conn })));
    }

    [Fact]
    public void Build_NoMcuDetected_ReturnsEmpty()
    {
        // two passives, no MCU — the pin map is legitimately empty (and Build logs a warning, not silence).
        var a = new ComponentSpec { Ref = "r1", Alias = "R1", Name = "Resistor", Pins = new() { new PinSpec { Name = "1", Kind = PinKind.Bidir }, new PinSpec { Name = "2", Kind = PinKind.Bidir } } };
        var b = new ComponentSpec { Ref = "c1", Alias = "C1", Name = "Capacitor", Pins = new() { new PinSpec { Name = "1", Kind = PinKind.Bidir }, new PinSpec { Name = "2", Kind = PinKind.Bidir } } };
        var conns = new List<Connection> { new() { From = "R1.1", To = "C1.1", Net = "signal" } };
        Assert.Empty(PinMap.Build(conns, new ComponentKb(new[] { a, b })));
    }
}

