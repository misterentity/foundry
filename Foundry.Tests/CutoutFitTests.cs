using Foundry.Core.Cad;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Tests;

// Cutout positions came straight from the generation JSON with NOTHING linking a cutout to the part it
// exposes, so the USB hole landed wherever the model felt like putting it. That is the most expensive
// error the enclosure can make: the case is perfectly manufacturable and simply wrong, and you find out
// after printing it.
public class CutoutFitTests
{
    private const double Wall = 2.0;
    private static readonly BoardExtent Board = new(80.0, 60.0);

    private static BoardPlacement Placement(params PartPlacement[] parts) =>
        new(Board, Array.Empty<double[]>(), StandoffMm: 4.0, ThicknessMm: 1.6)
        {
            Parts = parts.ToDictionary(p => p.Alias, p => p, StringComparer.OrdinalIgnoreCase),
        };

    private static Enclosure Case(params Cutout[] cutouts) => new()
    {
        Inner = new[] { 82.0, 62.0, 25.0 },
        Wall = Wall,
        Cutouts = cutouts.ToList(),
    };

    // ---- face selection ---------------------------------------------------------------------------

    [Theory]
    // board is 80 x 60; a part hugging each edge in turn
    [InlineData(5.0, 30.0, "left")]
    [InlineData(75.0, 30.0, "right")]
    [InlineData(40.0, 5.0, "front")]
    [InlineData(40.0, 55.0, "back")]
    public void NearestFace_PicksTheEdgeThePartHugs(double x, double y, string expected)
    {
        var part = new PartPlacement("J1", x, y, 8.0, 6.0, 5.0);
        Assert.Equal(expected, CutoutFit.NearestFace(part, Board).Face);
    }

    [Fact]
    public void NearestFace_ReportsTheGapFromTheCourtyardEdge()
    {
        var part = new PartPlacement("J1", 10.0, 30.0, 8.0, 6.0, 5.0);   // left edge of courtyard at x=6
        var (face, gap) = CutoutFit.NearestFace(part, Board);
        Assert.Equal("left", face);
        Assert.Equal(6.0, gap, 2);
    }

    // ---- the transform ----------------------------------------------------------------------------

    // Front face (board y=0). pos[0] runs along board X measured from the case centre; pos[1] is the
    // height above the case's mid-plane, since _cutout_solid offsets a side face from oz/2.
    [Fact]
    public void FrontPort_MapsBoardXToTheHorizontalOffsetAndStackUpToTheVertical()
    {
        var usb = new PartPlacement("USB", XMm: 20.0, YMm: 3.0, WidthMm: 9.0, DepthMm: 7.0, HeightMm: 3.2);
        var (results, _) = CutoutFit.Derive(
            Case(new Cutout { Ref = "USB", Face = "front", Shape = "rect", Label = "USB-C" }),
            Placement(usb));

        var d = Assert.Single(results);
        Assert.True(d.Derived, d.Reason);
        Assert.Equal("front", d.Cutout.Face);

        Assert.Equal(20.0 - 80.0 / 2, d.Cutout.Pos[0], 2);                   // -20 mm of case centre
        var oz = 25.0 + Wall;
        var expectedV = Wall + 4.0 + 1.6 + 3.2 / 2 - oz / 2;                 // floor + standoff + PCB + half part
        Assert.Equal(expectedV, d.Cutout.Pos[1], 2);
    }

    // Left face (board x=0) runs along board Y, not X — getting this wrong puts the port on the wrong axis.
    [Fact]
    public void LeftPort_MapsBoardYToTheHorizontalOffset()
    {
        var gland = new PartPlacement("GLAND", XMm: 4.0, YMm: 45.0, WidthMm: 8.0, DepthMm: 8.0, HeightMm: 12.0);
        var (results, _) = CutoutFit.Derive(
            Case(new Cutout { Ref = "GLAND", Face = "right", Shape = "circle", Label = "M12 gland" }),
            Placement(gland));

        var d = Assert.Single(results);
        Assert.True(d.Derived, d.Reason);
        Assert.Equal("left", d.Cutout.Face);                                  // derived, overriding "right"
        Assert.Equal(45.0 - 60.0 / 2, d.Cutout.Pos[0], 2);                    // board Y -> horizontal
    }

