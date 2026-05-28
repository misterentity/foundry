using Foundry.Core.Ai;
using Foundry.Core.Kb;
using Foundry.Core.Pcb;
using Foundry.Core.Project;

namespace Foundry.Tests;

// ---- DrcOptions defaults --------------------------------------------------------------------------

public class DrcOptionsTests
{
    [Fact]
    public void Default_ErrorsOnly_Mm_ThreeIterations()
    {
        var o = DrcOptions.Default;
        Assert.False(o.Strict);
        Assert.Equal("mm", o.Units);
        Assert.Equal(3, o.MaxIterations);
    }

    [Fact]
    public void Custom_OverridesStrictUnitsAndIterations()
    {
        var o = new DrcOptions(Strict: true, Units: "in", MaxIterations: 5);
        Assert.True(o.Strict);
        Assert.Equal("in", o.Units);
        Assert.Equal(5, o.MaxIterations);
    }
}

// ---- DrcReport.NotInstalled / Failed / Clean / DominantClasses -----------------------------------

public class DrcReportFactoryTests
{
    [Fact]
    public void NotInstalled_SurfacesKiCadDownloadGuidance()
    {
        var r = DrcReport.NotInstalled();
        Assert.False(r.Installed);
        Assert.False(r.Ok);
        Assert.False(r.Clean);
        Assert.Empty(r.Violations);
        Assert.Empty(r.Unconnected);
        Assert.Contains(KiCadInstaller.DownloadUrl, r.Summary);
    }

    [Fact]
    public void Failed_IsInstalledButNotOk_CarriesNotes()
    {
        var r = DrcReport.Failed("Couldn't run DRC.", new[] { "boom" });
        Assert.True(r.Installed);
        Assert.False(r.Ok);
        Assert.False(r.Clean);
        Assert.Equal("Couldn't run DRC.", r.Summary);
        Assert.Contains("boom", r.Notes);
    }

    [Fact]
    public void Failed_NullNotes_IsEmpty()
    {
        Assert.Empty(DrcReport.Failed("nope").Notes);
    }

    [Fact]
    public void Clean_RequiresOk_NoErrors_NoUnconnected()
    {
        Assert.True(new DrcReport(true, true, "", Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 0, 3, 0, Array.Empty<string>()).Clean);
        Assert.False(new DrcReport(true, true, "", Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 1, 0, 0, Array.Empty<string>()).Clean);
        Assert.False(new DrcReport(true, true, "", Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 0, 0, 2, Array.Empty<string>()).Clean);
        Assert.False(new DrcReport(true, false, "", Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 0, 0, 0, Array.Empty<string>()).Clean);
    }

    [Fact]
    public void DominantClasses_MostFrequentErrorFirst_FoldsUnconnected()
    {
        DrcViolation Err(string type) => new(type, "error", "", false, Array.Empty<DrcItem>());
        var report = new DrcReport(true, true, "",
            new[] { Err("clearance"), Err("clearance"), Err("copper_edge_clearance") },
            new[] { new DrcViolation("unconnected_items", "error", "", false, Array.Empty<DrcItem>()) },
            3, 0, 1, Array.Empty<string>());

        var classes = report.DominantClasses();
        Assert.Equal("clearance", classes[0]);          // two of them
        Assert.Contains("copper_edge_clearance", classes);
        Assert.Contains("unconnected_items", classes);   // folded from the Unconnected list
    }

    [Fact]
    public void DominantClasses_IgnoresWarningsAndExcluded()
    {
        var report = new DrcReport(true, true, "",
            new[]
            {
                new DrcViolation("clearance", "warning", "", false, Array.Empty<DrcItem>()),
                new DrcViolation("silk_overlap", "error", "", true, Array.Empty<DrcItem>()),  // excluded
                new DrcViolation("hole_clearance", "error", "", false, Array.Empty<DrcItem>()),
            },
            Array.Empty<DrcViolation>(), 1, 1, 0, Array.Empty<string>());

        var classes = report.DominantClasses();
        Assert.Equal(new[] { "hole_clearance" }, classes);
    }
}

