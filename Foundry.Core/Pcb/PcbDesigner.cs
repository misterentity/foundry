using Foundry.Core.Ai;
using Foundry.Core.Diagnostics;
using Foundry.Core.Pcb.Fab;

namespace Foundry.Core.Pcb;

/// <summary>
/// Result of the v2.5 build→route→DRC fix loop (<see cref="PcbDesigner.DesignAsync"/>) — mirrors
/// <see cref="RouteResult"/>'s Installed/Ok/Summary shape. <see cref="Ok"/> means the DRC gate PASSED
/// (a board that passes DRC — the credibility milestone). <see cref="KicadPcbPath"/> is the BEST board
/// seen (fewest errors, then fewest unconnected) even when the loop exhausts without a clean pass, so a
/// partial improvement is never thrown away. <see cref="Trace"/> records one line per iteration.
/// </summary>
public sealed record PcbDesignResult(
    bool Installed,
    bool Ok,
    string Summary,
    string? KicadPcbPath,
    DrcReport? Report,
    int Iterations,
    IReadOnlyList<string> Trace,
    IReadOnlyList<string> Notes)
{
    public static PcbDesignResult NotInstalled(string summary) =>
        new(false, false, summary, null, null, 0, Array.Empty<string>(), Array.Empty<string>());

    public static PcbDesignResult Failed(string summary, IEnumerable<string>? trace = null, IEnumerable<string>? notes = null) =>
        new(true, false, summary, null, null, 0, (trace ?? Array.Empty<string>()).ToArray(), (notes ?? Array.Empty<string>()).ToArray());
}

/// <summary>
/// Orchestrates the v2.5 DRC fix loop — the PCB analogue of
/// <see cref="Foundry.Core.Generation.ProjectGenerator.FixFirmwareAsync"/>: a bounded loop of
/// build→route→DRC, bumping a deterministic knob (gap/margin/router passes) matching the dominant
/// violation class each iteration, falling back to a FENCED AI plan revision when a bump can't help or
/// congestion persists, keeping the BEST board, returning Passed / Exhausted / NotInstalled. The AI never
/// sees or emits geometry — the deterministic placer/router still own every coordinate.
///
/// The loop's build/route/DRC/plan-revision steps are injected as delegates so tests can drive the exact
/// control flow with fakes (no KiCad/Java/AI). The default <see cref="DesignAsync"/> wires the real
/// <see cref="PcbBuilder"/>/<see cref="PcbRouter"/>/<see cref="PcbDrc"/>/<see cref="PcbPlanner"/>.
/// </summary>
public static class PcbDesigner
{
    // Deterministic bump schedule (monotonic — each step strictly loosens). See spec §D.
    // Gap0 raised 1.5 → 2.0 (real-geometry placement headroom); rungs kept strictly increasing.
    private const double Gap0 = 2.0, Gap1 = 3.0, Gap2 = 4.5;
    private const double Margin0 = 5.0, Margin1 = 7.0, Margin2 = 10.0;
    private const int Passes0 = 10, Passes1 = 20, Passes2 = 40;

    public static double NextGap(double g) => g < Gap1 - 1e-9 ? Gap1 : g < Gap2 - 1e-9 ? Gap2 : Gap2;
    public static double NextMargin(double m) => m < Margin1 - 1e-9 ? Margin1 : m < Margin2 - 1e-9 ? Margin2 : Margin2;
    public static int NextPasses(int p) => p < Passes1 ? Passes1 : p < Passes2 ? Passes2 : Passes2;

    /// <summary>Knobs the loop adjusts between iterations.</summary>
    public readonly record struct Knobs(double GapMm, double MarginMm, int Passes)
    {
        public static Knobs Initial => new(Gap0, Margin0, Passes0);
    }

    /// <summary>Builds + places a board for the given plan + knobs, returning the placed .kicad_pcb.</summary>
    public delegate Task<PcbResult> BuildStep(PlacementPlan plan, Knobs knobs, CancellationToken ct);

    /// <summary>Routes a built board with the given pass count, returning the routed .kicad_pcb.</summary>
    public delegate Task<RouteResult> RouteStep(string builtPcbPath, int passes, CancellationToken ct);

    /// <summary>DRCs a routed board, returning the gate report.</summary>
    public delegate Task<DrcReport> DrcStep(string boardPath, CancellationToken ct);

    /// <summary>Fenced AI plan revision given the current plan + the gate's violations.</summary>
    public delegate Task<PlacementPlan> ReviseStep(PlacementPlan plan, IReadOnlyList<DrcViolation> violations, CancellationToken ct);

