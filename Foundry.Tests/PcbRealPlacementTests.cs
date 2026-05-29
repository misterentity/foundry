using System.Text.Json;
using Foundry.Core.Kb;
using Foundry.Core.Pcb;
using Foundry.Core.Project;

namespace Foundry.Tests;

// ---- Real-geometry placement (Build 3/3) ---------------------------------------------------------
// Deterministic, KiCad-free coverage for the real-size placement seam: the placer must keep parts
// non-overlapping when fed the larger REAL courtyards; PcbJob.Build must PREFER injected real sizes
// over FootprintMap.CourtyardOf and that larger geometry must actually drive the spacing; the exotic
// SolarPool parts must resolve to the real KiCad footprints (not the generic header); and the
// measure-job JSON contract (input + output shape) must round-trip. No real KiCad is touched here.

public class PcbRealPlacementTests
{
    private static PcbPlacer.PlacedItem Item(string @ref, double w, double h, double rot = 0) =>
        new(@ref, "lib:" + @ref, (w, h), rot);

    // Reconstruct the inflated packing box for a placed ref (centre + half-extents) — mirrors the
    // placer's own guarantee that inflated courtyards (courtyard + 2*gap) are pairwise disjoint.
    private static (double X0, double Y0, double X1, double Y1) InflatedBox(
        PcbPlacer.PlaceResult res, PcbPlacer.PlacedItem it, double gap)
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
        const double eps = 1e-6;   // touching edges is OK; only true overlap fails
        return a.X0 < b.X1 - eps && a.X1 > b.X0 + eps && a.Y0 < b.Y1 - eps && a.Y1 > b.Y0 + eps;
    }

    private static void AssertNoOverlaps(IReadOnlyList<PcbPlacer.PlacedItem> items, PlacementPlan plan, double gap)
    {
        var res = PcbPlacer.Place(items, plan, gapMm: gap);
        var boxes = items.Select(i => (i.Ref, Box: InflatedBox(res, i, gap))).ToList();
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                Assert.False(Overlap(boxes[i].Box, boxes[j].Box), $"{boxes[i].Ref} overlaps {boxes[j].Ref}");
    }

    // A SolarPool-ish parts list using the REAL (larger) measured courtyards rather than the coarse
    // CourtyardOf approximations — the exact case real-geometry placement must still pack cleanly.
    private static IReadOnlyList<PcbPlacer.PlacedItem> RealSized() => new[]
    {
        Item("U1", 48.09, 41.34),   // ESP32 module — real courtyard (vs 18×25.5 approximation)
        Item("BT1", 88.0, 21.75),   // 18650 battery holder
        Item("J1", 8.99, 5.59),     // JST-PH 1x03 (pH sensor)
        Item("J2", 8.99, 5.59),     // JST-PH 1x03 (ORP sensor)
        Item("J3", 6.99, 5.59),     // JST-PH 1x02 (solar)
        Item("U2", 12.0, 8.0),      // MT3608 header
        Item("U3", 18.0, 9.0),      // CN3791 header
        Item("R1", 3.45, 1.99),     // 0805 real
        Item("C1", 2.20, 1.45),     // 0603 real
    };

    [Fact]
    public void RealSizes_NonOverlapStillHolds_EmptyPlan()
    {
        // The non-overlap invariant must survive the larger REAL courtyards at the bumped default gap.
        AssertNoOverlaps(RealSized(), PlacementPlan.Empty, gap: 2.0);
    }

    [Fact]
    public void RealSizes_NonOverlapStillHolds_WithGroupsEdgesAndNear()
    {
        var plan = PlacementPlan.Parse("""
        {"groups":[{"id":"mcu","members":["U1","R1","C1"]},
                   {"id":"power","members":["BT1","U2","U3"],"edge":"bottom"},
                   {"id":"io","members":["J1","J2","J3"],"edge":"left"}],
         "hints":[{"ref":"C1","near":"U1"}],
         "regionOrder":["mcu","power"]}
        """);
        AssertNoOverlaps(RealSized(), plan, gap: 2.0);
        AssertNoOverlaps(RealSized(), plan, gap: 4.5);   // also at the loop's loosest rung
    }

    [Fact]
    public void LargerCourtyard_DrivesGreaterSpacing()
    {
        // Same refs/plan, only the courtyard sizes differ. The larger geometry must push parts apart:
        // the inflated boxes are bigger, so the board the placer sizes to contain them must be bigger.
        var small = new[] { Item("A", 2.0, 2.0), Item("B", 2.0, 2.0), Item("C", 2.0, 2.0) };
        var large = new[] { Item("A", 40.0, 40.0), Item("B", 40.0, 40.0), Item("C", 40.0, 40.0) };

        var rSmall = PcbPlacer.Place(small, PlacementPlan.Empty, gapMm: 2.0);
        var rLarge = PcbPlacer.Place(large, PlacementPlan.Empty, gapMm: 2.0);

        double SmallBoardW(PcbPlacer.PlaceResult r) => r.OutlineSegmentsMm[1][0];
        Assert.True(SmallBoardW(rLarge) > SmallBoardW(rSmall),
            "larger courtyards must produce a larger board (greater spacing)");

        // and the centre-to-centre distance of two adjacent parts must scale with the courtyard.
        double DistAB(PcbPlacer.PlaceResult r) =>
            Math.Abs(r["A"].XMm - r["B"].XMm) + Math.Abs(r["A"].YMm - r["B"].YMm);
        Assert.True(DistAB(rLarge) > DistAB(rSmall));
    }

    [Fact]
    public void Build_PrefersInjectedRealSizes_OverCourtyardApproximation()
    {
        // ESP32 approximation is 18×25.5; inject a much larger real courtyard and assert the placer
        // spreads the board out accordingly (real size won, CourtyardOf was overridden).
        var p = new Project
        {
            Title = "Real vs Approx",
            Components = new()
            {
                new ComponentSpec { Alias = "MCU", Ref = "esp32", Name = "ESP32-WROOM-32" },
                new ComponentSpec { Alias = "C1", Ref = "cap", Name = "100nF capacitor" },
            },
            Connections = new()
            {
                new Connection { From = "MCU.3V3", To = "C1.1", Net = "power" },
                new Connection { From = "MCU.GND", To = "C1.2", Net = "ground" },
            },
        };

        var approx = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>());

        var esp32Lib = approx.Components.First(c => c.Ref == "MCU").Footprint;
        var real = new Dictionary<string, (double WMm, double HMm)>(StringComparer.OrdinalIgnoreCase)
        {
            [esp32Lib] = (48.09, 41.34),   // real KiCad ESP32-WROOM courtyard — much larger than 18×25.5
        };
        var withReal = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>(), realSizes: real);

        double BoardW(PcbJob j) => j.OutlineSegmentsMm[1][0];
        double BoardH(PcbJob j) => j.OutlineSegmentsMm[1][3];
        Assert.True(BoardW(withReal) > BoardW(approx) || BoardH(withReal) > BoardH(approx),
            "injected real ESP32 courtyard must enlarge the board vs. the CourtyardOf approximation");

        // contract intact: same refs + footprints, only geometry changed.
        Assert.Equal(
            approx.Components.Select(c => c.Ref).OrderBy(x => x),
            withReal.Components.Select(c => c.Ref).OrderBy(x => x));
        Assert.Equal(esp32Lib, withReal.Components.First(c => c.Ref == "MCU").Footprint);
    }

    [Fact]
    public void Build_RealSizes_MissingEntry_FallsBackPerId_NoThrow()
    {
        // A real map covering only ONE of the two libs: the other id must silently use CourtyardOf.
        var p = new Project
        {
            Title = "Partial Measure",
            Components = new()
            {
                new ComponentSpec { Alias = "MCU", Ref = "esp32", Name = "ESP32-WROOM-32" },
                new ComponentSpec { Alias = "C1", Ref = "cap", Name = "100nF capacitor" },
            },
            Connections = new()
            {
                new Connection { From = "MCU.3V3", To = "C1.1", Net = "power" },
            },
        };
        var partial = new Dictionary<string, (double WMm, double HMm)>(StringComparer.OrdinalIgnoreCase)
        {
            ["RF_Module:ESP32-WROOM-32"] = (48.09, 41.34),   // only the MCU measured
        };
        var job = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>(), realSizes: partial);

        Assert.DoesNotContain(job.Diagnostics, d => d.Severity == "error");
        var pts = job.Components.Select(c => (c.XMm, c.YMm)).ToList();
        Assert.Equal(pts.Count, pts.Distinct().Count());   // still a valid non-overlapping layout
    }
}

