using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Tests;

public class ValidationFixTests
{
    private static Project MakeProject()
    {
        var mcu = new ComponentSpec
        {
            Ref = "esp32", Alias = "MCU", Name = "ESP32", LogicV = 3.3,
            Pins = new()
            {
                new PinSpec { Name = "3V3", Kind = PinKind.Power },
                new PinSpec { Name = "GND", Kind = PinKind.Ground },
                new PinSpec { Name = "GPIO0", Kind = PinKind.Bidir, Strapping = true },
                new PinSpec { Name = "GPIO13", Kind = PinKind.Bidir },
            },
        };
        var sensor = new ComponentSpec
        {
            Ref = "sen", Alias = "SENSOR", Name = "Sensor", LogicV = 3.3,
            Pins = new()
            {
                new PinSpec { Name = "VCC", Kind = PinKind.Power },
                new PinSpec { Name = "GND", Kind = PinKind.Ground },
                new PinSpec { Name = "OUT", Kind = PinKind.Output },
            },
        };
        return new Project
        {
            Components = new() { mcu, sensor },
            Connections = new()
            {
                new Connection { From = "MCU.3V3", To = "SENSOR.VCC", Net = "power" },
                new Connection { From = "MCU.GND", To = "SENSOR.GND", Net = "ground" },
                new Connection { From = "MCU.GPIO0", To = "SENSOR.OUT", Net = "signal" }, // strapping misuse
            },
        };
    }

    [Fact]
    public void Revalidate_FlagsStrappingPin()
    {
        var p = MakeProject();
        ProjectValidator.Revalidate(p);
        Assert.Contains(p.Findings, f => f.Code == "PIN-04");
    }

    [Fact]
    public void AutoFix_RemapsStrappingPin_AndClearsFinding()
    {
        var p = MakeProject();
        ProjectValidator.Revalidate(p);
        var finding = p.Findings.First(f => f.Code == "PIN-04");

        Assert.True(ProjectValidator.CanAutoFix(finding));
        Assert.True(ProjectValidator.TryAutoFix(p, finding));

        // the GPIO0 connection was moved to the free GPIO13
        Assert.DoesNotContain(p.Connections, c => c.From == "MCU.GPIO0" || c.To == "MCU.GPIO0");
        Assert.Contains(p.Connections, c => c.From == "MCU.GPIO13" || c.To == "MCU.GPIO13");

        ProjectValidator.Revalidate(p);
        Assert.DoesNotContain(p.Findings, f => f.Code == "PIN-04");
    }

    [Fact]
    public void Sample_StrappingFinding_IsAutoFixable_AndApplies()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var strap = p.Findings.FirstOrDefault(f => f.Code == "PIN-04");
        Assert.NotNull(strap);
        Assert.True(strap!.AutoFixable);
        Assert.True(ProjectValidator.CanAutoFix(strap));