// ---- DrcReport.Parse — real kicad-cli pcb drc JSON shapes ----------------------------------------

public class DrcReportParseTests
{
    [Fact]
    public void Parse_CleanBoard_ExitZero_EmptyArrays_IsClean()
    {
        var json = "{\"violations\":[],\"unconnected_items\":[],\"schematic_parity\":[]}";
        var r = DrcReport.Parse(json, 0, null);

        Assert.True(r.Installed);
        Assert.True(r.Ok);
        Assert.True(r.Clean);
        Assert.Equal(0, r.ErrorCount);
        Assert.Equal(0, r.UnconnectedCount);
        Assert.Empty(r.Violations);
        Assert.Contains("clean", r.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ExitZero_NoReportFile_ReconciledToClean()
    {
        var r = DrcReport.Parse(null, 0, null);
        Assert.True(r.Ok);
        Assert.True(r.Clean);
    }

    [Fact]
    public void Parse_ClearanceAndUnconnected_ParsedWithSeverityAndLocation()
    {
        var json = """
        {
          "violations": [
            {"type":"clearance","severity":"error","description":"Clearance violation (0.15 mm)",
             "items":[{"uuid":"a","description":"Pad 1 of C3","pos":{"x":12.5,"y":7.0}},
                      {"uuid":"b","description":"Pad 2 of R4","pos":{"x":13.0,"y":7.0}}]}
          ],
          "unconnected_items": [
            {"type":"unconnected_items","severity":"error","description":"Missing connection: SDA",
             "items":[{"uuid":"c","description":"Pad 5 of U1","pos":{"x":1.0,"y":2.0}}]}
          ]
        }
        """;
        var r = DrcReport.Parse(json, 5, null);

        Assert.True(r.Installed);
        Assert.True(r.Ok);
        Assert.False(r.Clean);
        Assert.Equal(1, r.ErrorCount);
        Assert.Equal(1, r.UnconnectedCount);

        var v = Assert.Single(r.Violations);
        Assert.Equal("clearance", v.Type);
        Assert.Equal("error", v.Severity);
        Assert.Equal(2, v.Items.Count);
        Assert.NotNull(v.Location);
        Assert.Equal(12.5, v.Location!.Value.X);
        Assert.Equal(7.0, v.Location.Value.Y);

        var u = Assert.Single(r.Unconnected);
        Assert.Equal("unconnected_items", u.Type);
        Assert.Contains("net(s) unconnected", r.Summary);
    }

    [Fact]
    public void Parse_FoldsUnconnectedItemsTypeOutOfViolationsArray()
    {
        // Some KiCad builds emit unconnected nets inside violations[] as type "unconnected_items".
        var json = """
        {
          "violations": [
            {"type":"clearance","severity":"error","description":"","items":[]},
            {"type":"unconnected_items","severity":"error","description":"Missing connection","items":[]}
          ],
          "unconnected_items": []
        }
        """;
        var r = DrcReport.Parse(json, 5, null);

        Assert.Equal(1, r.ErrorCount);          // only the clearance counts as a generic error
        Assert.Equal(1, r.UnconnectedCount);    // the folded one became connectivity
        Assert.Equal("clearance", Assert.Single(r.Violations).Type);
        Assert.Equal("unconnected_items", Assert.Single(r.Unconnected).Type);
    }

    [Fact]
    public void Parse_ExcludedEntries_DoNotCountTowardGate()
    {
        var json = """
        {
          "violations": [
            {"type":"silk_overlap","severity":"error","description":"","excluded":true,"items":[]},
            {"type":"clearance","severity":"error","description":"","items":[]}
          ],
          "unconnected_items": []
        }
        """;
        var r = DrcReport.Parse(json, 5, null);
        Assert.Equal(1, r.ErrorCount);   // the excluded one is filtered out
        Assert.False(r.Clean);
    }

    [Fact]
    public void Parse_WarningsOnly_IsCleanButReported()
    {
        var json = """
        {"violations":[{"type":"courtyards_overlap","severity":"warning","description":"","items":[]}],
         "unconnected_items":[]}
        """;
        var r = DrcReport.Parse(json, 5, null);
        Assert.True(r.Clean);            // warnings don't gate by default
        Assert.Equal(0, r.ErrorCount);
        Assert.Equal(1, r.WarningCount);
        Assert.Contains("warning", r.Summary);
    }

    [Fact]
    public void Parse_InfraExitCode_IsFailureWithStderrNote()
    {
        var r = DrcReport.Parse(null, 2, "kicad-cli: cannot open board");
        Assert.True(r.Installed);
        Assert.False(r.Ok);
        Assert.False(r.Clean);
        Assert.Contains(r.Notes, n => n.Contains("cannot open board"));
    }

    [Fact]
    public void Parse_GarbageJson_OnViolationsExit_IsFailure_NeverThrows()
    {
        var r = DrcReport.Parse("not json at all {{{", 5, null);
        Assert.False(r.Ok);
        Assert.False(r.Clean);
    }
}

// ---- PcbDrc.BuildArgs (pure) + degradation -------------------------------------------------------

public class PcbDrcTests
{
    [Fact]
    public void BuildArgs_Default_EmitsJsonReportSeverityErrorAndExitCodeViolations()
    {
        var args = PcbDrc.BuildArgs("C:/tmp/board.kicad_pcb", "C:/tmp/board.drc.json");
        Assert.Contains("pcb drc", args);
        Assert.Contains("--format json", args);
        Assert.Contains("--output \"C:/tmp/board.drc.json\"", args);
        Assert.Contains("--severity-error", args);
        Assert.Contains("--exit-code-violations", args);
        Assert.Contains("\"C:/tmp/board.kicad_pcb\"", args);
        Assert.DoesNotContain("--severity-warning", args);   // not strict
        Assert.DoesNotContain("--units", args);              // default mm
    }

    [Fact]
    public void BuildArgs_Strict_AddsSeverityWarning()
    {
        var args = PcbDrc.BuildArgs("b.kicad_pcb", "r.json", new DrcOptions(Strict: true));
        Assert.Contains("--severity-warning", args);
    }

    [Fact]
    public void BuildArgs_NonMmUnits_AddsUnitsFlag_MmStaysImplicit()
    {
        Assert.Contains("--units in", PcbDrc.BuildArgs("b", "r", new DrcOptions(Units: "in")));
        Assert.DoesNotContain("--units", PcbDrc.BuildArgs("b", "r", new DrcOptions(Units: "mm")));
    }

    [Fact]
    public async Task CheckAsync_ReturnsNotInstalled_WhenKiCadAbsent()
    {
        // KiCad isn't installed here — assert graceful degradation, never a throw.
        if (KiCadInstaller.Locate() is not null) return;   // guard: real install present, skip

        var tmp = Path.Combine(Path.GetTempPath(), "foundry_drc_in_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        try
        {
            var r = await PcbDrc.CheckAsync(tmp);
            Assert.False(r.Installed);
            Assert.False(r.Ok);
            Assert.False(r.Clean);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task CheckAsync_NotInstalled_TakesPrecedenceOverMissingInput()
    {
        if (KiCadInstaller.Locate() is not null) return;
        var r = await PcbDrc.CheckAsync("Z:/does/not/exist.kicad_pcb");
        Assert.False(r.Installed);
        Assert.False(r.Ok);
    }
}

// ---- PcbDesigner knob bump schedule (pure) -------------------------------------------------------

public class PcbDesignerKnobsTests
{
    [Fact]
    public void Knobs_Initial_AreTheV22Defaults()
    {
        var k = PcbDesigner.Knobs.Initial;
        Assert.Equal(1.5, k.GapMm);
        Assert.Equal(5.0, k.MarginMm);
        Assert.Equal(10, k.Passes);
    }

    [Fact]
    public void NextGap_Climbs_1p5_2p5_4p0_ThenSaturates()
    {
        Assert.Equal(2.5, PcbDesigner.NextGap(1.5));
        Assert.Equal(4.0, PcbDesigner.NextGap(2.5));
        Assert.Equal(4.0, PcbDesigner.NextGap(4.0));   // saturated
    }

    [Fact]
    public void NextMargin_Climbs_5_7_10_ThenSaturates()
    {
        Assert.Equal(7.0, PcbDesigner.NextMargin(5.0));
        Assert.Equal(10.0, PcbDesigner.NextMargin(7.0));
        Assert.Equal(10.0, PcbDesigner.NextMargin(10.0));
    }

    [Fact]
    public void NextPasses_Climbs_10_20_40_ThenSaturates()
    {
        Assert.Equal(20, PcbDesigner.NextPasses(10));
        Assert.Equal(40, PcbDesigner.NextPasses(20));
        Assert.Equal(40, PcbDesigner.NextPasses(40));
    }
}

// ---- PcbDesigner.RunLoopAsync — the pure fix-loop control flow with injected fakes ---------------

public class PcbDesignerLoopTests
{
    // ---- fakes -------------------------------------------------------------------------------------

    private static PcbResult Built(string path) => new(true, true, "built", path, Array.Empty<string>());
    private static RouteResult Routed(string path) => new(true, true, "routed", path, 10, 0, 0, true, Array.Empty<string>());

    private static DrcReport Clean() =>
        new(true, true, "DRC clean — 0 errors, fully connected.",
            Array.Empty<DrcViolation>(), Array.Empty<DrcViolation>(), 0, 0, 0, Array.Empty<string>());

    private static DrcReport WithErrors(int errors, params string[] classes)
    {
        var v = classes.Select(c => new DrcViolation(c, "error", "", false, Array.Empty<DrcItem>())).ToList();
        return new DrcReport(true, true, $"DRC found {errors} error(s).", v, Array.Empty<DrcViolation>(),
            Math.Max(errors, v.Count), 0, 0, Array.Empty<string>());
    }

    private static DrcReport WithUnconnected(int n) =>
        new(true, true, $"DRC found {n} net(s) unconnected.",
            Array.Empty<DrcViolation>(),
            new[] { new DrcViolation("unconnected_items", "error", "", false, Array.Empty<DrcItem>()) },
            0, 0, n, Array.Empty<string>());

    // Build/route steps that just echo a deterministic path so the loop has something to carry.
    private static PcbDesigner.BuildStep BuildOk() =>
        (plan, knobs, ct) => Task.FromResult(Built($"board_g{knobs.GapMm}_m{knobs.MarginMm}.kicad_pcb"));
    private static PcbDesigner.RouteStep RouteOk() =>
        (built, passes, ct) => Task.FromResult(Routed("routed_" + built));
    private static PcbDesigner.ReviseStep NoRevise() =>
        (plan, viol, ct) => Task.FromResult(plan);

    [Fact]
    public async Task Loop_StopsImmediately_WhenFirstDrcIsClean()
    {
        int drcCalls = 0;
        PcbDesigner.DrcStep drc = (board, ct) => { drcCalls++; return Task.FromResult(Clean()); };

        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), RouteOk(), drc, NoRevise(), maxIterations: 3);

        Assert.True(r.Installed);
        Assert.True(r.Ok);
        Assert.Equal(1, r.Iterations);
        Assert.Equal(1, drcCalls);
        Assert.NotNull(r.KicadPcbPath);
        Assert.Contains("clean", r.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Loop_RemediatesThenPasses_WithinIterationBudget()
    {
        // Dirty (clearance) on attempt 1, clean on attempt 2 after the deterministic gap bump.
        int call = 0;
        PcbDesigner.DrcStep drc = (board, ct) =>
            Task.FromResult(++call == 1 ? WithErrors(1, "clearance") : Clean());

        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), RouteOk(), drc, NoRevise(), maxIterations: 3);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Iterations);
        Assert.Equal(2, call);
    }

    [Fact]
    public async Task Loop_AppliesDeterministicGapBump_ForClearanceClass()
    {
        // Record the knobs the build step sees so we can assert the gap bumped between iterations.
        var gaps = new List<double>();
        PcbDesigner.BuildStep build = (plan, knobs, ct) =>
        {
            gaps.Add(knobs.GapMm);
            return Task.FromResult(Built("b.kicad_pcb"));
        };
        int call = 0;
        PcbDesigner.DrcStep drc = (board, ct) =>
            Task.FromResult(++call == 1 ? WithErrors(2, "clearance") : Clean());

        await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, build, RouteOk(), drc, NoRevise(), maxIterations: 3);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(1.5, gaps[0]);   // initial
        Assert.Equal(2.5, gaps[1]);   // bumped for clearance
    }

    [Fact]
    public async Task Loop_AppliesMarginBump_ForEdgeClass()
    {
        var margins = new List<double>();
        PcbDesigner.BuildStep build = (plan, knobs, ct) =>
        {
            margins.Add(knobs.MarginMm);
            return Task.FromResult(Built("b.kicad_pcb"));
        };
        int call = 0;
        PcbDesigner.DrcStep drc = (board, ct) =>
            Task.FromResult(++call == 1 ? WithErrors(1, "copper_edge_clearance") : Clean());

        await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, build, RouteOk(), drc, NoRevise(), maxIterations: 3);

        Assert.Equal(5.0, margins[0]);
        Assert.Equal(7.0, margins[1]);   // bumped for edge class
    }

