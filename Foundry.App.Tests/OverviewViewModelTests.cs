using Foundry.App.ViewModels;
using System.Collections.Generic;
using System.Linq;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.App.Tests;

// Six strings from the original design comp still shipped in OverviewView.xaml, sitting directly beneath
// genuinely bound values so they read as computed. Two were counts ("4 subsystems · 9 nets", "2 warn · 1 info
// · 2 pass"), one was a cost saving attributed to a part swap no project had made ("–$1.85 (gland swap)"),
// one was a goal that is not stored anywhere ("Below 60-day goal"). These pin the ones that became real.
public class OverviewViewModelTests
{
    private static Project Proj(params Finding[] findings) => new()
    {
        Title = "T",
        Subsystems = new List<Subsystem>
        {
            new() { Id = "s1", Role = "Controller", Name = "ESP32" },
            new() { Id = "s2", Role = "Sensor", Name = "Soil" },
        },
        Connections = new List<Connection>
        {
            new() { From = "MCU.3V3", To = "SEN.VCC", Net = "power" },
            new() { From = "MCU.GND", To = "SEN.GND", Net = "ground" },
            new() { From = "MCU.GPIO34", To = "SEN.AOUT", Net = "signal" },
            new() { From = "MCU.GPIO4", To = "SEN.INT", Net = "signal" },   // same net class, not a new net
        },
        Findings = findings.ToList(),
        Validation = ProjectValidator.Rollup(findings),
    };

    private static Finding F(string severity) =>
        new() { Severity = severity, Code = "X", Title = "t", Description = "d" };

    [Fact]
    public void ArchitectureCounts_AreComputed_NotTheCompsLiteral()
    {
        var vm = new OverviewViewModel(Proj());
        Assert.Equal("2 subsystems · 3 nets", vm.ArchitectureCountText);
        Assert.DoesNotContain("9 nets", vm.ArchitectureCountText);
    }

    [Fact]
    public void ASingleSubsystemIsNotPluralised() =>
        Assert.StartsWith("1 subsystem ·", new OverviewViewModel(new Project
        {
            Subsystems = new List<Subsystem> { new() { Id = "s", Name = "n" } },
            Connections = new List<Connection> { new() { From = "A.1", To = "B.1", Net = "signal" } },
        }).ArchitectureCountText);

    [Fact]
    public void FindingCounts_AreComputed_AndOmitEmptyBuckets()
    {
        var vm = new OverviewViewModel(Proj(F("warn"), F("warn"), F("pass")));
        Assert.Equal("2 warn · 1 pass", vm.FindingsCountText);   // no "fail"/"unproven" buckets printed as 0
    }

    // "info" was in the hardcoded string and is not a severity this engine produces.
    [Fact]
    public void TheCountsNeverInventASeverity()
    {
        var vm = new OverviewViewModel(Proj(F("fail"), F("unproven"), F("pass")));
        Assert.DoesNotContain("info", vm.FindingsCountText);
        Assert.Equal("1 fail · 1 unproven · 1 pass", vm.FindingsCountText);
    }

    // The chip was permanently warn-coloured. A fail must not render amber.
    [Fact]
    public void TheChipSeverityFollowsTheRealRollup()
    {
        Assert.Equal("fail", new OverviewViewModel(Proj(F("fail"), F("warn"))).FindingsSeverity);
        Assert.Equal("warn", new OverviewViewModel(Proj(F("warn"), F("pass"))).FindingsSeverity);
        Assert.Equal("unproven", new OverviewViewModel(Proj(F("unproven"), F("pass"))).FindingsSeverity);
    }

    [Fact]
    public void AnUnvalidatedProject_SaysSoRatherThanShowingZeroes() =>
        Assert.Equal("not validated", new OverviewViewModel(Proj()).FindingsCountText);

    // ---- the stock headline ----

    [Fact]
    public void WithNoLiveQuotes_TheSourcingHeadlineDoesNotClaimStock()
    {
        var p = Proj();
        p.Bom = new List<BomLine> { new() { Qty = 1, Name = "ESP32", Stock = 1442, Price = 8.5 } };
        var vm = new OverviewViewModel(p);

        Assert.False(vm.AllInStock);
        Assert.Equal("stock not checked", vm.StockText);
    }

    [Fact]
    public void WithLiveQuotes_ItReportsThem()
    {
        var p = Proj();
        p.Bom = new List<BomLine>
        {
            new() { Qty = 1, Name = "ESP32", Stock = 1442, Price = 8.5, PriceSource = "DigiKey" },
        };
        var vm = new OverviewViewModel(p);

        Assert.True(vm.AllInStock);
        Assert.Equal("All in stock", vm.StockText);
    }

    // ---- print time ----

    [Fact]
    public void PrintTime_HidesWhenTheGeneratorLeftItEmpty()
    {
        var vm = new OverviewViewModel(Proj());
        Assert.False(vm.HasPrintTime);
        Assert.Equal("", vm.PrintTimeText);
    }

    [Fact]
    public void PrintTime_ShowsTheProjectsOwnFigure()
    {
        var p = Proj();
        p.Enclosure.PrintTime = "3h 40m";
        var vm = new OverviewViewModel(p);

        Assert.True(vm.HasPrintTime);
        Assert.Equal("3h 40m", vm.PrintTimeText);
        Assert.DoesNotContain("0.2mm", vm.PrintTimeText);   // the comp's layer height; nothing computes it
    }
}

// Delete ran on a single click of a small "×", with no confirmation, next to the row that OPENS the
// project -- and it takes the .rev history with it, so there was nothing left to restore from. The
// revision cleanup landed; the dialog it needed never did.
public class ProjectDeleteConfirmationTests
{
    private static ProjectsViewModel Vm(out List<string> prompts)
    {
        var seen = new List<string>();
        prompts = seen;
        var vm = new ProjectsViewModel(onNew: () => { }, onOpen: _ => { });
        vm.Confirm = (title, message) => { seen.Add(message); return false; };
        return vm;
    }

    [Fact]
    public void DeletingAsksFirst()
    {
        var vm = Vm(out var prompts);
        vm.DeleteCommand.Execute("p_doesnotexist");
        Assert.Single(prompts);
    }

    [Fact]
    public void TheQuestionNamesWhatIsLost()
    {
        var vm = Vm(out var prompts);
        vm.DeleteCommand.Execute("p_doesnotexist");

        Assert.Contains("version history", prompts[0]);
        Assert.Contains("cannot be undone", prompts[0]);
    }

    [Fact]
    public void AnEmptyIdNeverEvenAsks()
    {
        var vm = Vm(out var prompts);
        vm.DeleteCommand.Execute(null);
        vm.DeleteCommand.Execute("");
        Assert.Empty(prompts);
    }

    // Saying no must be a real no: the row stays.
    [Fact]
    public void DecliningKeepsTheProject()
    {
        var vm = new ProjectsViewModel(onNew: () => { }, onOpen: _ => { });
        var before = vm.Recent.Count;
        vm.Confirm = (_, _) => false;

        vm.DeleteCommand.Execute(vm.Recent.FirstOrDefault()?.Id ?? "p_none");
        Assert.Equal(before, vm.Recent.Count);
    }
}
