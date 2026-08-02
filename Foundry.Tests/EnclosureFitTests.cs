using Foundry.Core.Cad;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Tests;

public class EnclosureFitTests
{
    private static Enclosure Box(double l, double w, double h) =>
        new() { Inner = new[] { l, w, h }, Wall = 2.0 };

    private static readonly BoardExtent Board = new(60.0, 40.0);

    // A realistic mix: flat SMD parts plus one tall through-hole electrolytic.
    private static readonly PartHeight[] Parts =
    {
        new("Resistor_SMD:R_0805_2012Metric", 0.45, 0.0),
        new("Capacitor_SMD:C_0603_1608Metric", 0.80, 0.0),
        new("RF_Module:ESP32-WROOM-32", 3.10, 0.0),
        new("Capacitor_THT:CP_Radial_D5.0mm_P2.50mm", 5.00, 2.00),
    };

    [Fact]
    public void MinimumInner_IsDerivedFromTheBoardAndTheTallestPart()
    {
        var min = EnclosureFit.MinimumInner(Board, Parts);

        Assert.Equal(62.0, min[0], 2);                       // 60 + 2×1 mm side clearance
        Assert.Equal(42.0, min[1], 2);
        // standoff max(3, 2 below-board) + 1.6 PCB + 5.0 tallest + 1.0 lid clearance
        Assert.Equal(3.0 + 1.6 + 5.0 + 1.0, min[2], 2);
    }

    [Fact]
    public void AnEnclosureSizedFromTheBoardPasses()
    {
        var min = EnclosureFit.MinimumInner(Board, Parts);
        var findings = EnclosureFit.Check(Box(min[0], min[1], min[2]), Board, Parts);

        Assert.DoesNotContain(findings, f => f.Severity == "fail");
        Assert.Equal("pass", ProjectValidator.Rollup(findings));
    }

    // The shipped default before this check existed: a guessed cavity that the board does not fit in.
    [Fact]
    public void ABoardLargerThanTheCavityFails()
    {
        var findings = EnclosureFit.Check(Box(40, 30, 20), Board, Parts);

        var fit = Assert.Single(findings, f => f.Code == "FIT-XY");
        Assert.Equal("fail", fit.Severity);
        Assert.Contains("60", fit.Description);
        Assert.Equal("fail", ProjectValidator.Rollup(findings));
    }

    [Fact]
    public void ATallPartThatCannotClearTheLidFails()
    {
        // Cavity is wide enough but only 6 mm deep; the 5 mm electrolytic plus stack-up needs ~10.6 mm.
        var findings = EnclosureFit.Check(Box(70, 50, 6), Board, Parts);

        var z = Assert.Single(findings, f => f.Code == "FIT-Z");
        Assert.Equal("fail", z.Severity);
        Assert.Contains("CP_Radial", z.Title);
    }

    [Fact]
    public void PartsProtrudingBelowTheBoardRaiseTheStandoffRequirement()
    {
        var deep = new[] { new PartHeight("Connector_PinHeader_2.54mm:PinHeader_1x04_P2.54mm_Vertical", 8.65, 3.11) };
        var min = EnclosureFit.MinimumInner(Board, deep);

        Assert.Equal(3.11 + 1.6 + 8.65 + 1.0, min[2], 2);   // standoff follows the pin tails, not the 3 mm default

        var findings = EnclosureFit.Check(Box(min[0], min[1], min[2]), Board, deep);
        var under = Assert.Single(findings, f => f.Code == "FIT-UNDER");
        Assert.Equal("warn", under.Severity);
    }

    // The honest-refusal path: an unmeasurable part must not be silently treated as flat.
    [Fact]
    public void UnknownPartHeightsAreReportedUnproven_AndNeverRollUpToPass()
    {
        var parts = Parts.Append(PartHeight.Unknown("Custom:MysteryModule")).ToArray();
        var min = EnclosureFit.MinimumInner(Board, Parts);
        var findings = EnclosureFit.Check(Box(min[0], min[1], min[2]), Board, parts);

        var unk = Assert.Single(findings, f => f.Code == "FIT-UNK");
        Assert.Equal("unproven", unk.Severity);
        Assert.Contains("MysteryModule", unk.Description);

        Assert.DoesNotContain(findings, f => f.Severity == "fail");
        Assert.Equal("unproven", ProjectValidator.Rollup(findings));   // NOT "pass"
    }

    [Fact]
    public void AnEnclosureWithNoDimensionsIsAFailure()
    {
        var findings = EnclosureFit.Check(new Enclosure { Inner = Array.Empty<double>() }, Board, Parts);
        Assert.Single(findings, f => f is { Code: "FIT-DIM", Severity: "fail" });
    }

