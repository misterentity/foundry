using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Tests;

public class ValidationTests
{
    private static ComponentKb Kb => ComponentKb.Demo();

    [Fact]
    public void Demo_Yields_TwoWarnings_NoFailures()
    {
        var findings = RulesEngine.Validate(DemoData.SoilMoistureConnections(), Kb, batteryGoalDays: 60);

        Assert.DoesNotContain(findings, f => f.Severity == "fail");
        Assert.Equal(2, findings.Count(f => f.Severity == "warn")); // strapping pin + battery duty
        Assert.Contains(findings, f => f.Code == "PIN-04");          // GPIO0 strapping
        Assert.Contains(findings, f => f.Code == "PWR-02");          // battery budget
        Assert.Contains(findings, f => f is { Code: "VLT-00", Severity: "pass" });
    }

    [Fact]
    public void InjectedFault_5VSensorOn3V3Pin_IsCaught()
    {
        // PRD §19 acceptance: a 5V sensor output driving the 3.3V-only MCU pin must be flagged.
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec
            {
                Ref = "mcu", Alias = "MCU", Name = "ESP32", LogicV = 3.3, CurrentMaActive = 80,
                Pins = new()
                {
                    new PinSpec { Name = "GPIO34", Kind = PinKind.Analog, InputOnly = true },
                    new PinSpec { Name = "GND", Kind = PinKind.Ground },
                },
            },
            new ComponentSpec
            {
                Ref = "sensor5v", Alias = "SENSOR", Name = "5V Sensor", LogicV = 5.0, CurrentMaActive = 5,
                Pins = new()
                {
                    new PinSpec { Name = "OUT", Kind = PinKind.Output },
                    new PinSpec { Name = "GND", Kind = PinKind.Ground },
                },
            },
        });

        var connections = new List<Connection>
        {
            new() { From = "SENSOR.OUT", To = "MCU.GPIO34", Net = "signal" },
            new() { From = "SENSOR.GND", To = "MCU.GND",    Net = "ground" },
        };

        var findings = RulesEngine.Validate(connections, kb);

        var fault = Assert.Single(findings, f => f.Code == "VLT-LVL");
        Assert.Equal("fail", fault.Severity);
        Assert.DoesNotContain(findings, f => f is { Code: "VLT-00" }); // no "all consistent" pass when a mismatch exists
    }

    [Fact]
    public void PinConflict_SamePinTwoNets_IsFail()
    {
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec
            {
                Ref = "mcu", Alias = "MCU", Name = "ESP32", LogicV = 3.3,
                Pins = new()
                {
                    new PinSpec { Name = "GPIO13", Kind = PinKind.Bidir },
                    new PinSpec { Name = "GND", Kind = PinKind.Ground },
                },
            },
            new ComponentSpec { Ref = "a", Alias = "A", Name = "A", LogicV = 3.3, Pins = new() { new PinSpec { Name = "S", Kind = PinKind.Output } } },
            new ComponentSpec { Ref = "b", Alias = "B", Name = "B", LogicV = 3.3, Pins = new() { new PinSpec { Name = "S", Kind = PinKind.Output } } },
        });

        var connections = new List<Connection>
        {
            new() { From = "A.S", To = "MCU.GPIO13", Net = "signal" },
            new() { From = "B.S", To = "MCU.GPIO13", Net = "signal" },
        };

        var findings = RulesEngine.Validate(connections, kb);
        Assert.Contains(findings, f => f is { Code: "PIN-CONF", Severity: "fail" });
    }

    [Fact]
    public void I2cCollision_DuplicateAddress_IsFail()
    {
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "x", Alias = "X", Name = "Sensor X", LogicV = 3.3, I2cAddress = 0x76,
                Pins = new() { new PinSpec { Name = "SDA", Kind = PinKind.Bidir }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } },
            new ComponentSpec { Ref = "y", Alias = "Y", Name = "Sensor Y", LogicV = 3.3, I2cAddress = 0x76,
                Pins = new() { new PinSpec { Name = "SDA", Kind = PinKind.Bidir }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } },
        });

        var connections = new List<Connection>
        {
            new() { From = "X.SDA", To = "Y.SDA", Net = "i2c" },
        };

        var findings = RulesEngine.Validate(connections, kb);
        Assert.Contains(findings, f => f is { Code: "I2C-DUP", Severity: "fail" });
    }

    [Fact]
    public void MissingGround_IsWarned()
    {
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "s", Alias = "S", Name = "Sensor", LogicV = 3.3,
                Pins = new() { new PinSpec { Name = "VCC", Kind = PinKind.Power }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } },
            new ComponentSpec { Ref = "p", Alias = "P", Name = "Supply", OutputV = 3.3,
                Pins = new() { new PinSpec { Name = "VOUT", Kind = PinKind.Output } } },
        });

        // power connected, ground never connected to S
        var connections = new List<Connection>
        {
            new() { From = "P.VOUT", To = "S.VCC", Net = "power" },
        };

        var findings = RulesEngine.Validate(connections, kb);
        Assert.Contains(findings, f => f is { Code: "GND-NC", Severity: "warn" });
    }
}
