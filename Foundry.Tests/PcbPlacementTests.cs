using Foundry.Core.Ai;
using Foundry.Core.Kb;
using Foundry.Core.Pcb;
using Foundry.Core.Project;

namespace Foundry.Tests;

// ---- PlacementPlan tolerant parse ----------------------------------------------------------------

public class PlacementPlanTests
{
    // A full, well-formed plan exercising groups (with edge), hints (group/near/edge/rotation),
    // and an explicit regionOrder — the same fixture-on-disk-in-source shape GenerationTests uses.
    private const string FullFixture = """
    {
      "groups": [
        {"id":"power","members":["U2","C1"],"edge":"none"},
        {"id":"mcu","members":["U1","C2","Y1"]},
        {"id":"connectors","members":["J1","J2"],"edge":"left"}
      ],
      "hints": [
        {"ref":"C3","near":"U1","rotation":90},
        {"ref":"J2","edge":"right"},
        {"ref":"ANT1","edge":"top"}
      ],
      "regionOrder": ["power","mcu","connectors"]
    }
    """;

    [Fact]
    public void Parse_FullFixture_ReadsGroupsHintsRegions()
    {
        var plan = PlacementPlan.Parse(FullFixture);

        Assert.Equal(3, plan.Groups.Count);
        var power = plan.Groups.First(g => g.Id == "power");
        Assert.Equal(new[] { "U2", "C1" }, power.Members);
        Assert.Equal(EdgeAffinity.None, power.Edge);
        Assert.Equal(EdgeAffinity.Left, plan.Groups.First(g => g.Id == "connectors").Edge);

        Assert.Equal(3, plan.Hints.Count);
        var c3 = plan.Hints.First(h => h.Ref == "C3");
        Assert.Equal("U1", c3.NearRef);          // parsed from JSON key "near"
        Assert.Equal(90, c3.Rotation);
        Assert.Equal(EdgeAffinity.Right, plan.Hints.First(h => h.Ref == "J2").Edge);

        Assert.Equal(new[] { "power", "mcu", "connectors" }, plan.RegionOrder);
    }

    [Fact]
    public void Parse_Sparse_DefaultsAreSafe()
    {
        // Missing arrays, a group with no edge, a hint that is just a ref.
        var plan = PlacementPlan.Parse("""{"groups":[{"id":"only","members":["U1"]}],"hints":[{"ref":"U2"}]}""");

        Assert.Single(plan.Groups);
        Assert.Equal(EdgeAffinity.None, plan.Groups[0].Edge);
        Assert.Single(plan.Hints);
        var h = plan.Hints[0];
        Assert.Null(h.NearRef);
        Assert.Null(h.Group);
        Assert.Equal(EdgeAffinity.None, h.Edge);
        Assert.Equal(0, h.Rotation);
        Assert.Empty(plan.RegionOrder);
    }

    [Fact]
    public void Parse_GarbledButValidJson_DegradesGracefully_StillUsable()
    {
        // Unknown edge string → None; non-numeric rotation → 0; empty id group + empty ref hint dropped;
        // a stray member that is blank is dropped. The plan is still valid and usable.
        var plan = PlacementPlan.Parse("""
        {
          "groups": [
            {"id":"keep","members":["U1","",null],"edge":"northwest"},
            {"id":"","members":["X9"]}
          ],
          "hints": [
            {"ref":"C1","near":"U1","rotation":"not-a-number"},
            {"ref":"","edge":"left"}
          ],
          "regionOrder": ["keep","", null]
        }
        """);

        Assert.Single(plan.Groups);                       // empty-id group dropped
        Assert.Equal("keep", plan.Groups[0].Id);
        Assert.Equal(new[] { "U1" }, plan.Groups[0].Members);  // blank/null members dropped
        Assert.Equal(EdgeAffinity.None, plan.Groups[0].Edge);  // unknown edge → None

        Assert.Single(plan.Hints);                        // empty-ref hint dropped
        Assert.Equal("C1", plan.Hints[0].Ref);
        Assert.Equal(0, plan.Hints[0].Rotation);          // non-numeric rotation → 0

        Assert.Equal(new[] { "keep" }, plan.RegionOrder); // blank/null region entries dropped
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]            // valid JSON, but not an object
    [InlineData("{}")]                 // object, but all lists empty
    [InlineData("""{"groups":[],"hints":[],"regionOrder":[]}""")]
    public void Parse_NullEmptyGarbageOrEmptyLists_ReturnsSingletonEmpty(string? json)
    {
        Assert.Same(PlacementPlan.Empty, PlacementPlan.Parse(json));
    }
}