    /// <summary>
    /// Run the real fix loop for <paramref name="project"/>, writing the board(s) under
    /// <paramref name="outputDir"/>. AI placement (initial plan + revisions) is opt-in: keyed
    /// <paramref name="ai"/> → ask for a plan; otherwise the deterministic tidy-grid default is used.
    /// Degrades to <see cref="PcbDesignResult.NotInstalled"/> when KiCad (or the router toolchain) is absent.
    /// </summary>
    public static async Task<PcbDesignResult> DesignAsync(Project.Project project, string outputDir,
        IAnthropicClient? ai = null, string? model = null, DrcOptions? options = null, CancellationToken ct = default)
    {
        options ??= DrcOptions.Default;

        if (KiCadInstaller.Locate() is null)
            return PcbDesignResult.NotInstalled(DrcReport.NotInstalled().Summary);

        var planner = ai is not null ? new PcbPlanner(ai, model) : null;
        PlacementPlan initialPlan = ai is { HasKey: true } && planner is not null
            ? await planner.PlanAsync(project, ct)
            : PlacementPlan.Empty;

        // Measure real footprint geometry ONCE and reuse across every re-place iteration — geometry
        // doesn't change between iterations, only gap/margin do. Avoids re-spawning python each loop.
        var footprintDirs = System.IO.Directory.Exists(KiCadInstaller.Locate()!.FootprintDir)
            ? new[] { KiCadInstaller.Locate()!.FootprintDir }
            : Array.Empty<string>();
        var realSizes = await PcbBuilder.MeasureAsync(project, footprintDirs, ct);

        // Note: PcbBuilder.BuildAsync re-runs PlanAsync internally when handed a keyed client. To keep the
        // loop in control of the plan (so revisions take effect), the build step builds against the job with
        // the loop's current plan and never re-asks the AI for placement.
        BuildStep build = (plan, knobs, c) => PcbBuilder.BuildAsync(project, outputDir, plan, knobs.MarginMm, knobs.GapMm, realSizes, c);
        RouteStep route = (built, passes, c) => PcbRouter.RouteAsync(built, new RouteOptions(passes), c);
        DrcStep drc = (board, c) => PcbDrc.CheckAsync(board, options, c);
        ReviseStep revise = planner is not null
            ? (plan, viol, c) => planner.RevisePlanAsync(project, plan, viol, c)
            : (plan, _, _) => Task.FromResult(plan);

        return await RunLoopAsync(initialPlan, build, route, drc, revise, options.MaxIterations, ct);
    }

    /// <summary>
    /// End-to-end v2.6 capstone: run the v2.5 fix loop to a DRC-clean board, then export the fab file set and
    /// bundle it into a single <c>&lt;name&gt;-fab.zip</c> under <paramref name="outputDir"/>. Returns the
    /// <see cref="PcbDesignResult"/> plus the <see cref="FabExportResult"/>. The export only runs when the
    /// design passed (a DRC-clean board is the contract for fab); when the design didn't pass, the fab result
    /// mirrors the design's degrade (NotInstalled vs Failed) so callers see one consistent story. Prior entry
    /// points (<see cref="DesignAsync"/>/<see cref="RunLoopAsync"/>) are left intact.
    /// </summary>
    public static async Task<(PcbDesignResult Design, FabExportResult Fab)> DesignAndExportFabAsync(
        Project.Project project, string outputDir, IAnthropicClient? ai = null, string? model = null,
        DrcOptions? options = null, FabOptions? fabOptions = null, CancellationToken ct = default)
    {
        var design = await DesignAsync(project, outputDir, ai, model, options, ct);
        if (!design.Ok || string.IsNullOrEmpty(design.KicadPcbPath))
        {
            var fab = design.Installed
                ? FabExportResult.Failed($"No DRC-clean board to export — {design.Summary}")
                : FabExportResult.NotInstalled();
            return (design, fab);
        }

        // Named args: design.Ok is true only when the kept board's DRC report was Clean (RunLoopAsync), so the
        // orchestrator passes drcClean:true to skip a redundant kicad-cli DRC run inside the exporter.
        var fabResult = await GerberExporter.ExportAsync(design.KicadPcbPath!, outputDir, fabOptions,
            drcClean: true, drcOptions: options, ct: ct);
        return (design, fabResult);
    }