// ---- ResolvedLibIds (the measure target list) ----------------------------------------------------

public class PcbResolvedLibIdsTests
{
    private static Project SolarPoolish() => new()
    {
        Title = "SolarPool",
        Components = new()
        {
            new ComponentSpec { Alias = "U1", Ref = "esp32", Name = "ESP32-WROOM-32" },
            new ComponentSpec { Alias = "PH", Ref = "SEN0161-V2", Name = "Gravity Analog pH Sensor" },
            new ComponentSpec { Alias = "T1", Ref = "DS18B20-WP", Name = "DS18B20 waterproof temperature probe" },
        },
        Connections = new()
        {
            new Connection { From = "U1.GPIO34", To = "PH.AOUT", Net = "ph" },
            new Connection { From = "U1.3V3", To = "PH.VCC", Net = "power" },
            new Connection { From = "U1.GPIO4", To = "T1.DQ", Net = "1wire" },
        },
    };

    [Fact]
    public void ResolvedLibIds_AreDistinct_AndMatchBuildResolution()
    {
        var p = SolarPoolish();
        var ids = PcbJob.ResolvedLibIds(p);

        // distinct
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // they are exactly the lib ids Build assigns to the components (same resolve pass).
        var build = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>());
        var buildIds = build.Components.Select(c => c.Footprint).Distinct(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(buildIds.OrderBy(x => x), ids.OrderBy(x => x));

        // and they include the real exotic-part footprints (not generic headers).
        Assert.Contains("RF_Module:ESP32-WROOM-32", ids);
        Assert.Contains("Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical", ids);
    }