// ---- PcbPlacer deterministic placement -----------------------------------------------------------

public class PcbPlacerTests
{
    private static PcbPlacer.PlacedItem Item(string @ref, double w, double h, double rot = 0) =>
        new(@ref, "lib:" + @ref, (w, h), rot);

    // Reconstruct the inflated packing box for a placed ref (centre + half-extents), so we can assert
    // pairwise non-overlap exactly as the placer guarantees it (inflated boxes are disjoint).
    private static (double X0, double Y0, double X1, double Y1) InflatedBox(
        PcbPlacer.PlaceResult res, PcbPlacer.PlacedItem it, double gap, PlacementPlan plan)
    {
        var p = res[it.Ref];
        var (w, h) = it.Courtyard;
        if (p.Rot is 90 or 270) (w, h) = (h, w);
        double hw = (w + 2 * gap) / 2.0;
        double hh = (h + 2 * gap) / 2.0;
        return (p.XMm - hw, p.YMm - hh, p.XMm + hw, p.YMm + hh);
    }

    private static bool Overlap((double X0, double Y0, double X1, double Y1) a,
                                (double X0, double Y0, double X1, double Y1) b)
    {
        const double eps = 1e-6;   // touching edges (shared boundary) is OK; only true overlap fails
        return a.X0 < b.X1 - eps && a.X1 > b.X0 + eps && a.Y0 < b.Y1 - eps && a.Y1 > b.Y0 + eps;
    }

    private static void AssertNoOverlaps(IReadOnlyList<PcbPlacer.PlacedItem> items, PlacementPlan plan,
        double gap = 1.5)
    {
        var res = PcbPlacer.Place(items, plan, gapMm: gap);
        var boxes = items.Select(i => (i.Ref, Box: InflatedBox(res, i, gap, plan))).ToList();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                Assert.False(Overlap(boxes[i].Box, boxes[j].Box),
                    $"{boxes[i].Ref} overlaps {boxes[j].Ref}");
    }

    private static (double W, double D) BoardSize(PcbPlacer.PlaceResult res)
    {
        double w = 0, d = 0;
        foreach (var s in res.OutlineSegmentsMm)
        {
            w = Math.Max(w, Math.Max(s[0], s[2]));
            d = Math.Max(d, Math.Max(s[1], s[3]));
        }
        return (w, d);
    }

    // ---- default (no-plan) packing efficiency -----------------------------------------------------
    //
    // The regression: the no-plan path used a uniform grid whose cells were sized to the LARGEST part in
    // BOTH axes, so a single big component inflated every cell. The shipped demo — an 88 mm 18650 holder
    // beside an MCU and three small parts — laid out at 220 x 48 mm of mostly empty copper, and the
    // enclosure derived from that outline was correspondingly wrong.

    private static IReadOnlyList<PcbPlacer.PlacedItem> OneDominantPart() => new[]
    {
        Item("BAT", 88.0, 21.75),   // 18650 holder — far larger than everything else
        Item("U1", 18.0, 25.5),
        Item("J1", 7.62, 2.54),
        Item("J2", 5.08, 2.54),
        Item("C1", 1.6, 0.8),
    };