    [Fact]
    public async Task Loop_AppliesPassesBump_ForConnectivityClass()
    {
        var passes = new List<int>();
        PcbDesigner.RouteStep route = (built, p, ct) =>
        {
            passes.Add(p);
            return Task.FromResult(Routed("r.kicad_pcb"));
        };
        int call = 0;
        PcbDesigner.DrcStep drc = (board, ct) =>
            Task.FromResult(++call == 1 ? WithUnconnected(2) : Clean());

        await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), route, drc, NoRevise(), maxIterations: 3);

        Assert.Equal(10, passes[0]);
        Assert.Equal(20, passes[1]);   // bumped for unconnected
    }

    [Fact]
    public async Task Loop_RespectsMaxIterations_WhenNeverClean()
    {
        int call = 0;
        PcbDesigner.DrcStep drc = (board, ct) => { call++; return Task.FromResult(WithErrors(1, "clearance")); };

        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), RouteOk(), drc, NoRevise(), maxIterations: 3);

        Assert.False(r.Ok);
        Assert.Equal(3, r.Iterations);
        Assert.Equal(3, call);
        Assert.Contains("Best of 3", r.Summary);
    }

    [Fact]
    public async Task Loop_KeepsBestBoard_WhenItNeverFullyPasses()
    {
        // 3 errors, then 1 error (best), then 2 errors — the best (1-error) board must win.
        var reports = new[] { WithErrors(3, "clearance"), WithErrors(1, "clearance"), WithErrors(2, "clearance") };
        int i = 0;
        PcbDesigner.BuildStep build = (plan, knobs, ct) => Task.FromResult(Built($"attempt_{i + 1}.kicad_pcb"));
        PcbDesigner.DrcStep drc = (board, ct) => Task.FromResult(reports[i++]);

        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, build, RouteOk(), drc, NoRevise(), maxIterations: 3);

        Assert.False(r.Ok);
        Assert.NotNull(r.Report);
        Assert.Equal(1, r.Report!.ErrorCount);                 // the fewest-error board was kept
        Assert.Equal("routed_attempt_2.kicad_pcb", r.KicadPcbPath);
    }

    [Fact]
    public async Task Loop_CallsAiRevision_OnlyWhenStuck_OnClearanceCongestion()
    {
        // Clearance persists every attempt → after attempt 2 the loop asks the AI to revise the plan.
        int reviseCalls = 0;
        PcbDesigner.ReviseStep revise = (plan, viol, ct) => { reviseCalls++; return Task.FromResult(plan); };
        PcbDesigner.DrcStep drc = (board, ct) => Task.FromResult(WithErrors(1, "clearance"));

        await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), RouteOk(), drc, revise, maxIterations: 3);

        // attempt 1 bumps the gap (no revision); attempt 2 is stuck-on-clearance → exactly one revision.
        Assert.Equal(1, reviseCalls);
    }

    [Fact]
    public async Task Loop_DoesNotCallAiRevision_WhenADeterministicBumpResolvesIt()
    {
        int reviseCalls = 0;
        PcbDesigner.ReviseStep revise = (plan, viol, ct) => { reviseCalls++; return Task.FromResult(plan); };
        int call = 0;
        PcbDesigner.DrcStep drc = (board, ct) =>
            Task.FromResult(++call == 1 ? WithErrors(1, "clearance") : Clean());

        await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), RouteOk(), drc, revise, maxIterations: 3);

        Assert.Equal(0, reviseCalls);
    }

    [Fact]
    public async Task Loop_DegradesToNotInstalled_WhenBuildIsNotInstalled()
    {
        PcbDesigner.BuildStep build = (plan, knobs, ct) => Task.FromResult(PcbResult.NotInstalled());
        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, build, RouteOk(),
            (b, c) => Task.FromResult(Clean()), NoRevise(), maxIterations: 3);
        Assert.False(r.Installed);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Loop_DegradesToNotInstalled_WhenRouterIsNotInstalled()
    {
        PcbDesigner.RouteStep route = (built, passes, ct) => Task.FromResult(RouteResult.NotInstalled());
        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), route,
            (b, c) => Task.FromResult(Clean()), NoRevise(), maxIterations: 3);
        Assert.False(r.Installed);
    }

    [Fact]
    public async Task Loop_DegradesToNotInstalled_WhenDrcIsNotInstalled()
    {
        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), RouteOk(),
            (b, c) => Task.FromResult(DrcReport.NotInstalled()), NoRevise(), maxIterations: 3);
        Assert.False(r.Installed);
    }

    [Fact]
    public async Task Loop_BuildFailure_ReturnsFailedWithTrace()
    {
        PcbDesigner.BuildStep build = (plan, knobs, ct) => Task.FromResult(PcbResult.Failed("footprint missing"));
        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, build, RouteOk(),
            (b, c) => Task.FromResult(Clean()), NoRevise(), maxIterations: 3);
        Assert.True(r.Installed);
        Assert.False(r.Ok);
        Assert.Contains("footprint missing", r.Summary);
        Assert.NotEmpty(r.Trace);
    }

    [Fact]
    public async Task Loop_FallsBackToBuiltBoard_WhenRoutingPartiallyFails()
    {
        // Router returns Installed but not Ok → the loop DRCs the placed (built) board instead.
        var seen = new List<string>();
        PcbDesigner.RouteStep route = (built, passes, ct) =>
            Task.FromResult(RouteResult.Failed("router crashed"));
        PcbDesigner.DrcStep drc = (board, ct) => { seen.Add(board); return Task.FromResult(Clean()); };

        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), route, drc, NoRevise(), maxIterations: 1);

        Assert.True(r.Ok);
        Assert.DoesNotContain(seen, s => s.StartsWith("routed_"));   // the built board was checked, not a routed one
    }

    [Fact]
    public async Task Loop_ClampsMaxIterationsToAtLeastOne()
    {
        int call = 0;
        PcbDesigner.DrcStep drc = (board, ct) => { call++; return Task.FromResult(WithErrors(1, "clearance")); };
        var r = await PcbDesigner.RunLoopAsync(PlacementPlan.Empty, BuildOk(), RouteOk(), drc, NoRevise(), maxIterations: 0);
        Assert.Equal(1, call);
        Assert.Equal(1, r.Iterations);
    }
}