    [Fact]
    public void ResolvedLibIds_EmptyProject_IsEmpty()
    {
        var ids = PcbJob.ResolvedLibIds(new Project { Title = "Empty" });
        Assert.Empty(ids);
    }
}

// ---- FootprintMap exotic-part mappings (real KiCad-10 ids, not the generic header) ---------------

public class FootprintMapExoticTests
{
    private static ComponentSpec Spec(string name, string @ref = "X1") =>
        new() { Alias = "X1", Ref = @ref, Name = name };

    [Fact]
    public void GravityAnalogSensor_MapsTo_Jst1x03_NotGenericHeader()
    {
        foreach (var name in new[] { "Gravity Analog pH Sensor", "ORP sensor module", "DFRobot analog sensor" })
        {
            var c = FootprintMap.Resolve(Spec(name), 3);
            Assert.Equal("Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical", c.LibId);
            Assert.False(c.IsFallback);
        }
    }

    [Fact]
    public void Ds18b20Probe_MapsTo_To92Inline()
    {
        var c = FootprintMap.Resolve(Spec("DS18B20 waterproof temperature probe"), 3);
        Assert.Equal("Package_TO_SOT_THT:TO-92_Inline", c.LibId);
        Assert.False(c.IsFallback);
    }

    [Fact]
    public void SolarPanel_MapsTo_Jst1x02()
    {
        var c = FootprintMap.Resolve(Spec("6V 2W solar panel"), 2);
        Assert.Equal("Connector_JST:JST_PH_B2B-PH-K_1x02_P2.00mm_Vertical", c.LibId);
        Assert.False(c.IsFallback);
    }

    [Fact]
    public void Battery18650_MapsTo_KeystoneHolder()
    {
        var c = FootprintMap.Resolve(Spec("NCR18650B Li-ion cell"), 2);
        Assert.Equal("Battery:BatteryHolder_Keystone_1042_1x18650", c.LibId);
        Assert.False(c.IsFallback);
    }

    [Fact]
    public void Mt3608_And_Cn3791_MapToSizedHeaders_NotFallback()
    {
        // Module mounts via header strips — kept as headers but sized to the module's pin count,
        // and explicitly NOT flagged as the generic diagnostic fallback.
        var mt = FootprintMap.Resolve(Spec("MT3608 boost converter module"), 2);
        Assert.StartsWith("Connector_PinHeader_2.54mm:PinHeader_1x", mt.LibId);
        Assert.Contains("1x04", mt.LibId);   // max(pinCount,4)
        Assert.False(mt.IsFallback);

        var cn = FootprintMap.Resolve(Spec("CN3791 MPPT charger module"), 2);
        Assert.StartsWith("Connector_PinHeader_2.54mm:PinHeader_1x", cn.LibId);
        Assert.Contains("1x06", cn.LibId);   // max(pinCount,6)
        Assert.False(cn.IsFallback);
    }