    [Fact]
    public void OneOversizedPart_DoesNotInflateTheWholeBoard()
    {
        var items = OneDominantPart();
        var (w, d) = BoardSize(PcbPlacer.Place(items, PlacementPlan.Empty, marginMm: 5, gapMm: 2));

        // A board can never be narrower than its widest part, but it must not be a multiple of it either.
        Assert.True(w < 88.0 * 1.5, $"board width {w:0.#} mm is inflated well past the 88 mm part");
        Assert.True(d < 88.0, $"board depth {d:0.#} mm should not scale with the widest part");
    }

    [Fact]
    public void BoardIsAtLeastAsWideAsItsWidestPart()
    {
        var items = OneDominantPart();
        var (w, _) = BoardSize(PcbPlacer.Place(items, PlacementPlan.Empty, marginMm: 5, gapMm: 2));
        Assert.True(w >= 88.0, $"board width {w:0.#} mm cannot hold an 88 mm part");
    }

    [Fact]
    public void DominantPartLayout_StillHasNoOverlaps() =>
        AssertNoOverlaps(OneDominantPart(), PlacementPlan.Empty);

    // Small parts must share a shelf with the tall part rather than each wrapping onto their own row —
    // this is what First-Fit-Decreasing-Height buys over filling only the newest shelf.
    [Fact]
    public void SmallPartsShareShelvesWithTallerOnes()
    {
        var items = new[]
        {
            Item("TALL", 20, 30),
            Item("A", 5, 4), Item("B", 5, 4), Item("C", 5, 4), Item("D", 5, 4),
        };
        var res = PcbPlacer.Place(items, PlacementPlan.Empty, marginMm: 5, gapMm: 2);
        var rows = items.Select(i => Math.Round(res[i.Ref].YMm - (i.Courtyard.HMm / 2), 1)).Distinct().Count();
        Assert.True(rows <= 3, $"5 small parts spread over {rows} rows — shelves are not being reused");
    }

    [Fact]
    public void Placement_IsDeterministic()
    {
        var items = OneDominantPart();
        var a = PcbPlacer.Place(items, PlacementPlan.Empty);
        var b = PcbPlacer.Place(items, PlacementPlan.Empty);
        foreach (var i in items)
        {
            Assert.Equal(a[i.Ref].XMm, b[i.Ref].XMm, 4);
            Assert.Equal(a[i.Ref].YMm, b[i.Ref].YMm, 4);
        }
    }

    // A few fixture parts lists, including a dense one with many same-size parts.
    private static IReadOnlyList<PcbPlacer.PlacedItem> SmallMixed() => new[]
    {
        Item("U1", 18, 25.5),   // mcu module
        Item("C1", 1.6, 0.8),
        Item("C2", 1.6, 0.8),
        Item("R1", 2.0, 1.25),
        Item("J1", 10.16, 2.54),
    };

    private static IReadOnlyList<PcbPlacer.PlacedItem> Dense()
    {
        var list = new List<PcbPlacer.PlacedItem>();
        for (int i = 0; i < 40; i++) list.Add(Item($"C{i:00}", 1.6, 0.8));
        for (int i = 0; i < 10; i++) list.Add(Item($"R{i:00}", 2.0, 1.25));
        list.Add(Item("U1", 18, 25.5));
        return list;
    }

    [Fact]
    public void EmptyPlan_NoOverlap_TidyGrid_DistinctPositions()
    {
        var items = SmallMixed();
        var res = PcbPlacer.Place(items, PlacementPlan.Empty);

        // every ref placed exactly once
        Assert.Equal(items.Count, res.Positions.Count);
        Assert.All(items, i => Assert.True(res.TryGet(i.Ref, out _)));

        // distinct positions
        var pts = items.Select(i => (res[i.Ref].XMm, res[i.Ref].YMm)).ToList();
        Assert.Equal(pts.Count, pts.Distinct().Count());

        AssertNoOverlaps(items, PlacementPlan.Empty);
    }