// ---- PcbPlanner.RevisePlanAsync — fenced AI revision via FakeAi fixture (no live key) ------------

public class PcbPlannerRevisionTests
{
    private sealed class FakeAi : IAnthropicClient
    {
        private readonly string _json;
        private readonly bool _hasKey;
        public int Calls { get; private set; }
        public FakeAi(string json, bool hasKey = true) { _json = json; _hasKey = hasKey; }
        public bool HasKey => _hasKey;
        public Task<ModelListResult> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModelListResult(true, ModelCatalog.Fallback, null));
        public Task<string> CompleteAsync(string s, string u, string m, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_json);
        }
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

    private static IReadOnlyList<DrcViolation> Clearance() => new[]
    {
        new DrcViolation("clearance", "error", "Clearance violation",
            false, new[] { new DrcItem("u", "Pad 1 of C1", 1, 2) }),
    };

    [Fact]
    public async Task RevisePlanAsync_ReturnsRevisedPlan_FromValidJson()
    {
        var json = """
        {"groups":[{"id":"power","members":["U1","C1"],"edge":"none"}],
         "hints":[{"ref":"C1","near":"U1"}],
         "regionOrder":["power"]}
        """;
        var ai = new FakeAi(json);
        var planner = new PcbPlanner(ai);

        var revised = await planner.RevisePlanAsync(MiniProject(), PlacementPlan.Empty, Clearance());

        Assert.Equal(1, ai.Calls);
        Assert.Single(revised.Groups);
        Assert.Equal("power", revised.Groups[0].Id);
        Assert.Single(revised.Hints);
        Assert.Equal("U1", revised.Hints[0].NearRef);
    }

    [Fact]
    public async Task RevisePlanAsync_NoKey_KeepsCurrentPlan_AndDoesNotCallModel()
    {
        var current = new PlacementPlan(
            new[] { new PlacementGroup("mcu", new[] { "U1" }) },
            Array.Empty<PlacementHint>(), Array.Empty<string>());
        var ai = new FakeAi("{}", hasKey: false);
        var planner = new PcbPlanner(ai);

        var result = await planner.RevisePlanAsync(MiniProject(), current, Clearance());

        Assert.Same(current, result);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task RevisePlanAsync_GarbageReply_KeepsCurrentPlan()
    {
        var current = new PlacementPlan(
            new[] { new PlacementGroup("mcu", new[] { "U1" }) },
            Array.Empty<PlacementHint>(), Array.Empty<string>());
        var planner = new PcbPlanner(new FakeAi("not json at all"));

        var result = await planner.RevisePlanAsync(MiniProject(), current, Clearance());

        Assert.Same(current, result);
    }

    [Fact]
    public async Task RevisePlanAsync_EmptyParse_KeepsCurrentPlan()
    {
        // Valid JSON but no usable groups/hints/regionOrder → PlacementPlan.Empty → keep current.
        var current = new PlacementPlan(
            new[] { new PlacementGroup("mcu", new[] { "U1" }) },
            Array.Empty<PlacementHint>(), Array.Empty<string>());
        var planner = new PcbPlanner(new FakeAi("{\"groups\":[],\"hints\":[],\"regionOrder\":[]}"));

        var result = await planner.RevisePlanAsync(MiniProject(), current, Clearance());

        Assert.Same(current, result);
    }

    [Fact]
    public async Task RevisePlanAsync_NullCurrentPlan_DegradesToEmpty_NeverThrows()
    {
        var planner = new PcbPlanner(new FakeAi("garbage", hasKey: false));
        var result = await planner.RevisePlanAsync(MiniProject(), null!, Clearance());
        Assert.Same(PlacementPlan.Empty, result);
    }
}