    [Fact]
    public void NoHeightDataAtAll_StillSizesTheFloorFromTheBoard()
    {
        var min = EnclosureFit.MinimumInner(Board, Array.Empty<PartHeight>());
        Assert.Equal(62.0, min[0], 2);
        Assert.Equal(42.0, min[1], 2);
    }

    // ---- the rollup contract itself ----

    [Theory]
    [InlineData("fail", "fail")]
    [InlineData("warn", "warn")]
    [InlineData("unproven", "unproven")]
    [InlineData("info", "pass")]
    public void Rollup_NeverLetsAnUnprovenCheckReadAsPass(string severity, string expected) =>
        Assert.Equal(expected, ProjectValidator.Rollup(new[] { new Finding { Severity = severity } }));

    // End-to-end over the shipped demo: every part resolves through the SAME footprint decision the PCB
    // build makes, so the case is measured against the parts that will actually be placed.
    [Fact]
    public void HeightsFor_ResolvesTheDemoProjectThroughFootprintMap()
    {
        var demo = DemoData.CreateSoilMoistureProject();
        var kicad = Foundry.Core.Pcb.KiCadInstaller.Locate();
        var modelDir = kicad is null ? null : StepHeights.ModelDirFor(kicad.FootprintDir);

        var heights = EnclosureFit.HeightsFor(demo, modelDir);

        Assert.NotEmpty(heights);
        Assert.Equal(heights.Select(h => h.LibId).Distinct().Count(), heights.Count);   // deduped by footprint
        if (modelDir is not null)
            Assert.Contains(heights, h => h.IsKnown);   // at least some real geometry when KiCad is present
    }

    [Fact]
    public void BoardExtentOf_ReadsTheMaximumCornerOfThePlacerOutline()
    {
        var outline = new List<double[]>
        {
            new[] { 0.0, 0.0, 48.5, 0.0 },
            new[] { 48.5, 0.0, 48.5, 31.25 },
            new[] { 48.5, 31.25, 0.0, 31.25 },
            new[] { 0.0, 31.25, 0.0, 0.0 },
        };
        var e = EnclosureFit.BoardExtentOf(outline);
        Assert.Equal(48.5, e.WidthMm, 2);
        Assert.Equal(31.25, e.DepthMm, 2);
    }

    [Fact]
    public void BoardExtentOf_ToleratesAMissingOrMalformedOutline()
    {
        Assert.Equal(0.0, EnclosureFit.BoardExtentOf(Array.Empty<double[]>()).WidthMm, 2);
        Assert.Equal(0.0, EnclosureFit.BoardExtentOf(new[] { new[] { 1.0 } }).WidthMm, 2);
    }

    // End-to-end and PURE: no KiCad. This is the check that would have caught a generated case the
    // board cannot physically go into.
    [Fact]
    public void CheckProject_PlacesTheBoardAndComparesItToTheGeneratedCase()
    {
        var demo = DemoData.CreateSoilMoistureProject();
        var findings = EnclosureFit.CheckProject(demo);

        Assert.NotEmpty(findings);
        // Whatever the verdict, it must be a real one — never a silent pass over unmeasured parts.
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Code)));
    }

    [Fact]
    public void CheckProject_FailsWhenTheCaseIsTooSmallForTheBoardItPlaced()
    {
        var demo = DemoData.CreateSoilMoistureProject();
        demo.Enclosure.Inner = new[] { 10.0, 10.0, 10.0 };   // absurdly small for any real board

        var findings = EnclosureFit.CheckProject(demo);
        Assert.Contains(findings, f => f is { Code: "FIT-XY", Severity: "fail" });
    }

    [Fact]
    public void CheckProject_OnAProjectWithNoComponents_SaysNothing()
    {
        var empty = new Project { Enclosure = new Enclosure { Inner = new[] { 60.0, 40.0, 25.0 } } };
        Assert.Empty(EnclosureFit.CheckProject(empty));
    }

    // The wiring: mechanical findings must reach the same report card as the electrical ones.
    [Fact]
    public void Revalidate_SurfacesMechanicalFindingsAlongsideElectricalOnes()
    {
        var demo = DemoData.CreateSoilMoistureProject();
        ProjectValidator.Revalidate(demo);

        // The shipped sample's case (62 x 48 x 26) was never derived from its board: a naive placement
        // of an 88 mm 18650 holder alongside the MCU and headers is ~220 mm long. The check says so.
        Assert.Contains(demo.Findings, f => f is { Code: "FIT-XY", Severity: "fail" });
        Assert.Equal("fail", demo.Validation);
    }

    // Caught by rendering the tab, not by a test: mechanical findings were APPENDED after
    // RulesEngine.Validate had already sorted and numbered its own, so they sat below the passing rows
    // with a blank number column. They must be re-ordered and re-numbered with everything else.
    [Fact]
    public void Revalidate_SortsAndNumbersMechanicalFindingsWithTheElectricalOnes()
    {
        var demo = DemoData.CreateSoilMoistureProject();
        ProjectValidator.Revalidate(demo);

        Assert.All(demo.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Num),
            $"{f.Code} has no display number — it bypassed ordering"));

        int Rank(string s) => s switch { "fail" => 0, "warn" => 1, "unproven" => 2, "info" => 3, _ => 4 };
        var ranks = demo.Findings.Select(f => Rank(f.Severity)).ToList();
        Assert.Equal(ranks.OrderBy(r => r), ranks);   // severity order is monotonic

        var fit = demo.Findings.First(f => f.Code == "FIT-XY");
        Assert.StartsWith("F·", fit.Num, StringComparison.Ordinal);
    }

    // A project with no enclosure asked for is not a mechanical failure.
    [Fact]
    public void Revalidate_StaysSilentWhenNoEnclosureWasRequested()
    {
        var p = new Project
        {
            Components = new() { new Foundry.Core.Kb.ComponentSpec { Ref = "u", Alias = "U1", Name = "MCU" } },
            Enclosure = new Enclosure(),   // Inner defaults to 0,0,0
        };
        ProjectValidator.Revalidate(p);
        Assert.DoesNotContain(p.Findings, f => f.Code.StartsWith("FIT-", StringComparison.Ordinal));
    }

    [Fact]
    public void Rollup_RanksFailAboveWarnAboveUnproven() =>
        Assert.Equal("fail", ProjectValidator.Rollup(new[]
        {
            new Finding { Severity = "unproven" },
            new Finding { Severity = "warn" },
            new Finding { Severity = "fail" },
        }));
}