    [Fact]
    public void PortSize_ComesFromThePartsCrossSectionOnThatFace()
    {
        var usb = new PartPlacement("USB", 20.0, 3.0, WidthMm: 9.0, DepthMm: 7.0, HeightMm: 3.2);
        var (results, _) = CutoutFit.Derive(
            Case(new Cutout { Ref = "USB", Face = "front", Shape = "rect" }), Placement(usb));

        var size = Assert.Single(results).Cutout.Size!;
        Assert.Equal(9.0 + CutoutFit.PortClearanceMm, size[0], 2);            // across = part width
        Assert.Equal(3.2 + CutoutFit.PortClearanceMm, size[1], 2);            // up = part height
    }

    // A top/bottom face is a legitimate authored choice (a probe slot, a reset button); only the
    // in-plane position is derived.
    [Fact]
    public void TopFace_IsHonouredAndOnlyThePlanarPositionIsDerived()
    {
        var btn = new PartPlacement("BTN", 60.0, 50.0, 6.0, 6.0, 4.0);
        var (results, _) = CutoutFit.Derive(
            Case(new Cutout { Ref = "BTN", Face = "top", Shape = "circle", D = 6, Label = "Reset" }),
            Placement(btn));

        var d = Assert.Single(results);
        Assert.True(d.Derived, d.Reason);
        Assert.Equal("top", d.Cutout.Face);
        Assert.Equal(60.0 - 40.0, d.Cutout.Pos[0], 2);
        Assert.Equal(50.0 - 30.0, d.Cutout.Pos[1], 2);
        Assert.Equal(6, d.Cutout.D);                                          // authored diameter kept
    }

    // ---- refusals: keep the model's value, and say it is unverified --------------------------------

    [Fact]
    public void ACutoutNamingNoComponent_IsKeptAndReportedUnproven()
    {
        var original = new Cutout { Face = "front", Shape = "rect", Pos = new[] { 3.0, -4.0 }, Label = "USB-C" };
        var (results, findings) = CutoutFit.Derive(Case(original), Placement());

        var d = Assert.Single(results);
        Assert.False(d.Derived);
        Assert.Equal(new[] { 3.0, -4.0 }, d.Cutout.Pos);                      // untouched
        Assert.Single(findings, f => f is { Code: "CUT-POS", Severity: "unproven" });
    }

    [Fact]
    public void ACutoutNamingAMissingPart_IsKeptAndReportedUnproven()
    {
        var (results, findings) = CutoutFit.Derive(
            Case(new Cutout { Ref = "GHOST", Face = "front", Shape = "rect", Label = "USB-C" }),
            Placement(new PartPlacement("USB", 20, 3, 9, 7, 3.2)));

        Assert.False(Assert.Single(results).Derived);
        Assert.NotEmpty(findings);
    }

    // A part in the middle of the board has no defensible face — deriving one would be a guess.
    [Fact]
    public void APartFarFromEveryEdge_IsNotDerived()
    {
        var mid = new PartPlacement("MCU", 40.0, 30.0, 18.0, 25.0, 3.1);
        var (results, _) = CutoutFit.Derive(
            Case(new Cutout { Ref = "MCU", Face = "front", Shape = "rect" }), Placement(mid));

        var d = Assert.Single(results);
        Assert.False(d.Derived);
        Assert.Contains("from the nearest edge", d.Reason);
    }

    // Without a height there is no vertical position for a SIDE port — refuse rather than assume zero.
    [Fact]
    public void ASidePortWithNoPartHeight_IsNotDerived()
    {
        var noH = new PartPlacement("USB", 20.0, 3.0, 9.0, 7.0, double.NaN);
        var (results, _) = CutoutFit.Derive(
            Case(new Cutout { Ref = "USB", Face = "front", Shape = "rect" }), Placement(noH));

        var d = Assert.Single(results);
        Assert.False(d.Derived);
        Assert.Contains("height", d.Reason);
    }