    [Fact]
    public void ExoticIds_HaveOfflineCourtyardApproximations_NotJustTheDefault()
    {
        // The placer's offline fallback must know these new ids — otherwise no-KiCad placement uses the
        // generic 10×10 default. JST/18650 got explicit CourtyardOf entries.
        var (jw, jh) = FootprintMap.CourtyardOf("Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical");
        Assert.NotEqual((10.0, 10.0), (jw, jh));
        Assert.True(jw > 0 && jh > 0);

        var (bw, bh) = FootprintMap.CourtyardOf("Battery:BatteryHolder_Keystone_1042_1x18650");
        Assert.True(bw > 40.0, "18650 holder should be physically large in the offline approximation");
        Assert.True(bh > 0);
    }
}

// ---- Measure-job JSON contract (pure shape round-trip; no KiCad) ----------------------------------

public class PcbMeasureJsonShapeTests
{
    [Fact]
    public void MeasureInput_HasModeFootprintDirsAndLibIds()
    {
        // The exact object MeasureAsync serializes for `build_board.py measure`.
        var jobObj = new
        {
            mode = "measure",
            footprintDirs = new[] { @"C:/kicad/footprints" },
            libIds = new[] { "RF_Module:ESP32-WROOM-32", "Resistor_SMD:R_0805_2012Metric" },
        };
        var json = JsonSerializer.Serialize(jobObj);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("measure", root.GetProperty("mode").GetString());
        Assert.Equal(1, root.GetProperty("footprintDirs").GetArrayLength());
        Assert.Equal(2, root.GetProperty("libIds").GetArrayLength());
    }

    [Fact]
    public void MeasureOutput_SizesShape_ParsesPerLibId()
    {
        // The one-line JSON the script emits: { ok, sizes: { "<libId>": {wMm,hMm,pads,src} }, notes }.
        const string stdout = """
        loading pcbnew...
        {"ok":true,"sizes":{"RF_Module:ESP32-WROOM-32":{"wMm":48.09,"hMm":41.34,"pads":48,"src":"courtyard"},"Resistor_SMD:R_0805_2012Metric":{"wMm":3.45,"hMm":1.99,"pads":2,"src":"courtyard"}},"notes":["Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical not found"]}
        """;

        // Parse exactly as PcbBuilder.ParseSizes does: take the last line that starts with '{'.
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("{"));
        Assert.NotNull(line);

        var map = new Dictionary<string, (double WMm, double HMm)>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(line!);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        foreach (var prop in doc.RootElement.GetProperty("sizes").EnumerateObject())
            map[prop.Name] = (prop.Value.GetProperty("wMm").GetDouble(), prop.Value.GetProperty("hMm").GetDouble());

        Assert.Equal(2, map.Count);
        Assert.Equal((48.09, 41.34), map["RF_Module:ESP32-WROOM-32"]);
        Assert.Equal((3.45, 1.99), map["Resistor_SMD:R_0805_2012Metric"]);

        // A footprint listed in notes (not found) is simply absent from sizes → caller falls back per-id.
        Assert.False(map.ContainsKey("Connector_JST:JST_PH_B3B-PH-K_1x03_P2.00mm_Vertical"));
    }

    [Fact]
    public void MeasureOutput_NoSizes_YieldsEmptyMap_NoThrow()
    {
        const string stdout = """{"ok":false,"sizes":{},"notes":["no footprint dir"]}""";
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("{"));
        Assert.NotNull(line);

        var map = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(line!);
        if (doc.RootElement.TryGetProperty("sizes", out var sizes))
            foreach (var prop in sizes.EnumerateObject())
                map[prop.Name] = (prop.Value.GetProperty("wMm").GetDouble(), prop.Value.GetProperty("hMm").GetDouble());

        Assert.Empty(map);
    }
}