    [Fact]
    public void NullPlan_TreatedAsEmpty()
    {
        var items = SmallMixed();
        var res = PcbPlacer.Place(items, null!);
        Assert.Equal(items.Count, res.Positions.Count);
        AssertNoOverlaps(items, PlacementPlan.Empty);
    }

    [Fact]
    public void NoItems_ProducesMarginSquareOutline()
    {
        var res = PcbPlacer.Place(Array.Empty<PcbPlacer.PlacedItem>(), PlacementPlan.Empty, marginMm: 5);
        Assert.Empty(res.Positions);
        Assert.Equal(4, res.OutlineSegmentsMm.Count);
        // board is 2*margin square
        Assert.Equal(10.0, res.OutlineSegmentsMm[1][0], 3);   // right edge x == w == 2*margin
    }

    [Fact]
    public void NoOverlap_AcrossSeveralFixtures_IncludingDense()
    {
        var plan = PlacementPlan.Parse("""
        {"groups":[{"id":"mcu","members":["U1"]},{"id":"caps"}],
         "hints":[{"ref":"C1","near":"U1"}],
         "regionOrder":["mcu","caps"]}
        """);
        AssertNoOverlaps(SmallMixed(), plan);
        AssertNoOverlaps(SmallMixed(), PlacementPlan.Empty);
        AssertNoOverlaps(Dense(), PlacementPlan.Empty);
        AssertNoOverlaps(Dense(), PlacementPlan.Parse("""{"groups":[{"id":"big","members":["U1"]}]}"""));
    }

    [Fact]
    public void EveryRef_AppearsExactlyOnce_EvenUnassigned()
    {
        // Plan references some refs; others fall to _unassigned and must still be placed.
        var items = SmallMixed();
        var plan = PlacementPlan.Parse("""{"groups":[{"id":"g","members":["U1","C1"]}]}""");
        var res = PcbPlacer.Place(items, plan);
        Assert.Equal(items.Count, res.Positions.Count);
        foreach (var i in items) Assert.True(res.TryGet(i.Ref, out _));
    }

    [Fact]
    public void EdgeAffinity_Left_PutsConnectorOnBoardBoundary()
    {
        var items = new[]
        {
            Item("U1", 18, 25.5),
            Item("J1", 10.16, 2.54),
        };
        var plan = PlacementPlan.Parse("""
        {"groups":[{"id":"mcu","members":["U1"]},{"id":"io","members":["J1"],"edge":"left"}]}
        """);
        const double gap = 1.5, margin = 5.0;
        var res = PcbPlacer.Place(items, plan, marginMm: margin, gapMm: gap);

        var j1 = res["J1"];
        var u1 = res["U1"];
        // J1's left courtyard edge is flush to the left margin (its centre - half width == margin),
        // within the 0.05mm placement snap grid.
        double j1Left = j1.XMm - (10.16 + 2 * gap) / 2.0;
        Assert.True(Math.Abs(j1Left - margin) <= 0.05, $"left edge {j1Left} not flush to margin {margin}");
        // and the connector is left of the MCU interior.
        Assert.True(j1.XMm < u1.XMm, "left-edge connector should sit left of interior");
        AssertNoOverlaps(items, plan, gap);
    }

    [Fact]
    public void EdgeAffinity_HintOverridesGroupEdge()
    {
        // group edge is left, but a per-item hint pins J2 to the right.
        var items = new[]
        {
            Item("U1", 18, 25.5),
            Item("J1", 10.16, 2.54),
            Item("J2", 10.16, 2.54),
        };
        var plan = PlacementPlan.Parse("""
        {"groups":[{"id":"mcu","members":["U1"]},{"id":"io","members":["J1","J2"],"edge":"left"}],
         "hints":[{"ref":"J2","edge":"right"}]}
        """);
        var res = PcbPlacer.Place(items, plan);
        // J1 (group=left) is left of U1; J2 (hint=right) is right of U1.
        Assert.True(res["J1"].XMm < res["U1"].XMm);
        Assert.True(res["J2"].XMm > res["U1"].XMm);
        AssertNoOverlaps(items, plan);
    }