    [Fact]
    public void AllDerived_ProducesNoUnprovenFinding()
    {
        var (_, findings) = CutoutFit.Derive(
            Case(new Cutout { Ref = "USB", Face = "front", Shape = "rect" }),
            Placement(new PartPlacement("USB", 20, 3, 9, 7, 3.2)));
        Assert.Empty(findings);
    }

    [Fact]
    public void NoCutouts_IsSilent()
    {
        var (results, findings) = CutoutFit.Derive(Case(), Placement());
        Assert.Empty(results);
        Assert.Empty(findings);
    }

    // The unproven verdict must reach the report card, not sit in a list nobody reads.
    [Fact]
    public void UnverifiedCutouts_NeverRollUpToPass()
    {
        var (_, findings) = CutoutFit.Derive(
            Case(new Cutout { Face = "front", Shape = "rect", Label = "USB-C" }), Placement());
        Assert.Equal("unproven", ProjectValidator.Rollup(findings));
    }
}

// The CSG build clamps any cutout that would breach its face edge and carries on, so the case prints
// perfectly with the hole somewhere other than where the design says. That was reported only as a count in
// one status line on the enclosure tab — never on the report card, in the PDF, or in the export.
public class CutoutBoundsTests
{
    // inner 80 x 60 x 25, wall 2  ->  outer 84 x 64 x 27
    private static Enclosure Case(params Cutout[] c) =>
        new() { Inner = new[] { 80.0, 60.0, 25.0 }, Wall = 2.0, Cutouts = c.ToList() };

    private static Cutout Port(string face, double u, double v, double w = 10, double h = 6) =>
        new() { Face = face, Shape = "rect", Size = new[] { w, h }, Pos = new[] { u, v }, Label = "USB-C" };

    [Fact]
    public void APortWellInsideItsFace_IsSilent() =>
        Assert.Empty(CutoutFit.CheckBounds(Case(Port("front", 0, 0))));

    // front face: u runs along ox=84, so the limit is 84/2 - 10/2 - 2 = 35.
    [Theory]
    [InlineData(34.9)]
    [InlineData(35.0)]
    [InlineData(-35.0)]
    public void APortExactlyAtTheLimit_StillFits(double u) =>
        Assert.Empty(CutoutFit.CheckBounds(Case(Port("front", u, 0))));

    [Theory]
    [InlineData(36.0)]
    [InlineData(-36.0)]
    [InlineData(60.0)]
    public void APortPastTheLimit_Warns(double u)
    {
        var f = Assert.Single(CutoutFit.CheckBounds(Case(Port("front", u, 0))));
        Assert.Equal("CUT-FIT", f.Code);
        Assert.Equal("warn", f.Severity);
        Assert.Contains("USB-C", f.Title);
    }

    [Fact]
    public void TheWarningSaysHowFarPastTheEdgeItIs()
    {
        var f = Assert.Single(CutoutFit.CheckBounds(Case(Port("front", 38.0, 0))));
        Assert.Contains("3 mm across", f.Description);   // 38 - 35
    }

    // Each face measures against different case axes; using the wrong pair silently mis-reports.
    [Fact]
    public void EachFaceIsMeasuredAgainstItsOwnAxes()
    {
        // left/right run along oy=64 -> limit 64/2 - 10/2 - 2 = 25. A u of 30 fits on FRONT but not LEFT.
        Assert.Empty(CutoutFit.CheckBounds(Case(Port("front", 30, 0))));
        Assert.Single(CutoutFit.CheckBounds(Case(Port("left", 30, 0))));

        // top runs u along ox=84 and v along oy=64 (not oz).
        Assert.Empty(CutoutFit.CheckBounds(Case(Port("top", 0, 26))));   // limit 64/2 - 6/2 - 2 = 27
        Assert.Single(CutoutFit.CheckBounds(Case(Port("top", 0, 28))));
    }