public class StepHeightsTests
{
    private static string? ModelDir()
    {
        var kicad = Foundry.Core.Pcb.KiCadInstaller.Locate();
        if (kicad is null) return null;
        var dir = StepHeights.ModelDirFor(kicad.FootprintDir);
        return Directory.Exists(dir) ? dir : null;
    }

    // Real STEP models, real datasheet dimensions. Skips when KiCad isn't installed.
    [Theory]
    [InlineData("Resistor_SMD:R_0805_2012Metric", 0.45)]
    [InlineData("Capacitor_SMD:C_0603_1608Metric", 0.80)]
    [InlineData("RF_Module:ESP32-WROOM-32", 3.10)]
    [InlineData("Package_DIP:DIP-28_W7.62mm", 3.68)]
    public void HeightAboveBoard_MatchesTheRealPart(string libId, double expected)
    {
        var dir = ModelDir();
        if (dir is null) return;   // no KiCad — nothing to measure against

        var h = StepHeights.For(libId, dir);
        Assert.True(h.IsKnown, $"no height resolved for {libId}");
        Assert.Equal(expected, h.AboveMm, 1);
    }

    [Fact]
    public void PinTailsBelowTheBoardAreMeasuredSeparately()
    {
        var dir = ModelDir();
        if (dir is null) return;

        var h = StepHeights.For("Connector_PinHeader_2.54mm:PinHeader_1x04_P2.54mm_Vertical", dir);
        Assert.True(h.IsKnown);
        Assert.True(h.BelowMm > 2.0, $"header pin tails should hang below the board, got {h.BelowMm}");
    }

    // The four footprints Foundry emits that KiCad ships no model for must still resolve.
    [Theory]
    [InlineData("Module:RaspberryPi_Pico_Common_SMD")]
    [InlineData("Module:Arduino_UNO_R3")]
    [InlineData("Module:Arduino_Nano")]
    [InlineData("Package_TO_SOT_SMD:SOT-223-3_TabPin2")]
    public void CuratedHeights_CoverTheFootprintsKiCadDoesNotModel(string libId)
    {
        var h = StepHeights.For(libId, ModelDir());
        Assert.True(h.IsKnown, $"{libId} has neither a shipped model nor a curated height");
        Assert.True(h.AboveMm > 0);
    }

    [Fact]
    public void AnUnmodelledPartIsUnknown_NotZero()
    {
        var h = StepHeights.For("Nonexistent_Lib:No_Such_Footprint", ModelDir());
        Assert.False(h.IsKnown);
        Assert.True(double.IsNaN(h.AboveMm));   // never 0.0, which would read as "flat"
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon")]
    public void MalformedLibIdsAreUnknown(string libId) =>
        Assert.False(StepHeights.For(libId, ModelDir()).IsKnown);
}