    [Fact]
    public void NearRef_SeatsCapAdjacentToTarget()
    {
        var items = new[]
        {
            Item("U1", 8, 8),
            Item("C1", 1.6, 0.8),     // decoupling cap for U1
            Item("R1", 2.0, 1.25),
        };
        var plan = PlacementPlan.Parse("""
        {"groups":[{"id":"mcu","members":["U1","R1"]}],"hints":[{"ref":"C1","near":"U1"}]}
        """);
        const double gap = 1.5;
        var res = PcbPlacer.Place(items, plan, gapMm: gap);

        var u1 = res["U1"];
        var c1 = res["C1"];
        // cap centre is within one (courtyard + gap) span of its target on at least one axis — i.e.
        // it is seated directly against U1, not parked across the board.
        double dx = Math.Abs(c1.XMm - u1.XMm);
        double dy = Math.Abs(c1.YMm - u1.YMm);
        // Seated directly against U1 = centre-to-centre distance is at most the sum of the two inflated
        // half-extents plus one gap (the placer packs inflated courtyards U1+2*gap, C1+2*gap touching).
        double adjX = ((8 + 2 * gap) + (1.6 + 2 * gap)) / 2.0 + gap + 0.05;
        double adjY = ((8 + 2 * gap) + (0.8 + 2 * gap)) / 2.0 + gap + 0.05;
        Assert.True(dx <= adjX && dy <= adjY,
            $"cap not adjacent to target: dx={dx} (max {adjX}), dy={dy} (max {adjY})");
        AssertNoOverlaps(items, plan, gap);
    }

    [Fact]
    public void Output_IsDeterministic()
    {
        var items = Dense();
        var plan = PlacementPlan.Parse("""{"groups":[{"id":"g","members":["U1"]}],"hints":[{"ref":"C00","near":"U1"}]}""");
        var a = PcbPlacer.Place(items, plan);
        var b = PcbPlacer.Place(items, plan);
        foreach (var i in items)
        {
            Assert.Equal(a[i.Ref].XMm, b[i.Ref].XMm);
            Assert.Equal(a[i.Ref].YMm, b[i.Ref].YMm);
            Assert.Equal(a[i.Ref].Rot, b[i.Ref].Rot);
        }
    }

    [Fact]
    public void Outline_ContainsEveryPlacedBox()
    {
        var items = SmallMixed();
        const double gap = 1.5;
        var res = PcbPlacer.Place(items, PlacementPlan.Empty, gapMm: gap);
        double boardW = res.OutlineSegmentsMm[1][0];   // right edge
        double boardH = res.OutlineSegmentsMm[1][3];   // top edge
        foreach (var it in items)
        {
            var box = InflatedBox(res, it, gap, PlacementPlan.Empty);
            Assert.True(box.X0 >= -0.01 && box.Y0 >= -0.01, $"{it.Ref} off bottom/left");
            Assert.True(box.X1 <= boardW + 0.01 && box.Y1 <= boardH + 0.01, $"{it.Ref} off top/right");
        }
    }
}

// ---- FootprintMap.CourtyardOf (placer input) -----------------------------------------------------

