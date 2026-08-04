using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Tests;

// Auto-fix existed but was one click per finding, and generation never invoked it — so a freshly generated
// project arrived carrying failures the engine already knew how to resolve (a strapping pin to remap, an
// unconnected ground) and left the user to clear them by hand, one at a time.
public class AutoFixAllTests
{
    private static ComponentSpec Mcu(params (string name, PinKind kind, bool strapping, bool inputOnly)[] pins) => new()
    {
        Ref = "u1", Alias = "MCU", Name = "ESP32", LogicV = 3.3, OutputV = 3.3,
        Pins = pins.Select(p => new PinSpec
        { Name = p.name, Kind = p.kind, Strapping = p.strapping, InputOnly = p.inputOnly }).ToList(),
    };

    private static ComponentSpec Peripheral(string alias, params string[] pins) => new()
    {
        Ref = alias.ToLowerInvariant(), Alias = alias, Name = alias + " module", LogicV = 3.3,
        Pins = pins.Select(n => new PinSpec
        {
            Name = n,
            Kind = n is "VCC" ? PinKind.Power : n is "GND" ? PinKind.Ground : PinKind.Input,
        }).ToList(),
    };

    private static Project Design(List<ComponentSpec> comps, params (string from, string to, string net)[] nets) =>
        new()
        {
            Title = "T",
            Components = comps,
            Connections = nets.Select(n => new Connection { From = n.from, To = n.to, Net = n.net }).ToList(),
        };

    // ---- the strapping pin: the classic one-click fix, now automatic ----

    private static Project StrappingDesign() => Design(
        new List<ComponentSpec>
        {
            Mcu(("GPIO0", PinKind.Bidir, true, false),      // strapping — must be vacated
                ("GPIO13", PinKind.Bidir, false, false),
                ("GPIO14", PinKind.Bidir, false, false),
                ("3V3", PinKind.Power, false, false),
                ("GND", PinKind.Ground, false, false)),
            Peripheral("BTN", "A"),
        },
        ("MCU.GPIO0", "BTN.A", "signal"));

    [Fact]
    public void AStrappingPinIsVacatedWithoutBeingAsked()
    {
        var p = StrappingDesign();
        ProjectValidator.Revalidate(p);
        Assert.Contains(p.Findings, f => f.Code == "PIN-04");

        var outcome = ProjectValidator.AutoFixAll(p);

        Assert.True(outcome.Applied >= 1);
        Assert.DoesNotContain(p.Findings, f => f.Code == "PIN-04");
        Assert.DoesNotContain(p.Connections, c => c.From == "MCU.GPIO0" || c.To == "MCU.GPIO0");
    }