    /// <summary>
    /// The pure control flow — bounded loop over the injected steps, deterministic-bump-first then
    /// AI-revision-if-stuck, keeping the best (fewest-error, then fewest-unconnected) board. Used directly by
    /// tests with fake steps; <see cref="DesignAsync"/> wires the real ones. Never throws on the normal
    /// degrade paths.
    /// </summary>
    public static async Task<PcbDesignResult> RunLoopAsync(
        PlacementPlan initialPlan, BuildStep build, RouteStep route, DrcStep drc, ReviseStep revise,
        int maxIterations = 3, CancellationToken ct = default)
    {
        maxIterations = Math.Max(1, maxIterations);
        var plan = initialPlan ?? PlacementPlan.Empty;
        var knobs = Knobs.Initial;
        var trace = new List<string>();

        string? bestPath = null;
        DrcReport? bestReport = null;
        int bestAttempt = 0;
        int? lastErrorCount = null;   // carried across iterations to show the "19 → N errors" delta

        for (int attempt = 1; attempt <= maxIterations; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var built = await build(plan, knobs, ct);
            if (!built.Installed) return PcbDesignResult.NotInstalled(built.Summary);
            // Connectivity gate: a named footprint with an unmatched net pin means the board may be mis-wired.
            // Refuse to route/DRC/export it — a confidently-wrong fab board is worse than no board.
            if (built.UnmappedPins.Count > 0)
            {
                trace.Add($"attempt {attempt}: connectivity unverified — {built.UnmappedPins.Count} unmapped pin(s)");
                return PcbDesignResult.Failed(
                    $"Connectivity unverified ({built.UnmappedPins.Count} unmapped pin(s)) — not routing or exporting a board that may be mis-wired.",
                    trace, built.UnmappedPins);
            }
            if (!built.Ok || string.IsNullOrEmpty(built.KicadPcbPath))
            {
                trace.Add($"attempt {attempt}: build failed — {built.Summary}");
                return PcbDesignResult.Failed($"PCB build failed: {built.Summary}", trace, built.Notes);
            }

            var routed = await route(built.KicadPcbPath!, knobs.Passes, ct);
            if (!routed.Installed) return PcbDesignResult.NotInstalled(routed.Summary);
            var boardForDrc = routed.Ok && !string.IsNullOrEmpty(routed.RoutedPcbPath)
                ? routed.RoutedPcbPath!
                : built.KicadPcbPath!;

            var report = await drc(boardForDrc, ct);
            if (!report.Installed) return PcbDesignResult.NotInstalled(report.Summary);

            if (IsBetter(report, bestReport))
            {
                bestReport = report;
                bestPath = boardForDrc;
                bestAttempt = attempt;
            }

            // Surface the error-count delta vs the previous iteration ("19 → 4 errors") so progress is legible.
            var errs = report.ErrorCount + report.UnconnectedCount;
            var delta = lastErrorCount is { } prev && prev != errs ? $" [{prev} → {errs} errors]" : "";
            trace.Add($"attempt {attempt}: gap={knobs.GapMm:0.#} margin={knobs.MarginMm:0.#} passes={knobs.Passes} → {report.Summary}{delta}");
            lastErrorCount = errs;

            if (report.Clean)
            {
                var summary = $"DRC clean on attempt {attempt} — {report.Summary}";
                return new PcbDesignResult(true, true, summary, boardForDrc, report, attempt, trace,
                    report.Notes.ToArray());
            }

            if (attempt == maxIterations) break;   // no point remediating past the last iteration

            // ---- remediate for the next iteration: deterministic bump first ----
            var classes = report.DominantClasses();
            bool bumped = false;
            var next = knobs;

            if (classes.Any(IsEdgeClass)) { next = next with { MarginMm = NextMargin(next.MarginMm) }; bumped |= next.MarginMm > knobs.MarginMm; }
            if (classes.Any(IsClearanceClass)) { next = next with { GapMm = NextGap(next.GapMm) }; bumped |= next.GapMm > knobs.GapMm; }
            if (classes.Any(IsConnectivityClass)) { next = next with { Passes = NextPasses(next.Passes) }; bumped |= next.Passes > knobs.Passes; }

            knobs = next;

            // If a bump can't help (every relevant knob already maxed) or clearance congestion persists past
            // the first remediation, ask the AI to revise the PLAN (fenced to advice; Empty-safe).
            bool stuckOnClearance = attempt >= 2 && classes.Any(IsClearanceClass);
            if (!bumped || stuckOnClearance)
            {
                var revised = await revise(plan, report.Violations, ct);
                plan = revised ?? plan;
                trace.Add($"attempt {attempt}: AI plan revision ({(bumped ? "congestion persists" : "bumps exhausted")})");
            }
        }

        // loop exhausted — return the best board with a "not clean after N tries" summary
        var bestSummary = bestReport is null
            ? $"DRC not clean after {maxIterations} attempts."
            : bestReport.Clean
                ? $"DRC clean on attempt {bestAttempt} — {bestReport.Summary}"
                : $"Best of {maxIterations}: {bestReport.Summary}";

        return new PcbDesignResult(true, bestReport?.Clean == true, bestSummary, bestPath, bestReport,
            maxIterations, trace, bestReport?.Notes.ToArray() ?? Array.Empty<string>());
    }

    /// <summary>Best = fewest errors, then fewest unconnected, then fewest warnings. A clean report always wins.</summary>
    private static bool IsBetter(DrcReport candidate, DrcReport? incumbent)
    {
        if (incumbent is null) return true;
        if (candidate.Clean != incumbent.Clean) return candidate.Clean;
        if (candidate.ErrorCount != incumbent.ErrorCount) return candidate.ErrorCount < incumbent.ErrorCount;
        if (candidate.UnconnectedCount != incumbent.UnconnectedCount) return candidate.UnconnectedCount < incumbent.UnconnectedCount;
        return candidate.WarningCount < incumbent.WarningCount;
    }

    private static bool IsEdgeClass(string type) =>
        type.Equals("copper_edge_clearance", StringComparison.OrdinalIgnoreCase);

    private static bool IsClearanceClass(string type) =>
        type.Equals("clearance", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("hole_clearance", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("courtyards_overlap", StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectivityClass(string type) =>
        type.Equals("unconnected_items", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("track_dangling", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("via_dangling", StringComparison.OrdinalIgnoreCase);
}