public class CourtyardOfTests
{
    [Theory]
    [InlineData("RF_Module:ESP32-WROOM-32", 18.0, 25.5)]
    [InlineData("Module:RaspberryPi_Pico_Common_SMD", 21.0, 51.0)]
    [InlineData("Resistor_SMD:R_0402_1005Metric", 1.0, 0.5)]
    [InlineData("Resistor_SMD:R_0603_1608Metric", 1.6, 0.8)]
    [InlineData("Resistor_SMD:R_0805_2012Metric", 2.0, 1.25)]
    [InlineData("Capacitor_SMD:C_1206_3216Metric", 3.2, 1.6)]
    [InlineData("Package_TO_SOT_SMD:SOT-23", 3.0, 3.0)]
    [InlineData("Package_TO_SOT_SMD:SOT-223-3_TabPin2", 7.0, 7.0)]
    [InlineData("Package_TO_SOT_THT:TO-220-3_Vertical", 10.0, 4.5)]
    [InlineData("Diode_SMD:D_SOD-123", 2.7, 1.6)]
    [InlineData("Connector_PinHeader_2.54mm:PinHeader_1x04_P2.54mm_Vertical", 10.16, 2.54)]
    public void CourtyardOf_KnownLibIds(string libId, double w, double h)
    {
        var (cw, ch) = FootprintMap.CourtyardOf(libId);
        Assert.Equal(w, cw, 2);
        Assert.Equal(h, ch, 2);
    }

    [Fact]
    public void CourtyardOf_CountScaled_Soic8()
    {
        var (w, h) = FootprintMap.CourtyardOf("Package_SO:SOIC-8_3.9x4.9mm_P1.27mm");
        Assert.Equal((8 / 2) * 1.27 + 2, w, 2);   // 7.08
        Assert.Equal(6.0, h, 2);
    }

    [Fact]
    public void CourtyardOf_Unmatched_IsGenerousNonZeroDefault()
    {
        var (w, h) = FootprintMap.CourtyardOf("Some:Totally_Unknown_Footprint");
        Assert.Equal(10.0, w, 2);
        Assert.Equal(10.0, h, 2);
    }

    [Fact]
    public void CourtyardOf_NeverZero()
    {
        Assert.True(FootprintMap.CourtyardOf("").WMm > 0);
        Assert.True(FootprintMap.CourtyardOf(null!).HMm > 0);
    }
}

// ---- PcbPlanner AI pass (no live key) ------------------------------------------------------------

public class PcbPlannerTests
{
    private sealed class FakeAi : IAnthropicClient
    {
        private readonly string _json;
        public FakeAi(string json) => _json = json;
        public bool HasKey => true;
        public Task<ModelListResult> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModelListResult(true, ModelCatalog.Fallback, null));
        public Task<string> CompleteAsync(string s, string u, string m, CancellationToken ct = default) =>
            Task.FromResult(_json);
    }

    private static Project MiniProject() => new()
    {
        Title = "Mini Board",
        Components = new()
        {
            new ComponentSpec { Alias = "U1", Ref = "esp32", Name = "ESP32-WROOM-32" },
            new ComponentSpec { Alias = "C1", Ref = "cap", Name = "100nF capacitor" },
        },
        Connections = new()
        {
            new Connection { From = "U1.3V3", To = "C1.1", Net = "power" },
            new Connection { From = "U1.GND", To = "C1.2", Net = "ground" },
        },
    };

    [Fact]
    public async Task PlanAsync_NoKey_ReturnsEmpty()
    {
        var planner = new PcbPlanner(new StubAnthropicClient());
        var plan = await planner.PlanAsync(MiniProject());
        Assert.Same(PlacementPlan.Empty, plan);
    }

    [Fact]
    public async Task PlanAsync_FixtureReply_ParsesPlan()
    {
        const string reply = """
        Here is the placement plan:
        {"groups":[{"id":"mcu","members":["U1","C1"]}],
         "hints":[{"ref":"C1","near":"U1"}],
         "regionOrder":["mcu"]}
        """;
        var planner = new PcbPlanner(new FakeAi(reply));
        var plan = await planner.PlanAsync(MiniProject());

        Assert.Single(plan.Groups);
        Assert.Equal("mcu", plan.Groups[0].Id);
        Assert.Equal("U1", plan.Hints.Single().NearRef);
    }

    [Fact]
    public async Task PlanAsync_GarbageReply_ReturnsEmpty_NeverThrows()
    {
        var planner = new PcbPlanner(new FakeAi("the model rambled and gave no json"));
        var plan = await planner.PlanAsync(MiniProject());
        Assert.Same(PlacementPlan.Empty, plan);
    }
}