    // Vertical on a side face is bounded by oz = inner H + wall = 27 (the base is open on top),
    // NOT H + 2*wall. Limit is 27/2 - 6/2 - 2 = 8.5.
    [Fact]
    public void SideFaceVerticalUsesTheOpenTopOuterHeight()
    {
        Assert.Empty(CutoutFit.CheckBounds(Case(Port("front", 0, 8.5))));
        var f = Assert.Single(CutoutFit.CheckBounds(Case(Port("front", 0, 10.5))));
        Assert.Contains("2 mm up", f.Description);
    }

    // A circle's bound comes from D, not from Size — reading the wrong field lets a big hole run off the face.
    [Fact]
    public void ACircleIsBoundedByItsDiameter()
    {
        // front-face limit for D=20 is 42 - 10 - 2 = 30; for D=6 it is 42 - 3 - 2 = 37.
        Cutout Circle(double d, double u) =>
            new() { Face = "front", Shape = "circle", D = d, Pos = new[] { u, 0.0 }, Label = "Fan" };

        Assert.Empty(CutoutFit.CheckBounds(Case(Circle(20, 30))));    // exactly at the limit
        Assert.Single(CutoutFit.CheckBounds(Case(Circle(20, 31))));   // past it
        Assert.Empty(CutoutFit.CheckBounds(Case(Circle(6, 31))));     // same spot, smaller hole, fine
    }

    [Fact]
    public void BothAxesOverrunning_AreReportedTogether()
    {
        var f = Assert.Single(CutoutFit.CheckBounds(Case(Port("front", 40, 12))));
        Assert.Contains("across and", f.Description);
    }

    [Fact]
    public void NoEnclosure_IsSilent() =>
        Assert.Empty(CutoutFit.CheckBounds(new Enclosure { Inner = Array.Empty<double>() }));

    // The whole point: this must not stay a status line. It has to move the rollup.
    [Fact]
    public void AClampedPort_KeepsTheProjectOffAPass() =>
        Assert.Equal("warn", ProjectValidator.Rollup(CutoutFit.CheckBounds(Case(Port("front", 40, 0)))));
}

// The Enclosure header asserted "derived from footprints" for every port — true for none of them
// before this class existed and true for only some of them now. It states the ratio instead.
// Lives here, not in a view-model test: EnclosureViewModel's constructor starts the CAD sidecar.
public class CutoutSourceSummaryTests
{
    private static BoardPlacement Board(params PartPlacement[] parts) =>
        new(new BoardExtent(80, 60), Array.Empty<double[]>(), 4.0, 1.6)
        { Parts = parts.ToDictionary(p => p.Alias, p => p, StringComparer.OrdinalIgnoreCase) };

    private static Enclosure Case(params Cutout[] c) =>
        new() { Inner = new[] { 82.0, 62.0, 25.0 }, Wall = 2.0, Cutouts = c.ToList() };

    [Fact]
    public void NoCutouts_ReadsNone() =>
        Assert.Equal("none", CutoutFit.SummariseSource(Case(), Board()));

    [Fact]
    public void NoBoard_ReadsPositionsFromTheDesign() =>
        Assert.Equal("positions from the design",
            CutoutFit.SummariseSource(Case(new Cutout { Face = "front", Label = "USB-C" }), null));

    [Fact]
    public void NoneDerivable_ReadsPositionsFromTheDesign() =>
        Assert.Equal("positions from the design",
            CutoutFit.SummariseSource(Case(new Cutout { Face = "front", Label = "USB-C" }), Board()));

    [Fact]
    public void AllDerivable_SaysSo() =>
        Assert.Equal("all derived from the board",
            CutoutFit.SummariseSource(
                Case(new Cutout { Ref = "USB", Face = "front", Shape = "rect" }),
                Board(new PartPlacement("USB", 20, 3, 9, 7, 3.2))));

    [Fact]
    public void PartlyDerivable_StatesTheRatio() =>
        Assert.Equal("1 of 2 derived from the board",
            CutoutFit.SummariseSource(
                Case(new Cutout { Ref = "USB", Face = "front", Shape = "rect" },
                     new Cutout { Face = "back", Shape = "rect", Label = "unlinked" }),
                Board(new PartPlacement("USB", 20, 3, 9, 7, 3.2))));
}
