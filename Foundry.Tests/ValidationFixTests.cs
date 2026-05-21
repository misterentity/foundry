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