// ---- PcbJob.Build with an AI plan (contract unchanged) -------------------------------------------

public class PcbJobWithPlanTests
{
    private static Project MiniProject() => new()
    {
        Title = "Mini Board",
        Components = new()
        {
            new ComponentSpec { Alias = "MCU", Ref = "esp32", Name = "ESP32 DevKit" },
            new ComponentSpec { Alias = "SENSOR", Ref = "bme280", Name = "BME280" },
        },
        Connections = new()
        {
            new Connection { From = "MCU.3V3", To = "SENSOR.VCC", Net = "power" },
            new Connection { From = "MCU.GND", To = "SENSOR.GND", Net = "ground" },
            new Connection { From = "MCU.GPIO21", To = "SENSOR.SDA", Net = "i2c" },
            new Connection { From = "MCU.GPIO22", To = "SENSOR.SCL", Net = "i2c" },
        },
    };

    [Fact]
    public void Build_WithPlan_KeepsComponentContract_OnlyCoordinatesDiffer()
    {
        var p = MiniProject();
        var grid = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>());            // Empty/grid
        var plan = PlacementPlan.Parse("""
        {"groups":[{"id":"mcu","members":["MCU"]},{"id":"io","members":["SENSOR"],"edge":"left"}]}
        """);
        var ai = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>(), plan);

        // same refs, footprints, and per-pad nets — the byte-compatible contract is preserved.
        Assert.Equal(
            grid.Components.Select(c => c.Ref).OrderBy(x => x),
            ai.Components.Select(c => c.Ref).OrderBy(x => x));

        foreach (var g in grid.Components)
        {
            var a = ai.Components.First(c => c.Ref == g.Ref);
            Assert.Equal(g.Footprint, a.Footprint);
            Assert.Equal(g.PadNets.OrderBy(kv => kv.Key), a.PadNets.OrderBy(kv => kv.Key));
        }

        // only the placement changed: the edge plan must move at least one part vs. the grid.
        bool moved = grid.Components.Any(g =>
        {
            var a = ai.Components.First(c => c.Ref == g.Ref);
            return Math.Abs(g.XMm - a.XMm) > 0.01 || Math.Abs(g.YMm - a.YMm) > 0.01;
        });
        Assert.True(moved, "AI plan should change at least one coordinate vs. the tidy grid");
    }

    [Fact]
    public void Build_WithPlan_StillValid_NoErrors_RefsAndPadNetsIntact()
    {
        var plan = PlacementPlan.Parse("""
        {"groups":[{"id":"mcu","members":["MCU"]}],"hints":[{"ref":"SENSOR","near":"MCU"}]}
        """);
        var job = PcbJob.Build(MiniProject(), "out.kicad_pcb", Array.Empty<string>(), plan);

        Assert.DoesNotContain(job.Diagnostics, d => d.Severity == "error");
        Assert.Equal(new[] { "MCU", "SENSOR" }, job.Components.Select(c => c.Ref).OrderBy(x => x).ToArray());

        var mcu = job.Components.First(c => c.Ref == "MCU");
        Assert.Equal("SDA", mcu.PadNets["GPIO21"]);
        Assert.Equal("GND", mcu.PadNets["GND"]);

        // distinct positions + 4-segment closed rectangular outline preserved.
        var pts = job.Components.Select(c => (c.XMm, c.YMm)).ToList();
        Assert.Equal(pts.Count, pts.Distinct().Count());
        Assert.Equal(4, job.OutlineSegmentsMm.Count);
    }
}
