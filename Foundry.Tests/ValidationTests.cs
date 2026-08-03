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
    public void MissingI2cPullups_AreFlagged_AndPresentOnesArent()
    {
        var mcu = new ComponentSpec { Ref = "esp32", Alias = "MCU", Name = "ESP32", LogicV = 3.3,
            Pins = new() { new PinSpec { Name = "GPIO21", Kind = PinKind.Bidir }, new PinSpec { Name = "GPIO22", Kind = PinKind.Bidir } } };
        var dev = new ComponentSpec { Ref = "bme", Alias = "DEV", Name = "BME280", LogicV = 3.3,
            Pins = new() { new PinSpec { Name = "SDA", Kind = PinKind.Bidir }, new PinSpec { Name = "SCL", Kind = PinKind.Bidir } } };
        var res = new ComponentSpec { Ref = "r", Alias = "RPU", Name = "4.7kΩ Resistor",
            Pins = new() { new PinSpec { Name = "1", Kind = PinKind.Bidir }, new PinSpec { Name = "2", Kind = PinKind.Bidir } } };

        var i2c = new List<Connection>
        {
            new() { From = "MCU.GPIO21", To = "DEV.SDA", Net = "i2c" },
            new() { From = "MCU.GPIO22", To = "DEV.SCL", Net = "i2c" },
        };

        var without = RulesEngine.Validate(i2c, new ComponentKb(new[] { mcu, dev }));
        Assert.Contains(without, f => f.Code == "PULL-I2C");

        var withPull = new List<Connection>(i2c)
        {
            new() { From = "RPU.1", To = "DEV.SDA", Net = "i2c" },
            new() { From = "RPU.2", To = "DEV.SCL", Net = "i2c" },
        };
        var with = RulesEngine.Validate(withPull, new ComponentKb(new[] { mcu, dev, res }));
        Assert.DoesNotContain(with, f => f.Code == "PULL-I2C");
    }

    [Fact]
    public void BareLedWithoutResistor_IsFlagged()
    {
        var mcu = new ComponentSpec { Ref = "uno", Alias = "MCU", Name = "Arduino Uno", LogicV = 5.0,
            Pins = new() { new PinSpec { Name = "D13", Kind = PinKind.Output } } };
        var led = new ComponentSpec { Ref = "led", Alias = "LED1", Name = "5mm Red LED",
            Pins = new() { new PinSpec { Name = "A", Kind = PinKind.Input }, new PinSpec { Name = "K", Kind = PinKind.Ground } } };

        var direct = new List<Connection> { new() { From = "MCU.D13", To = "LED1.A", Net = "signal" } };
        Assert.Contains(RulesEngine.Validate(direct, new ComponentKb(new[] { mcu, led })), f => f.Code == "LED-R");

        var res = new ComponentSpec { Ref = "r", Alias = "R1", Name = "330Ω Resistor",
            Pins = new() { new PinSpec { Name = "1", Kind = PinKind.Bidir }, new PinSpec { Name = "2", Kind = PinKind.Bidir } } };
        var withR = new List<Connection>
        {
            new() { From = "MCU.D13", To = "R1.1", Net = "signal" },
            new() { From = "R1.2", To = "LED1.A", Net = "signal" },
        };
        Assert.DoesNotContain(RulesEngine.Validate(withR, new ComponentKb(new[] { mcu, led, res })), f => f.Code == "LED-R");
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
    public void SupplyVoltage_SourceIdentifiedByOutputV_NotEndpointOrder()
    {
        // The 5V regulator is the To side and the 3.3V-only sink is the From side — the rule must still treat
        // the part with OutputV as the SOURCE (5V) and flag it against the sink's 3.0–3.6V range.
        var reg = new ComponentSpec { Ref = "reg", Alias = "REG", Name = "5V Reg", OutputV = 5.0,
            Pins = new() { new PinSpec { Name = "VOUT", Kind = PinKind.Power } } };
        var sink = new ComponentSpec { Ref = "s", Alias = "DEV", Name = "3V3 Dev", InputVRange = new[] { 3.0, 3.6 },
            Pins = new() { new PinSpec { Name = "VCC", Kind = PinKind.Power } } };
        var conns = new List<Connection> { new() { From = "DEV.VCC", To = "REG.VOUT", Net = "power" } };
        Assert.Contains(RulesEngine.Validate(conns, new ComponentKb(new[] { reg, sink })),
            x => x.Code == "VLT-SUP" && x.Severity == "fail");
    }

    [Fact]
    public void PinConflict_SharedI2cBus_IsNotFlagged()
    {
        // The MCU's SDA/SCL fan out to multiple devices on one shared bus — that is BY DESIGN, not a pin
        // conflict. The old rule counted i2c endpoints and false-failed every multi-device I²C design.
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "mcu", Alias = "MCU", Name = "ESP32", LogicV = 3.3,
                Pins = new() { new PinSpec { Name = "GPIO21", Kind = PinKind.Bidir }, new PinSpec { Name = "GPIO22", Kind = PinKind.Bidir } } },
            new ComponentSpec { Ref = "d1", Alias = "D1", Name = "BME280", LogicV = 3.3, I2cAddress = 0x76,
                Pins = new() { new PinSpec { Name = "SDA", Kind = PinKind.Bidir }, new PinSpec { Name = "SCL", Kind = PinKind.Bidir } } },
            new ComponentSpec { Ref = "d2", Alias = "D2", Name = "OLED", LogicV = 3.3, I2cAddress = 0x3C,
                Pins = new() { new PinSpec { Name = "SDA", Kind = PinKind.Bidir }, new PinSpec { Name = "SCL", Kind = PinKind.Bidir } } },
        });
        var connections = new List<Connection>
        {
            new() { From = "MCU.GPIO21", To = "D1.SDA", Net = "i2c" },
            new() { From = "MCU.GPIO22", To = "D1.SCL", Net = "i2c" },
            new() { From = "MCU.GPIO21", To = "D2.SDA", Net = "i2c" },   // same bus pin, second device
            new() { From = "MCU.GPIO22", To = "D2.SCL", Net = "i2c" },
        };
        Assert.DoesNotContain(RulesEngine.Validate(connections, kb), f => f.Code == "PIN-CONF");
    }

    [Fact]
    public void VoltageLevel_LowDriverIntoHighInput_IsSafe_NotFlagged()
    {
        // 3.3V MCU output → 5V device input is normally fine; the direction-blind rule used to false-fail it.
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "mcu", Alias = "MCU", Name = "ESP32", LogicV = 3.3,
                Pins = new() { new PinSpec { Name = "GPIO5", Kind = PinKind.Output } } },
            new ComponentSpec { Ref = "dev", Alias = "DEV", Name = "5V Module", LogicV = 5.0,
                Pins = new() { new PinSpec { Name = "IN", Kind = PinKind.Input } } },
        });
        var connections = new List<Connection> { new() { From = "MCU.GPIO5", To = "DEV.IN", Net = "signal" } };
        Assert.DoesNotContain(RulesEngine.Validate(connections, kb), f => f.Code == "VLT-LVL");
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

    // ---- grounding: stop grading the model with the model's own answer key ----
    //
    // Every other rule reasons over ComponentSpec.Pins, which ProjectGenerator builds from the model's
    // JSON reply. So a hallucinated pin passed every check, reached pinmap.h and got flashed to real
    // hardware; the PCB build was the only thing that ever refused it. PartResolver applies the same
    // authority that build refuses on.

    private static ComponentSpec Esp32(params PinSpec[] pins) => new()
    {
        Ref = "esp32", Alias = "MCU", Name = "ESP32-WROOM-32", LogicV = 3.3,
        Pins = pins.ToList(),
    };

    [Fact]
    public void AWiredPinThatTheRealPartDoesNotHave_Fails()
    {
        // GPIO99 does not exist on an ESP32-WROOM-32; the curated map is the authority.
        var kb = new ComponentKb(new[]
        {
            Esp32(new PinSpec { Name = "GPIO99", Kind = PinKind.Bidir },
                  new PinSpec { Name = "GND", Kind = PinKind.Ground }),
            new ComponentSpec { Ref = "s", Alias = "SEN", Name = "Sensor",
                Pins = new() { new PinSpec { Name = "OUT", Kind = PinKind.Output } } },
        });
        var findings = RulesEngine.Validate(
            new List<Connection> { new() { From = "MCU.GPIO99", To = "SEN.OUT", Net = "signal" } }, kb);

        Assert.Contains(findings, f => f is { Code: "PIN-UNK", Severity: "fail" });
    }

    // A part may legitimately DECLARE pins the resolved footprint lacks. Only wiring one can do harm.
    [Fact]
    public void AnUnwiredPinThatTheRealPartLacks_DoesNotFail()
    {
        var kb = new ComponentKb(new[]
        {
            Esp32(new PinSpec { Name = "GPIO4", Kind = PinKind.Bidir },
                  new PinSpec { Name = "GPIO99", Kind = PinKind.Bidir },   // declared, never wired
                  new PinSpec { Name = "GND", Kind = PinKind.Ground }),
            new ComponentSpec { Ref = "s", Alias = "SEN", Name = "Sensor",
                Pins = new() { new PinSpec { Name = "OUT", Kind = PinKind.Output } } },
        });
        var findings = RulesEngine.Validate(
            new List<Connection> { new() { From = "MCU.GPIO4", To = "SEN.OUT", Net = "signal" } }, kb);

        Assert.DoesNotContain(findings, f => f.Code == "PIN-UNK");
    }

    [Fact]
    public void RealPinsOnAKnownPart_ProduceNoGroundingFailure()
    {
        var kb = new ComponentKb(new[]
        {
            Esp32(new PinSpec { Name = "GPIO4", Kind = PinKind.Bidir },
                  new PinSpec { Name = "GND", Kind = PinKind.Ground }),
            new ComponentSpec { Ref = "s", Alias = "SEN", Name = "Sensor",
                Pins = new() { new PinSpec { Name = "OUT", Kind = PinKind.Output } } },
        });
        var findings = RulesEngine.Validate(
            new List<Connection> { new() { From = "MCU.GPIO4", To = "SEN.OUT", Net = "signal" } }, kb);

        Assert.DoesNotContain(findings, f => f.Code == "PIN-UNK");
    }

    // Absence of evidence is not evidence: a part with no authority is UNPROVEN, never failed.
    [Fact]
    public void APartWithNoAuthoritativePinout_IsUnprovenNotFailed()
    {
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "x", Alias = "X1", Name = "Mystery Widget 9000",
                Pins = new() { new PinSpec { Name = "WHATEVER", Kind = PinKind.Bidir } } },
            new ComponentSpec { Ref = "s", Alias = "SEN", Name = "Sensor",
                Pins = new() { new PinSpec { Name = "OUT", Kind = PinKind.Output } } },
        });
        var findings = RulesEngine.Validate(
            new List<Connection> { new() { From = "X1.WHATEVER", To = "SEN.OUT", Net = "signal" } }, kb);

        Assert.DoesNotContain(findings, f => f.Code == "PIN-UNK");
        Assert.Contains(findings, f => f is { Code: "PIN-UNVERIFIED", Severity: "unproven" });
    }

    [Fact]
    public void UngroundedParts_KeepTheProjectOffAPass()
    {
        var p = new Project
        {
            Components = new()
            {
                new ComponentSpec { Ref = "x", Alias = "X1", Name = "Mystery Widget 9000",
                    Pins = new() { new PinSpec { Name = "A", Kind = PinKind.Bidir } } },
            },
            Connections = new() { new Connection { From = "X1.A", To = "X1.A", Net = "signal" } },
        };
        ProjectValidator.Revalidate(p);
        Assert.NotEqual("pass", p.Validation);
    }

    // ---- referential integrity: the engine must not report health for what it never checked ----

    // Every rule resolves parts through kb.ByAlias(..) and skips a miss, so a netlist naming a part the
    // design never declared previously produced ZERO findings — a clean bill of health over an
    // unvalidatable design.
    [Fact]
    public void UndeclaredPart_InANet_Fails()
    {
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "m", Alias = "MCU", Name = "MCU", LogicV = 3.3,
                Pins = new() { new PinSpec { Name = "GPIO4", Kind = PinKind.Bidir }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } },
        });
        var connections = new List<Connection>
        {
            new() { From = "MCU.GPIO4", To = "GHOST.OUT", Net = "signal" },   // GHOST is never declared
        };

        var findings = RulesEngine.Validate(connections, kb);
        Assert.Contains(findings, f => f is { Code: "NET-REF", Severity: "fail" } && f.Refs.Contains("GHOST"));
    }

    [Fact]
    public void InventedPin_OnADeclaredPart_Fails()
    {
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "m", Alias = "MCU", Name = "MCU", LogicV = 3.3,
                Pins = new() { new PinSpec { Name = "GPIO4", Kind = PinKind.Bidir }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } },
            new ComponentSpec { Ref = "s", Alias = "SEN", Name = "Sensor", LogicV = 3.3,
                Pins = new() { new PinSpec { Name = "OUT", Kind = PinKind.Output }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } },
        });
        var connections = new List<Connection>
        {
            new() { From = "MCU.GPIO99", To = "SEN.OUT", Net = "signal" },   // GPIO99 does not exist
        };

        var findings = RulesEngine.Validate(connections, kb);
        Assert.Contains(findings, f => f is { Code: "NET-PIN", Severity: "fail" } && f.Refs.Contains("MCU.GPIO99"));
    }

    // A part with no pin table makes no claims to contradict (common for passives) — not an error.
    [Fact]
    public void PartWithNoPinTable_DoesNotFailReferentialCheck()
    {
        var kb = new ComponentKb(new[]
        {
            new ComponentSpec { Ref = "m", Alias = "MCU", Name = "MCU", LogicV = 3.3,
                Pins = new() { new PinSpec { Name = "GPIO4", Kind = PinKind.Bidir } } },
            new ComponentSpec { Ref = "r", Alias = "R1", Name = "220R resistor" },   // no Pins
        });
        var connections = new List<Connection>
        {
            new() { From = "MCU.GPIO4", To = "R1.1", Net = "signal" },
        };

        var findings = RulesEngine.Validate(connections, kb);
        Assert.DoesNotContain(findings, f => f.Code is "NET-REF" or "NET-PIN");
    }

    // The rollup is what the report card renders; an unresolvable netlist must never reach "pass".
    [Fact]
    public void UnresolvableNetlist_CannotRollUpToPass()
    {
        var p = new Project
        {
            Components = new()
            {
                new ComponentSpec { Ref = "m", Alias = "MCU", Name = "MCU", LogicV = 3.3,
                    Pins = new() { new PinSpec { Name = "GPIO4", Kind = PinKind.Bidir }, new PinSpec { Name = "GND", Kind = PinKind.Ground } } },
            },
            Connections = new() { new Connection { From = "MCU.GPIO4", To = "GHOST.OUT", Net = "signal" } },
        };

        ProjectValidator.Revalidate(p);
        Assert.Equal("fail", p.Validation);
    }

    [Fact]
    public void DemoProject_StillHasNoReferentialFailures()
    {
        var demo = Foundry.Core.Project.DemoData.CreateSoilMoistureProject();
        var findings = RulesEngine.Validate(demo.Connections, new ComponentKb(demo.Components));
        Assert.DoesNotContain(findings, f => f.Code is "NET-REF" or "NET-PIN");
    }
}