        Assert.True(ProjectValidator.TryAutoFix(p, strap));
        ProjectValidator.Revalidate(p);
        Assert.DoesNotContain(p.Findings, f => f.Code == "PIN-04");
    }

    [Fact]
    public void NonNetlistFix_IsNotAutoFixable()
    {
        // e.g. a battery-life or sourcing fix has a Fix label but no deterministic netlist edit
        var f = new Finding { Code = "PWR-02", Fix = "Auto-tune duty" };
        Assert.False(f.AutoFixable);
        Assert.False(ProjectValidator.CanAutoFix(f));
    }

    [Fact]
    public void AutoFix_ThreeWayPinConflict_MovesLosersToDistinctPins_AndClears()
    {
        // Three outputs on one MCU pin. The fix must move the two losers to TWO DIFFERENT free GPIOs — the old
        // code moved both onto the same pin, just re-creating the conflict while reporting success.
        var mcu = new ComponentSpec
        {
            Ref = "esp32", Alias = "MCU", Name = "ESP32", LogicV = 3.3,
            Pins = new()
            {
                new PinSpec { Name = "GPIO5", Kind = PinKind.Bidir },
                new PinSpec { Name = "GPIO13", Kind = PinKind.Bidir },
                new PinSpec { Name = "GPIO14", Kind = PinKind.Bidir },
            },
        };
        ComponentSpec Drv(string a) => new() { Ref = a, Alias = a, Name = a, LogicV = 3.3, Pins = new() { new PinSpec { Name = "S", Kind = PinKind.Output } } };
        var p = new Project
        {
            Components = new() { mcu, Drv("A"), Drv("B"), Drv("C") },
            Connections = new()
            {
                new Connection { From = "A.S", To = "MCU.GPIO5", Net = "signal" },
                new Connection { From = "B.S", To = "MCU.GPIO5", Net = "signal" },
                new Connection { From = "C.S", To = "MCU.GPIO5", Net = "signal" },
            },
        };
        ProjectValidator.Revalidate(p);
        var conf = p.Findings.First(f => f.Code == "PIN-CONF");
        Assert.True(ProjectValidator.TryAutoFix(p, conf));

        var mcuEps = p.Connections.Select(c => new[] { c.From, c.To }.First(e => e.StartsWith("MCU."))).ToList();
        Assert.Equal(3, mcuEps.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // three DISTINCT pins

        ProjectValidator.Revalidate(p);
        Assert.DoesNotContain(p.Findings, f => f.Code == "PIN-CONF");                  // genuinely cleared
    }

    [Fact]
    public void ConnectRail_Power_RefusesNonSupplyAndWrongVoltage()
    {
        // 3.3V-only sink that needs power. The only other parts are NOT valid supplies for it:
        // OTHER has a VCC INPUT (no OutputV — wiring to it would be input-to-input); REG5 outputs 5V (out of range).
        var sink = new ComponentSpec { Ref = "s", Alias = "SENSOR", Name = "3V3 Sensor", LogicV = 3.3, InputVRange = new[] { 3.0, 3.6 },
            Pins = new() { new PinSpec { Name = "VCC", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var other = new ComponentSpec { Ref = "o", Alias = "OTHER", Name = "Other Sensor", LogicV = 3.3,   // no OutputV
            Pins = new() { new PinSpec { Name = "VCC", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var reg5 = new ComponentSpec { Ref = "r5", Alias = "REG5", Name = "5V Reg", OutputV = 5.0,
            Pins = new() { new PinSpec { Name = "VOUT", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var p = new Project
        {
            Components = new() { sink, other, reg5 },
            Connections = new() { new Connection { From = "REG5.GND", To = "SENSOR.GND", Net = "ground" } },
        };
        ProjectValidator.Revalidate(p);
        var pwr = new Finding { Code = "PWR-NC", Refs = new() { "SENSOR" } };   // target SENSOR deterministically
        Assert.False(ProjectValidator.TryAutoFix(p, pwr));               // no compatible supply → refuse
        Assert.DoesNotContain(p.Connections, c => c.Net == "power");
    }

    [Fact]
    public void ConnectRail_Power_WiresFromACompatibleRegulator()
    {
        var sink = new ComponentSpec { Ref = "s", Alias = "SENSOR", Name = "3V3 Sensor", LogicV = 3.3, InputVRange = new[] { 3.0, 3.6 },
            Pins = new() { new PinSpec { Name = "VCC", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var reg = new ComponentSpec { Ref = "reg", Alias = "REG", Name = "3V3 Regulator", OutputV = 3.3,
            Pins = new() { new PinSpec { Name = "VOUT", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var p = new Project
        {
            Components = new() { sink, reg },
            Connections = new() { new Connection { From = "REG.GND", To = "SENSOR.GND", Net = "ground" } },
        };
        ProjectValidator.Revalidate(p);
        var pwr = new Finding { Code = "PWR-NC", Refs = new() { "SENSOR" } };   // target SENSOR deterministically
        Assert.True(ProjectValidator.TryAutoFix(p, pwr));
        Assert.Contains(p.Connections, c => c.Net == "power" && (c.From == "REG.VOUT" || c.To == "REG.VOUT"));
    }

    // The exact shape of the ESP32 DevKit in Foundry's OWN generation system prompt
    // (ProjectGenerator.cs: "inputV":[3.0,5.5] with a single "3V3" power pin). The component-level range
    // legitimately spans 3.0–5.5 V because the BOARD accepts 5 V on VIN — but the only power pin declared
    // is the 3.3 V rail, and putting 5 V on it destroys the part. Before the pin-accurate check this
    // auto-fix wired USB.VBUS -> MCU.3V3 and the re-validation then reported "safe to power on".
    [Fact]
    public void ConnectRail_Power_RefusesFiveVoltSupplyOntoA3V3Pin()
    {
        var mcu = new ComponentSpec { Ref = "esp32", Alias = "MCU", Name = "ESP32 DevKit v1", LogicV = 3.3,
            InputVRange = new[] { 3.0, 5.5 },
            Pins = new() { new PinSpec { Name = "3V3", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var usb = new ComponentSpec { Ref = "usb", Alias = "USB", Name = "USB 5V", OutputV = 5.0,
            Pins = new() { new PinSpec { Name = "VBUS", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var p = new Project
        {
            Components = new() { mcu, usb },
            Connections = new() { new Connection { From = "USB.GND", To = "MCU.GND", Net = "ground" } },
        };
        ProjectValidator.Revalidate(p);

        Assert.False(ProjectValidator.TryAutoFix(p, new Finding { Code = "PWR-NC", Refs = new() { "MCU" } }));
        Assert.DoesNotContain(p.Connections, c => c.Net == "power");

        // And the refusal must not be laundered into a pass: the rail is still unconnected.
        ProjectValidator.Revalidate(p);
        Assert.NotEqual("pass", p.Validation);
    }

    // Same board, but a real 3.3 V supply is present — the fix must still work.
    [Fact]
    public void ConnectRail_Power_AcceptsMatchingRailOnANamedPin()
    {
        var mcu = new ComponentSpec { Ref = "esp32", Alias = "MCU", Name = "ESP32 DevKit v1", LogicV = 3.3,
            InputVRange = new[] { 3.0, 5.5 },
            Pins = new() { new PinSpec { Name = "3V3", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var reg = new ComponentSpec { Ref = "reg", Alias = "REG", Name = "AMS1117-3.3", OutputV = 3.3,
            Pins = new() { new PinSpec { Name = "VOUT", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var p = new Project
        {
            Components = new() { mcu, reg },
            Connections = new() { new Connection { From = "REG.GND", To = "MCU.GND", Net = "ground" } },
        };
        ProjectValidator.Revalidate(p);

        Assert.True(ProjectValidator.TryAutoFix(p, new Finding { Code = "PWR-NC", Refs = new() { "MCU" } }));
        Assert.Contains(p.Connections, c => c.Net == "power" && c.From == "REG.VOUT" && c.To == "MCU.3V3");
    }

    // A 5 V supply IS correct when the board exposes a pin that accepts it.
    [Fact]
    public void ConnectRail_Power_PrefersThePinThatAcceptsTheSupply()
    {
        var mcu = new ComponentSpec { Ref = "esp32", Alias = "MCU", Name = "ESP32 DevKit v1", LogicV = 3.3,
            InputVRange = new[] { 3.0, 5.5 },
            Pins = new()
            {
                new PinSpec { Name = "3V3", Kind = PinKind.Power },
                new PinSpec { Name = "VIN", Kind = PinKind.Power },
                new PinSpec { Name = "GND", Kind = PinKind.Ground },
            } };
        var usb = new ComponentSpec { Ref = "usb", Alias = "USB", Name = "USB 5V", OutputV = 5.0,
            Pins = new() { new PinSpec { Name = "VBUS", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } };
        var p = new Project
        {
            Components = new() { mcu, usb },
            Connections = new() { new Connection { From = "USB.GND", To = "MCU.GND", Net = "ground" } },
        };
        ProjectValidator.Revalidate(p);

        Assert.True(ProjectValidator.TryAutoFix(p, new Finding { Code = "PWR-NC", Refs = new() { "MCU" } }));
        Assert.Contains(p.Connections, c => c.Net == "power" && c.To == "MCU.VIN");   // never MCU.3V3
        Assert.DoesNotContain(p.Connections, c => c.Net == "power" && c.To == "MCU.3V3");
    }

    [Theory]
    [InlineData("3V3", 3.3)]
    [InlineData("3.3V", 3.3)]
    [InlineData("+5V", 5.0)]
    [InlineData("5V", 5.0)]
    [InlineData("1V8", 1.8)]
    [InlineData("12V", 12.0)]
    [InlineData("VBUS", 5.0)]
    public void RailVoltageOf_ParsesDeclaredRails(string pin, double expected) =>
        Assert.Equal(expected, ProjectValidator.RailVoltageOf(pin)!.Value, 3);

    [Theory]
    [InlineData("VIN")]
    [InlineData("VCC")]
    [InlineData("VDD")]
    [InlineData("VBAT")]
    [InlineData("")]
    public void RailVoltageOf_TreatsGenericSupplyPinsAsUnconstrained(string pin) =>
        Assert.Null(ProjectValidator.RailVoltageOf(pin));

    [Fact]
    public void ConnectRail_AddsMissingGround()
    {
        var p = MakeProject();
        p.Connections.RemoveAll(c => c.Net == "ground");      // strip ground
        ProjectValidator.Revalidate(p);
        var gnd = p.Findings.FirstOrDefault(f => f.Code == "GND-NC");
        Assert.NotNull(gnd);

        Assert.True(ProjectValidator.TryAutoFix(p, gnd!));
        Assert.Contains(p.Connections, c => c.Net == "ground");
    }
}