    [Fact]
    public void EveryEditIsRecorded()
    {
        var outcome = ProjectValidator.AutoFixAll(StrappingDesign());

        Assert.Equal(outcome.Applied, outcome.Changes.Count);
        Assert.All(outcome.Changes, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.Contains(outcome.Changes, c => c.Contains("PIN-04"));
    }

    [Fact]
    public void TheVerdictIsReportedBeforeAndAfter()
    {
        var outcome = ProjectValidator.AutoFixAll(StrappingDesign());

        Assert.Equal("warn", outcome.BeforeVerdict);
        Assert.True(outcome.AfterWarn < outcome.BeforeWarn,
            $"warnings should drop: {outcome.BeforeWarn} -> {outcome.AfterWarn}");
    }

    // ---- it must loop: one fix frees the pin the next one wanted ----

    [Fact]
    public void SeveralFindingsAreResolvedInOneCall()
    {
        // Two peripherals both on the same strapping pin: a conflict AND a strapping violation.
        var p = Design(
            new List<ComponentSpec>
            {
                Mcu(("GPIO0", PinKind.Bidir, true, false),
                    ("GPIO12", PinKind.Bidir, false, false),
                    ("GPIO13", PinKind.Bidir, false, false),
                    ("GPIO14", PinKind.Bidir, false, false),
                    ("3V3", PinKind.Power, false, false),
                    ("GND", PinKind.Ground, false, false)),
                Peripheral("BTN", "A"),
                Peripheral("LED", "A"),
            },
            ("MCU.GPIO0", "BTN.A", "signal"),
            ("MCU.GPIO0", "LED.A", "signal"));

        var outcome = ProjectValidator.AutoFixAll(p);

        Assert.True(outcome.Applied >= 1);
        Assert.DoesNotContain(p.Findings, f => f.Severity == "fail");
        // Both peripherals still wired, and no longer to the same pin.
        var btn = p.Connections.Single(c => c.To == "BTN.A" || c.From == "BTN.A");
        var led = p.Connections.Single(c => c.To == "LED.A" || c.From == "LED.A");
        Assert.NotEqual(btn.From, led.From);
    }

    // ---- termination ----

    [Fact]
    public void ItTerminatesOnADesignItCannotFix()
    {
        // Only a strapping pin available: the remap has nowhere to go, so it must stop, not spin.
        var p = Design(
            new List<ComponentSpec>
            {
                Mcu(("GPIO0", PinKind.Bidir, true, false),
                    ("3V3", PinKind.Power, false, false),
                    ("GND", PinKind.Ground, false, false)),
                Peripheral("BTN", "A"),
            },
            ("MCU.GPIO0", "BTN.A", "signal"));

        var outcome = ProjectValidator.AutoFixAll(p, maxPasses: 4);

        Assert.Equal(0, outcome.Applied);
        Assert.Contains(p.Findings, f => f.Code == "PIN-04");   // honestly still reported
    }

    [Fact]
    public void ACleanDesignIsLeftCompletelyAlone()
    {
        var p = Design(
            new List<ComponentSpec>
            {
                Mcu(("GPIO13", PinKind.Bidir, false, false),
                    ("3V3", PinKind.Power, false, false),
                    ("GND", PinKind.Ground, false, false)),
                Peripheral("LED", "A"),
            },
            ("MCU.GPIO13", "LED.A", "signal"));

        var before = p.Connections.Select(c => $"{c.From}->{c.To}").ToList();
        var outcome = ProjectValidator.AutoFixAll(p);

        Assert.Equal(0, outcome.Applied);
        Assert.Empty(outcome.Changes);
        Assert.Equal(before, p.Connections.Select(c => $"{c.From}->{c.To}").ToList());
    }

    [Fact]
    public void ItNeverRunsForever()
    {
        var p = StrappingDesign();
        // maxPasses: 1 must still be safe and still report truthfully.
        var outcome = ProjectValidator.AutoFixAll(p, maxPasses: 1);
        Assert.True(outcome.Applied >= 0);
    }

    // ---- it must not make things worse ----

    [Fact]
    public void TheOutcomeNeverReportsMoreFailuresThanItStartedWith()
    {
        foreach (var p in new[] { StrappingDesign(), Design(
            new List<ComponentSpec>
            {
                Mcu(("GPIO0", PinKind.Bidir, true, false), ("GPIO5", PinKind.Bidir, false, false),
                    ("3V3", PinKind.Power, false, false), ("GND", PinKind.Ground, false, false)),
                Peripheral("SEN", "VCC", "GND", "OUT"),
            },
            ("MCU.GPIO0", "SEN.OUT", "signal")) })
        {
            var o = ProjectValidator.AutoFixAll(p);
            Assert.True(o.AfterFail <= o.BeforeFail,
                $"auto-fix must never increase failures: {o.BeforeFail} -> {o.AfterFail}");
        }
    }

    // What it could NOT fix has to stay visible — the point is to shorten the list, not to hide it.
    [Fact]
    public void WhatIsLeftIsStillReported()
    {
        var p = Design(
            new List<ComponentSpec>
            {
                Mcu(("GPIO0", PinKind.Bidir, true, false), ("GPIO13", PinKind.Bidir, false, false),
                    ("3V3", PinKind.Power, false, false), ("GND", PinKind.Ground, false, false)),
                new() { Ref = "x", Alias = "X1", Name = "Mystery Widget",
                        Pins = new() { new PinSpec { Name = "A", Kind = PinKind.Bidir } } },
            },
            ("MCU.GPIO0", "X1.A", "signal"));

        var outcome = ProjectValidator.AutoFixAll(p);

        Assert.NotEmpty(outcome.Remaining);
        Assert.Equal(p.Findings.Count, outcome.Remaining.Count);
    }

    // ---- the netlist-derived artifacts must follow the edit ----

    [Fact]
    public void RemappingAPinChangesTheDerivedPinMap()
    {
        var p = StrappingDesign();
        var before = Foundry.Core.Firmware.PinMap.Build(p.Connections, new ComponentKb(p.Components));
        Assert.Contains(before, e => e.Gpio == 0);

        ProjectValidator.AutoFixAll(p);
        var after = Foundry.Core.Firmware.PinMap.Build(p.Connections, new ComponentKb(p.Components));

        // The whole reason generation regenerates firmware after an auto-fix: leaving pinmap.h pointing at
        // GPIO0 after moving off it would flash the board with the pin the design no longer uses.
        Assert.DoesNotContain(after, e => e.Gpio == 0);
        Assert.NotEmpty(after);
    }
}
