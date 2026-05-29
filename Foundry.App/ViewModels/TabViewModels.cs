using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Config;
using Foundry.Core.Export;
using Foundry.Core.Firmware;
using Foundry.Core.Project;
using Foundry.Core.Simulation;
using Foundry.Core.Sourcing;
using Foundry.Core.Validation;
using Microsoft.Win32;

namespace Foundry.App.ViewModels;

/// <summary>Base for the workspace tab view models — all read the canonical Project.</summary>
public abstract class TabViewModelBase : ObservableObject
{
    protected TabViewModelBase(Project project) => Project = project;
    public Project Project { get; }
}

// ---------------- Overview ----------------
public sealed class SourcingRow
{
    public required string Distributor { get; init; }
    public required int Lines { get; init; }
    public required double Cost { get; init; }
    public required string Status { get; init; } // ok | warn
    public string CostText => $"${Cost:0.00}";
    public string LinesText => $"{Lines} lines";
    public string StatusText => Status == "ok" ? "ready" : "low stock";
}

public sealed partial class OverviewViewModel : TabViewModelBase
{
    public OverviewViewModel(Project project) : base(project)
    {
        var attention = project.Findings.Where(f => f.Severity is "fail" or "warn").Take(3).ToList();
        TopFindings = attention.Count > 0 ? attention : project.Findings.Take(3).ToList();

        Sourcing = project.Bom
            .GroupBy(b => string.IsNullOrWhiteSpace(b.Dist) ? "—" : b.Dist)
            .Select(g => new SourcingRow
            {
                Distributor = g.Key, Lines = g.Count(), Cost = g.Sum(x => x.Qty * x.Price),
                Status = g.Any(x => x.Stock < 100) ? "warn" : "ok",
            })
            .OrderByDescending(s => s.Cost).ToList();
    }

    /// <summary>Raised when the user asks to regenerate the whole project (handled by the shell).</summary>
    public event Action? RebuildRequested;

    [RelayCommand] private void Rebuild() => RebuildRequested?.Invoke();

    public IReadOnlyList<Finding> TopFindings { get; }
    public IReadOnlyList<SourcingRow> Sourcing { get; }
    public string CostText => $"${Project.Kpis.Cost:0.00}";
    public bool AllInStock => Project.Bom.Count > 0 && Project.Bom.All(b => b.Stock >= 100);
    public string StockText => AllInStock ? "All in stock" : $"{Project.Bom.Count(b => b.Stock < 100)} low-stock";

    /// <summary>Export the branded project-spec PDF.</summary>
    [RelayCommand]
    private void ExportPdf()
    {
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "project-spec.pdf");
            var wiring = Rendering.WiringImage.Render(Project);
            File.WriteAllBytes(path, Foundry.Core.Export.PdfExporter.ProjectPdf(Project, wiring));
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    /// <summary>Save the current design as a reusable template (PRD v2 G13).</summary>
    [RelayCommand]
    private void SaveAsTemplate()
    {
        try
        {
            Foundry.Core.Project.TemplateStore.Save(Project, Project.Title);
            System.Windows.MessageBox.Show($"Saved “{Project.Title}” as a template. Start a new project from it via New project → Templates.",
                "Foundry — template saved", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Couldn't save the template: {ex.Message}", "Foundry", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Export a shareable .foundryproj bundle (project + deliverables) to the export folder.</summary>
    [RelayCommand]
    private void ExportBundle()
    {
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var safe = string.Concat((Project.Title ?? "project").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch));
            var path = Path.Combine(dir, (string.IsNullOrWhiteSpace(safe) ? "project" : safe) + ProjectBundle.Extension);
            ProjectBundle.Export(Project, path);
            Foundry.Core.Diagnostics.AppLog.Info("export", $"bundle → {path}");
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    /// <summary>Write the DigiKey BOM CSV and open the cart manager.</summary>
    [RelayCommand]
    private void Cart()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Foundry");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "digikey-bom.csv"), CartLinks.DigiKeyBomCsv(Project.Bom));
            Process.Start(new ProcessStartInfo { FileName = CartLinks.DigiKeyBomManager, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }
}

// ---------------- BOM ----------------
/// <summary>Observable wrapper over a BOM line so live sourcing updates reflect in the table.</summary>
public sealed partial class BomRow : ObservableObject
{
    private readonly BomLine _line;
    public BomRow(BomLine line)
    {
        _line = line;
        _price = line.Price; _stock = line.Stock; _lead = line.Lead; _dist = line.Dist;
    }

    public int Qty => _line.Qty;
    public string Name => _line.Name;
    public string Note => _line.Note;
    public string Mpn => _line.Mpn;

    [ObservableProperty] private double _price;
    [ObservableProperty] private int _stock;
    [ObservableProperty] private string _lead;
    [ObservableProperty] private string _dist;

    public double Extended => Qty * Price;
    partial void OnPriceChanged(double value) => OnPropertyChanged(nameof(Extended));

    public void Apply(SourcingQuote q) { Dist = q.Distributor; Price = q.UnitPrice; Stock = q.Stock; Lead = q.Lead; }

    // v2 G10: substitutes
    [ObservableProperty] private bool _showAlternates;
    [ObservableProperty] private bool _altBusy;
    [ObservableProperty] private bool _altLoaded;
    public ObservableCollection<Foundry.Core.Sourcing.Alternate> Alternates { get; } = new();
}

public sealed partial class BomViewModel : TabViewModelBase
{
    private readonly Foundry.Core.Generation.ProjectGenerator? _reviser;
    [ObservableProperty] private string _sourcingStatus;
    [ObservableProperty] private bool _isRefreshing;

    public ObservableCollection<BomRow> Rows { get; }

    /// <summary>Raised to ask the shell to swap a part (revise + re-run downstream).</summary>
    public event Action<string>? SwapRequested;

    public BomViewModel(Project project, Foundry.Core.Generation.ProjectGenerator? reviser = null) : base(project)
    {
        _reviser = reviser;
        Rows = new ObservableCollection<BomRow>(project.Bom.Select(l => new BomRow(l)));
        var svc = SourcingService.Shared;
        _sourcingStatus = svc.IsLive
            ? $"live pricing via {svc.ProviderName}"
            : "offline — cached estimates · add a sourcing key in Settings for live pricing";
    }

    public double Total => Rows.Sum(r => r.Extended);
    public string TotalText => $"${Total:0.00}";
    public int Units => Rows.Sum(r => r.Qty);
    public string SubtotalLabel => $"Subtotal · {Rows.Count} lines · {Units} units";
    public string LinesLabel => $"BILL OF MATERIALS · {Rows.Count} LINES";
    public int LowStockCount => Rows.Count(r => r.Stock < 100);

    /// <summary>Real cost/line breakdown by distributor (replaces the old hardcoded substitutions).</summary>
    public IReadOnlyList<SourcingRow> ByDistributor => Rows
        .GroupBy(r => string.IsNullOrWhiteSpace(r.Dist) ? "—" : r.Dist)
        .Select(g => new SourcingRow
        {
            Distributor = g.Key, Lines = g.Count(), Cost = g.Sum(r => r.Extended),
            Status = g.Any(r => r.Stock < 100) ? "warn" : "ok",
        })
        .OrderByDescending(s => s.Cost).ToList();

    /// <summary>Expand a row and lazily fetch AI-suggested substitutes (PRD v2 G10).</summary>
    [RelayCommand]
    private async Task ToggleAlternates(BomRow? row)
    {
        if (row is null) return;
        row.ShowAlternates = !row.ShowAlternates;
        if (!row.ShowAlternates || row.AltLoaded || _reviser is null) return;
        row.AltBusy = true;
        try
        {
            var alts = await _reviser.SuggestAlternatesAsync(row.Name, row.Mpn);
            row.Alternates.Clear();
            foreach (var a in alts) row.Alternates.Add(a);
            row.AltLoaded = true;
            if (alts.Count == 0) row.Alternates.Add(new Foundry.Core.Sourcing.Alternate { Name = "No substitutes suggested.", Note = "" });
        }
        catch { }
        finally { row.AltBusy = false; }
    }

    // v2 G12: budget mode
    [ObservableProperty] private string _targetBudget = "";

    /// <summary>Ask the AI to bring the BOM under a target budget by substituting cheaper parts (PRD v2 G12).</summary>
    [RelayCommand]
    private void OptimizeForBudget()
    {
        var t = TargetBudget.Trim().TrimStart('$');
        if (!double.TryParse(t, out var budget) || budget <= 0) { SourcingStatus = "Enter a target budget (e.g. 25) first."; return; }
        SwapRequested?.Invoke(
            $"Rework the design to bring the total BOM cost under ${budget:0.00} by substituting cheaper but " +
            $"suitable, pin-compatible parts where possible. Keep the device fully functional; if a tradeoff is " +
            $"unavoidable, make the most reasonable choice. Current total is about {TotalText}.");
    }

    /// <summary>Swap a BOM part for a suggested alternate — revises the whole project.</summary>
    [RelayCommand]
    private void Swap(Foundry.Core.Sourcing.Alternate? alt)
    {
        if (alt is null || string.IsNullOrWhiteSpace(alt.Mpn)) return;
        SwapRequested?.Invoke(
            $"Replace the BOM part \"{alt.Replaces}\" with \"{alt.Name}\" (MPN {alt.Mpn}) and update the design, " +
            $"netlist and firmware to match the substitute. Keep everything else the same.");
    }

    [RelayCommand]
    private async Task RefreshPrices()
    {
        var svc = SourcingService.Shared;
        if (!svc.IsLive)
        {
            SourcingStatus = "offline — add a Nexar/Octopart key in Settings for live pricing";
            return;
        }
        IsRefreshing = true;
        try
        {
            foreach (var row in Rows)
            {
                var q = await svc.GetQuoteAsync(row.Mpn);
                if (q is not null) row.Apply(q);
            }
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(TotalText));
            OnPropertyChanged(nameof(ByDistributor));
            OnPropertyChanged(nameof(LowStockCount));
            SourcingStatus = $"live pricing via {svc.ProviderName} · updated {DateTime.Now:HH:mm}";
        }
        finally { IsRefreshing = false; }
    }

    /// <summary>Write a DigiKey-format BOM CSV and open the BOM manager for one-click upload (PRD §8.7).</summary>
    [RelayCommand]
    private void Cart()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Foundry");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "digikey-bom.csv");
            File.WriteAllText(path, CartLinks.DigiKeyBomCsv(Project.Bom));
            OpenUrl(CartLinks.DigiKeyBomManager);
            SourcingStatus = $"DigiKey BOM CSV written to {path} — upload it in the BOM manager";
        }
        catch (Exception ex) { SourcingStatus = $"cart export failed: {ex.Message}"; }
    }

    /// <summary>Write a Mouser-format BOM CSV and open Mouser's BOM tool (PRD v2 G11).</summary>
    [RelayCommand]
    private void CartMouser()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Foundry");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mouser-bom.csv");
            File.WriteAllText(path, CartLinks.MouserBomCsv(Project.Bom));
            OpenUrl(CartLinks.MouserBom);
            SourcingStatus = $"Mouser BOM CSV written to {path} — import it in Mouser's BOM tool";
        }
        catch (Exception ex) { SourcingStatus = $"cart export failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void Buy(BomRow? row)
    {
        if (row is not null) OpenUrl(CartLinks.Search(row.Dist, row.Mpn));
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* best effort */ }
    }
}

// ---------------- Wiring ----------------
public sealed partial class WiringViewModel : TabViewModelBase
{
    public WiringViewModel(Project project) : base(project)
    {
        Sim = new SimulationViewModel(project);
        // Running the simulation only makes sense on the breadboard (it's the live pin-state renderer).
        Sim.RunStarting += () => Breadboard = true;
    }
    public int NetCount => Project.Connections.Count;
    [ObservableProperty] private string _status = "";

    // Track B v2.2: netlist → .kicad_pcb export (mirrors the firmware VERIFY-BUILD not-installed UX).
    [ObservableProperty] private bool _isExportingPcb;
    [ObservableProperty] private string _pcbStatus = "";
    [ObservableProperty] private string _pcbSeverity = "info";  // pass | fail | info
    public ObservableCollection<string> PcbNotes { get; } = new();
    public bool HasPcbStatus => !string.IsNullOrEmpty(PcbStatus);
    partial void OnPcbStatusChanged(string value) => OnPropertyChanged(nameof(HasPcbStatus));

    // Track B v2.4: route the placed board with FreeRouting (export DSN → headless route → import SES).
    // The last-built board is remembered so ROUTE can run on it without rebuilding.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRoutePcb))]
    [NotifyPropertyChangedFor(nameof(CanExportFab))]
    [NotifyCanExecuteChangedFor(nameof(RoutePcbCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportFabCommand))]
    private string? _lastPcbPath;
    public bool CanRoutePcb => !IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath);
    partial void OnIsExportingPcbChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRoutePcb));
        OnPropertyChanged(nameof(CanExportFab));
        RoutePcbCommand.NotifyCanExecuteChanged();
        DesignPcbCommand.NotifyCanExecuteChanged();
        ExportFabCommand.NotifyCanExecuteChanged();
        DesignAndExportFabCommand.NotifyCanExecuteChanged();
    }
    public bool CanDesignPcb => !IsExportingPcb;

    // Track B v2.6 capstone: export the standard 2-layer fab file set (Gerbers + Excellon drill) from the
    // last DRC-clean board and bundle it into a single board-house-ready ZIP. Mirrors the not-installed UX.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFabZip))]
    private string? _lastFabZipPath;
    public bool HasFabZip => !string.IsNullOrEmpty(LastFabZipPath);
    public bool CanExportFab => !IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath);

    /// <summary>Live simulation state for this design (RUN/STOP lives by the SCHEMATIC|BREADBOARD toggle).</summary>
    public SimulationViewModel Sim { get; }

    // v2 G5: schematic ⇄ breadboard view
    [ObservableProperty] private bool _breadboard;
    [RelayCommand] private void ShowSchematic() => Breadboard = false;
    [RelayCommand] private void ShowBreadboard() => Breadboard = true;

    /// <summary>Render the wiring diagram to a PNG in the configured export folder.</summary>
    [RelayCommand]
    private void ExportPng()
    {
        try
        {
            var png = Breadboard ? Rendering.WiringImage.RenderBreadboard(Project) : Rendering.WiringImage.Render(Project);
            if (png is null) { Status = "Couldn't render the diagram."; return; }
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Breadboard ? "breadboard.png" : "wiring.png");
            File.WriteAllBytes(path, png);
            Foundry.Core.Diagnostics.AppLog.Info("export", $"wiring PNG → {path}");
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            Status = $"Exported to {path}";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }

    /// <summary>Export a KiCad netlist (.net) + a CSV pin report for PCB layout (PRD v2 G4/G6).</summary>
    [RelayCommand]
    private void ExportKiCad()
    {
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var net = Path.Combine(dir, "netlist.net");
            File.WriteAllText(net, Foundry.Core.Fabrication.KiCadNetlist.Export(Project));
            File.WriteAllText(Path.Combine(dir, "pinout.csv"), Foundry.Core.Fabrication.PinReport.Csv(Project));
            Foundry.Core.Diagnostics.AppLog.Info("export", $"KiCad netlist + pinout → {dir}");
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            Status = $"KiCad netlist + pinout exported to {dir}";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }

    /// <summary>
    /// Build a <c>.kicad_pcb</c> from the netlist (footprints + grid placement + ratsnest) and reveal it.
    /// Degrades gracefully when KiCad isn't installed — surfaces install guidance, never throws (PRD Track B v2.2).
    /// </summary>
    [RelayCommand]
    private async Task ExportPcb()
    {
        if (IsExportingPcb) return;
        IsExportingPcb = true;
        PcbNotes.Clear();
        PcbSeverity = "info"; PcbStatus = "Building the PCB from the netlist…";
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var result = await Foundry.Core.Pcb.PcbBuilder.BuildAsync(Project, dir);

            foreach (var n in result.Notes) PcbNotes.Add(n);

            if (!result.Installed)
            {
                PcbSeverity = "info";
                PcbStatus = $"KiCad isn't installed — install it from {Foundry.Core.Pcb.KiCadInstaller.DownloadUrl} to export a PCB.";
                return;
            }

            PcbSeverity = result.Ok ? "pass" : "fail";
            PcbStatus = result.Summary;
            if (result.Ok && result.KicadPcbPath is not null)
            {
                LastPcbPath = result.KicadPcbPath;
                Foundry.Core.Diagnostics.AppLog.Info("export", $"KiCad PCB → {result.KicadPcbPath}");
                // Continue straight into routing — copper tracks on the placed board (v2.4).
                await RouteCore(result.KicadPcbPath);
            }
        }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"PCB export failed: {ex.Message}"; }
        finally { IsExportingPcb = false; }
    }

    /// <summary>
    /// Route the last-built <c>.kicad_pcb</c> with FreeRouting, writing a <c>.routed.kicad_pcb</c> beside it.
    /// Degrades gracefully when KiCad / Java (JRE 21+) / the FreeRouting jar are missing — surfaces install
    /// guidance, downloads the single jar on demand, and never throws (PRD Track B v2.4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRoutePcb))]
    private async Task RoutePcb()
    {
        if (IsExportingPcb || string.IsNullOrEmpty(LastPcbPath)) return;
        IsExportingPcb = true;
        PcbNotes.Clear();
        try { await RouteCore(LastPcbPath); }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"PCB routing failed: {ex.Message}"; }
        finally { IsExportingPcb = false; }
    }

    /// <summary>Shared routing step used by both EXPORT+ROUTE and the standalone ROUTE affordance.</summary>
    private async Task RouteCore(string pcbPath)
    {
        // Java is locate-only; the FreeRouting jar can be fetched on demand (~58 MB, one-time).
        if (Foundry.Core.Pcb.FreeRoutingInstaller.LocateJava() is not null
            && !Foundry.Core.Pcb.FreeRoutingInstaller.JarPresent)
        {
            PcbSeverity = "info"; PcbStatus = "Downloading FreeRouting (~58 MB, one-time)…";
            try { await Foundry.Core.Pcb.FreeRoutingInstaller.DownloadJarAsync(); }
            catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"FreeRouting download failed: {ex.Message}"; return; }
        }

        PcbSeverity = "info"; PcbStatus = "Routing the PCB with FreeRouting…";
        var route = await Foundry.Core.Pcb.PcbRouter.RouteAsync(pcbPath);

        foreach (var n in route.Notes) PcbNotes.Add(n);

        if (!route.Installed)
        {
            PcbSeverity = "info";
            PcbStatus = route.Summary;  // KiCad + Java (JRE 21+) + jar install guidance
            return;
        }

        PcbSeverity = route.Ok ? "pass" : "fail";
        PcbStatus = route.Summary;
        if (route.Ok && route.RoutedPcbPath is not null)
        {
            Foundry.Core.Diagnostics.AppLog.Info("export", $"routed PCB → {route.RoutedPcbPath}");
            // v2.5: gate the routed board with DRC before revealing it.
            await DrcCore(route.RoutedPcbPath);
            Process.Start(new ProcessStartInfo { FileName = route.RoutedPcbPath, UseShellExecute = true });
        }
    }

    /// <summary>
    /// Run the v2.5 DRC gate on a routed board and surface the verdict in the PCB status block: PASS (green)
    /// or N violations (severity-colored). Degrades to clear install guidance when KiCad is absent. Never throws.
    /// </summary>
    private async Task DrcCore(string boardPath)
    {
        PcbSeverity = "info"; PcbStatus = "Running DRC on the routed board…";
        var report = await Foundry.Core.Pcb.PcbDrc.CheckAsync(boardPath);

        foreach (var n in report.Notes) PcbNotes.Add(n);

        if (!report.Installed)
        {
            PcbSeverity = "info";
            PcbStatus = report.Summary;  // "DRC needs KiCad — install it from … to run kicad-cli pcb drc."
            return;
        }

        if (report.Clean)
        {
            PcbSeverity = "pass";
            PcbStatus = $"DRC PASS — {report.Summary}";
        }
        else
        {
            // Errors fail the gate; warning-only with no unconnected nets is a softer "warn".
            PcbSeverity = report.ErrorCount > 0 || report.UnconnectedCount > 0 ? "fail" : "warn";
            PcbStatus = report.Summary;
            // List the top few violations so the user sees what to fix.
            foreach (var v in report.Violations.Where(v => !v.Excluded).Take(8))
            {
                var where = v.Location is { } p ? $" @ ({p.X:0.##}, {p.Y:0.##})" : "";
                PcbNotes.Add($"· [{v.Severity}] {v.Type}: {v.Description}{where}");
            }
            foreach (var u in report.Unconnected.Where(u => !u.Excluded).Take(4))
                PcbNotes.Add($"· [unconnected] {u.Description}");
        }
        Foundry.Core.Diagnostics.AppLog.Info("export", $"DRC {(report.Clean ? "pass" : "fail")} → {boardPath}");
    }

    /// <summary>
    /// Run the full v2.5 build→route→DRC fix loop (<see cref="Foundry.Core.Pcb.PcbDesigner"/>): the
    /// deterministic gate plus the bounded AI/clearance remediation loop. Reports the final verdict and
    /// per-iteration progress ("iteration 2/3: …"). Uses the stored Anthropic key for AI placement advice when
    /// present (geometry stays deterministic). Degrades to install guidance when KiCad is absent; never throws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDesignPcb))]
    private async Task DesignPcb()
    {
        if (IsExportingPcb) return;
        IsExportingPcb = true;
        PcbNotes.Clear();
        PcbSeverity = "info"; PcbStatus = "Designing the PCB — build → route → DRC fix loop…";
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);

            // AI placement advice is opt-in: use the stored key when present (the placer/router own geometry).
            var key = new Foundry.Core.Security.CredentialStore().Read(Foundry.Core.Security.CredentialStore.AnthropicTarget);
            Foundry.Core.Ai.IAnthropicClient? ai =
                string.IsNullOrWhiteSpace(key) ? null : new Foundry.Core.Ai.AnthropicClient(key!);
            var model = ConfigStore.Load().ModelId;
            var options = Foundry.Core.Pcb.DrcOptions.Default;

            var result = await Foundry.Core.Pcb.PcbDesigner.DesignAsync(Project, dir, ai, model, options);

            // Per-iteration progress: each Core trace line starts "attempt N: …"; re-badge it as
            // "iteration N/M:" for the UI (drop the redundant "attempt N:" prefix) so the error-count
            // delta the loop emits — e.g. "19 → 4 errors" — reads cleanly.
            int n = 0;
            foreach (var line in result.Trace)
            {
                var body = line.StartsWith("attempt ", StringComparison.Ordinal) && line.IndexOf(": ", StringComparison.Ordinal) is var i && i > 0
                    ? line[(i + 2)..]
                    : line;
                PcbNotes.Add($"iteration {++n}/{options.MaxIterations}: {body}");
            }
            foreach (var note in result.Notes) PcbNotes.Add(note);

            if (!result.Installed)
            {
                PcbSeverity = "info";
                PcbStatus = $"KiCad isn't installed — install it from {Foundry.Core.Pcb.KiCadInstaller.DownloadUrl} to run the DRC fix loop.";
                return;
            }

            PcbSeverity = result.Ok ? "pass" : "fail";
            // Final verdict spelled out: PASS, or the explicit N errors / N unrouted breakdown of the best board.
            var verdict = result.Report is { } rpt && !rpt.Clean
                ? $"DRC FAIL — {rpt.ErrorCount} error(s)"
                  + (rpt.UnconnectedCount > 0 ? $", {rpt.UnconnectedCount} net(s) unrouted" : "")
                  + $" after {result.Iterations} iteration(s)."
                : result.Summary;
            PcbStatus = result.Ok ? $"DRC PASS — {result.Summary}" : verdict;
            if (result.KicadPcbPath is not null)
            {
                LastPcbPath = result.KicadPcbPath;
                Foundry.Core.Diagnostics.AppLog.Info("export", $"PCB design {(result.Ok ? "passed" : "best-effort")} after {result.Iterations} iter → {result.KicadPcbPath}");
                Process.Start(new ProcessStartInfo { FileName = result.KicadPcbPath, UseShellExecute = true });
            }
        }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"PCB design failed: {ex.Message}"; }
        finally { IsExportingPcb = false; }
    }

    /// <summary>
    /// v2.6 capstone: export the standard 2-layer fab file set (Gerbers + Excellon drill) from the last
    /// DRC-clean board and bundle it into a single <c>&lt;name&gt;-fab.zip</c> a board house (JLCPCB/PCBWay)
    /// accepts as-is. Reveals the ZIP and reports the file set on success. Degrades to clear install guidance
    /// when KiCad is absent (mirrors the not-installed UX). Never throws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportFab))]
    private async Task ExportFab()
    {
        if (IsExportingPcb || string.IsNullOrEmpty(LastPcbPath)) return;
        IsExportingPcb = true;
        PcbNotes.Clear();
        try { await ExportFabCore(LastPcbPath); }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"Fab export failed: {ex.Message}"; }
        finally { IsExportingPcb = false; }
    }

    /// <summary>Shared fab-export step used by EXPORT GERBERS and the one-shot DESIGN + GERBERS path.</summary>
    private async Task ExportFabCore(string boardPath)
    {
        PcbSeverity = "info"; PcbStatus = "Exporting Gerbers + drill and packaging the fab ZIP…";
        var dir = ConfigStore.Load().OutputFolder;
        Directory.CreateDirectory(dir);

        var fab = await Foundry.Core.Pcb.Fab.GerberExporter.ExportAsync(boardPath, dir);

        foreach (var n in fab.Notes) PcbNotes.Add(n);

        if (!fab.Installed)
        {
            PcbSeverity = "info";
            PcbStatus = fab.Summary;  // "Fab export needs KiCad — install it from … to run kicad-cli pcb export."
            return;
        }

        PcbSeverity = fab.Ok ? "pass" : "fail";
        PcbStatus = fab.Summary;
        if (fab.Ok && fab.ZipPath is not null)
        {
            LastFabZipPath = fab.ZipPath;
            foreach (var f in fab.Files) PcbNotes.Add($"· {f}");
            Foundry.Core.Diagnostics.AppLog.Info("export", $"fab ZIP → {fab.ZipPath}");
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
    }

    /// <summary>
    /// The v2.6 one-shot path: build → place → route → DRC-fix loop → Gerbers + drill ZIP in a single action
    /// (<see cref="Foundry.Core.Pcb.PcbDesigner.DesignAndExportFabAsync"/>). Reports per-iteration progress, the
    /// DRC verdict, then the fab package. Degrades to install guidance when KiCad is absent; never throws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDesignPcb))]
    private async Task DesignAndExportFab()
    {
        if (IsExportingPcb) return;
        IsExportingPcb = true;
        PcbNotes.Clear();
        PcbSeverity = "info"; PcbStatus = "Designing the PCB and exporting fab files — build → route → DRC → Gerbers…";
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);

            var key = new Foundry.Core.Security.CredentialStore().Read(Foundry.Core.Security.CredentialStore.AnthropicTarget);
            Foundry.Core.Ai.IAnthropicClient? ai =
                string.IsNullOrWhiteSpace(key) ? null : new Foundry.Core.Ai.AnthropicClient(key!);
            var model = ConfigStore.Load().ModelId;
            var options = Foundry.Core.Pcb.DrcOptions.Default;

            var (design, fab) = await Foundry.Core.Pcb.PcbDesigner.DesignAndExportFabAsync(Project, dir, ai, model, options);

            int n = 0;
            foreach (var line in design.Trace)
                PcbNotes.Add($"iteration {++n}/{options.MaxIterations}: {line}");
            foreach (var note in design.Notes) PcbNotes.Add(note);

            if (!design.Installed)
            {
                PcbSeverity = "info";
                PcbStatus = $"KiCad isn't installed — install it from {Foundry.Core.Pcb.KiCadInstaller.DownloadUrl} to design + export fab files.";
                return;
            }

            if (design.KicadPcbPath is not null) LastPcbPath = design.KicadPcbPath;

            // The DRC gate must pass before there's a board worth fabbing.
            if (!design.Ok)
            {
                PcbSeverity = "fail";
                PcbStatus = design.Summary;
                return;
            }

            foreach (var fn in fab.Notes) PcbNotes.Add(fn);
            PcbSeverity = fab.Ok ? "pass" : "fail";
            PcbStatus = fab.Summary;
            if (fab.Ok && fab.ZipPath is not null)
            {
                LastFabZipPath = fab.ZipPath;
                foreach (var f in fab.Files) PcbNotes.Add($"· {f}");
                Foundry.Core.Diagnostics.AppLog.Info("export", $"design + fab ZIP after {design.Iterations} iter → {fab.ZipPath}");
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"Design + fab export failed: {ex.Message}"; }
        finally { IsExportingPcb = false; }
    }

    /// <summary>Reveal the last-produced fab ZIP in Explorer (selects the file).</summary>
    [RelayCommand(CanExecute = nameof(HasFabZip))]
    private void RevealFabZip()
    {
        if (string.IsNullOrEmpty(LastFabZipPath) || !File.Exists(LastFabZipPath)) return;
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{LastFabZipPath}\"", UseShellExecute = true }); }
        catch (Exception ex) { Status = $"Couldn't reveal the fab ZIP: {ex.Message}"; }
    }

    partial void OnLastFabZipPathChanged(string? value)
    {
        RevealFabZipCommand.NotifyCanExecuteChanged();
        QuoteFabCommand.NotifyCanExecuteChanged();
        PlaceFabOrderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOrderFab));
    }

    // ---- Track B v2.7: get a quote / prepare an order with a board house ----
    // Opt-in, user-keyed, explicit-confirm. Quoting and order-prep NEVER submit or pay — PLACE ORDER only
    // opens the fab's upload page (http(s) via OpenUrl) with the params copied and the ZIP ready; the user
    // finishes on the fab's site. Mirrors the PCB status block's not-installed / degrade-gracefully UX.
    [ObservableProperty] private bool _isFabbing;
    [ObservableProperty] private string _fabOrderStatus = "";
    [ObservableProperty] private string _fabQuoteText = "";
    public ObservableCollection<string> FabOrderNotes { get; } = new();
    public bool HasFabOrderStatus => !string.IsNullOrEmpty(FabOrderStatus);
    partial void OnFabOrderStatusChanged(string value) => OnPropertyChanged(nameof(HasFabOrderStatus));

    /// <summary>Which house we'll quote/order with (e.g. "PCBWAY · live quotes" or "offline · estimate + handoff").</summary>
    public string FabProviderLabel
    {
        get
        {
            var svc = Foundry.Core.Pcb.Fab.FabService.Shared;
            return svc.IsLive ? $"{svc.ProviderName} · live quotes"
                 : svc.NeedsApiKey ? $"{svc.ProviderName} · estimate + handoff"
                 : "no API key — estimate + assisted handoff (add a key in Settings)";
        }
    }

    public bool CanOrderFab => !IsFabbing && HasFabZip;

    partial void OnIsFabbingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOrderFab));
        QuoteFabCommand.NotifyCanExecuteChanged();
        PlaceFabOrderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fetch a price/lead-time quote for the last fab ZIP (clearly labelled Estimate vs Live). Never orders.</summary>
    [RelayCommand(CanExecute = nameof(CanOrderFab))]
    private async Task QuoteFab()
    {
        if (IsFabbing || string.IsNullOrEmpty(LastFabZipPath)) return;
        IsFabbing = true;
        FabOrderNotes.Clear();
        FabOrderStatus = $"Getting a quote from {Foundry.Core.Pcb.Fab.FabService.Shared.ProviderName}…";
        FabQuoteText = "";
        try
        {
            var svc = Foundry.Core.Pcb.Fab.FabService.Shared;
            var spec = Foundry.Core.Pcb.Fab.FabService.BuildSpec(LastFabZipPath, LastPcbPath);
            var quote = await svc.QuoteAsync(spec);
            var tag = quote.Source == Foundry.Core.Pcb.Fab.FabQuoteSource.Live ? "LIVE" : "ESTIMATE";
            var price = quote.Price is { } p ? $"{p:0.00} {quote.Currency}" : "price n/a";
            var lead = quote.LeadTimeDays is { } d ? $" · ~{d} day lead" : "";
            FabQuoteText = $"[{tag}] {quote.Provider} · {price}{lead}";
            FabOrderStatus = quote.Summary;
            foreach (var n in quote.Notes) FabOrderNotes.Add($"· {n}");
            Foundry.Core.Diagnostics.AppLog.Info("fab", $"quote ({tag}) · {FabQuoteText}");
        }
        catch (Exception ex) { FabOrderStatus = $"Quote failed: {ex.Message}"; }
        finally { IsFabbing = false; }
    }

    /// <summary>
    /// Explicit-confirm order prep: prepares an assisted handoff (never auto-submits), copies the order params,
    /// reveals the ZIP, and opens the fab's upload page so the user finishes the order themselves on the fab's site.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOrderFab))]
    private async Task PlaceFabOrder()
    {
        if (IsFabbing || string.IsNullOrEmpty(LastFabZipPath)) return;

        var confirm = System.Windows.MessageBox.Show(
            "Foundry will open the board house's order page in your browser with the order details copied to your " +
            "clipboard and the fab ZIP ready to upload.\n\nFoundry does NOT submit the order and does NOT pay — you " +
            "review the price and place the order yourself on the fab's site.\n\nContinue?",
            "Foundry — prepare PCB order",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        IsFabbing = true;
        FabOrderNotes.Clear();
        FabOrderStatus = "Preparing the order handoff…";
        try
        {
            var svc = Foundry.Core.Pcb.Fab.FabService.Shared;
            var spec = Foundry.Core.Pcb.Fab.FabService.BuildSpec(LastFabZipPath, LastPcbPath);
            var handoff = await svc.PrepareOrderAsync(spec);

            foreach (var n in handoff.Notes) FabOrderNotes.Add($"· {n}");
            FabOrderNotes.Add($"· order params copied to clipboard: {handoff.ClipboardParams}");
            try { System.Windows.Clipboard.SetText(handoff.ClipboardParams); } catch { /* clipboard best effort */ }

            // Reveal the ZIP so the user can drag-drop it on the fab's upload page.
            if (File.Exists(handoff.ZipPath))
                try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{handoff.ZipPath}\"", UseShellExecute = true }); }
                catch { /* best effort */ }

            // Open the fab portal — http(s) only via the safe OpenUrl. This is the only outward action, and the
            // user explicitly confirmed it; nothing is submitted or paid.
            OpenUrl(handoff.PortalUrl);

            FabOrderStatus = handoff.Summary;
            Foundry.Core.Diagnostics.AppLog.Info("fab", $"order handoff opened · {handoff.Provider} · {handoff.PortalUrl} (not submitted)");
        }
        catch (Exception ex) { FabOrderStatus = $"Order prep failed: {ex.Message}"; }
        finally { IsFabbing = false; }
    }

    /// <summary>Open a URL in the default browser — http(s) only (defends against shell-handler abuse).</summary>
    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try { Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    /// <summary>Render the wiring diagram to a vector SVG in the configured export folder.</summary>
    [RelayCommand]
    private void ExportSvg()
    {
        try
        {
            var svg = Rendering.WiringImage.RenderSvg(Project);
            if (svg is null) { Status = "Couldn't render the diagram."; return; }
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "wiring.svg");
            File.WriteAllText(path, svg);
            Foundry.Core.Diagnostics.AppLog.Info("export", $"wiring SVG → {path}");
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            Status = $"Exported to {path}";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }
}

/// <summary>
/// Live-simulation control for the Wiring tab (Track A step 3/4). Asks <see cref="SimulatorFactory"/> for the
/// right engine for this board — avr8js for Arduino AVR (Uno/Nano/Mega), Renode for STM32/RP2040 — then
/// compiles/loads the firmware, starts the session, and streams per-GPIO edges into <see cref="LivePinState"/>,
/// which the breadboard binds to and renders as glowing pins/wires. Engine-agnostic: the same one
/// <c>pin=level</c> contract drives the UI regardless of which engine produced the edges, so the install
/// affordance (INSTALL RENODE) only surfaces for the Renode engine when it's the chosen one and missing.
/// </summary>
public sealed partial class SimulationViewModel : ObservableObject
{
    private readonly Project _project;
    private readonly ISimulator _simulator;
    private SimSession? _session;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private PinStateSnapshot? _livePinState;
    [ObservableProperty] private bool _renodeInstalled;

    /// <summary>True when the chosen engine is Renode (which has an on-demand install step).</summary>
    private readonly bool _usesRenode;

    /// <summary>Raised just before a run starts so the Wiring view can flip to the breadboard renderer.</summary>
    public event Action? RunStarting;

    public SimulationViewModel(Project project, ISimulator? simulator = null)
    {
        _project = project;
        _simulator = simulator ?? SimulatorFactory.For(project);
        _usesRenode = _simulator.Engine == SimEngine.Renode;
        RenodeInstalled = RenodeInstaller.IsInstalled;
        var cap = _simulator.CanSimulate(project);
        CanSimulate = cap.Supported;
        Status = cap.Supported
            ? (NeedsRenode ? cap.Reason : "Ready to simulate — press RUN.")
            : cap.Reason;
    }

    /// <summary>Whether this board has any live-simulation model at all (false ⇒ "flash to run").</summary>
    public bool CanSimulate { get; }

    /// <summary>Only the Renode engine needs a one-time install; avr8js runs in-process.</summary>
    public bool NeedsRenode => CanSimulate && _usesRenode && !RenodeInstalled;

    partial void OnRenodeInstalledChanged(bool value) => OnPropertyChanged(nameof(NeedsRenode));

    partial void OnSpeedChanged(double value) => _session?.SetSpeed(value);

    /// <summary>Compile → start the engine → subscribe to pin edges (marshalled to the UI thread).</summary>
    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning || IsStarting || !CanSimulate) return;
        if (_usesRenode && !RenodeInstaller.IsInstalled) { Status = "Renode isn't installed — click INSTALL RENODE."; return; }

        IsStarting = true;
        RunStarting?.Invoke();
        Status = "Starting simulation…";
        _cts = new CancellationTokenSource();
        try
        {
            var session = await _simulator.StartAsync(_project, _cts.Token);
            _session = session;
            LivePinState = session.Current;
            Status = session.StatusMessage;

            if (!session.IsRunning)
            {
                // The simulator degrades gracefully (compile/engine failure) — it returns a stopped session.
                session.Dispose();
                _session = null;
                return;
            }

            session.Updated += OnSessionUpdated;
            session.Stopped += OnSessionStopped;
            session.SetSpeed(Speed);
            IsRunning = true;
            Foundry.Core.Diagnostics.AppLog.Info("sim", $"UI sim started · {session.Pins.Count} pin(s)");
        }
        catch (OperationCanceledException) { Status = "Start cancelled."; }
        catch (Exception ex) { Status = $"Couldn't start simulation: {ex.Message}"; }
        finally { IsStarting = false; }
    }

    private void OnSessionUpdated(PinStateSnapshot snapshot) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            LivePinState = snapshot;
            if (_session is not null) Status = _session.StatusMessage;
        }));

    private void OnSessionStopped(string final) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            IsRunning = false;
            Status = final;
        }));

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        var s = _session;
        _session = null;
        if (s is not null)
        {
            s.Updated -= OnSessionUpdated;
            s.Stopped -= OnSessionStopped;
            s.Dispose();
        }
        IsRunning = false;
        LivePinState = null;
        Status = "Stopped.";
    }

    /// <summary>Download a portable Renode to the app tools folder on demand (one-time).</summary>
    [RelayCommand]
    private async Task InstallRenode()
    {
        if (IsStarting || !_usesRenode) return;
        IsStarting = true;
        Status = "Downloading Renode (~120 MB, one-time)…";
        try
        {
            await RenodeInstaller.DownloadAsync();
            RenodeInstalled = RenodeInstaller.IsInstalled;
            Status = RenodeInstalled ? "Renode installed — press RUN to simulate." : "Renode install didn't complete.";
        }
        catch (Exception ex) { Status = $"Install failed: {ex.Message}"; }
        finally { IsStarting = false; }
    }
}

// ---------------- Enclosure ----------------
/// <summary>One row in the Enclosure tab's parametric-slider panel (PRD v2 Phase D).</summary>
public sealed partial class ScadParamRow : ObservableObject
{
    private readonly Action<ScadParamRow> _onChanged;
    public ScadParamRow(Foundry.Core.Cad.ScadParam p, Action<ScadParamRow> onChanged)
    {
        Name = p.Name; DisplayName = p.DisplayName;
        Min = p.Min; Max = p.Max; Step = p.Step;
        _value = p.Value;
        _onChanged = onChanged;
    }
    public string Name { get; }
    public string DisplayName { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    [ObservableProperty] private double _value;
    public string ValueText => Step >= 1 ? Value.ToString("0") : Value.ToString("0.##");
    partial void OnValueChanged(double value) { OnPropertyChanged(nameof(ValueText)); _onChanged(this); }
}

public sealed partial class EnclosureViewModel : TabViewModelBase
{
    private readonly Foundry.Core.Generation.ProjectGenerator? _ai;
    [ObservableProperty] private string _view = "ISO";
    [ObservableProperty] private bool _meshReady;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _showOffline;
    [ObservableProperty] private bool _sidecarOnline;
    [ObservableProperty] private string _sidecarStatus = "connecting to CAD sidecar…";
    [ObservableProperty] private byte[]? _stlBytes;

    // v2 Phase B/C/D: AI-written OpenSCAD ("Advanced" mode) + parametric sliders
    [ObservableProperty] private bool _advanced;
    [ObservableProperty] private string _scad = "";
    [ObservableProperty] private bool _scadBusy;
    [ObservableProperty] private string _scadStatus = "";
    [ObservableProperty] private string _scadError = "";
    public bool OpenScadInstalled => Foundry.Core.Cad.OpenScadInstaller.IsInstalled;
    public ObservableCollection<ScadParamRow> Parameters { get; } = new();
    public bool HasScadError => !string.IsNullOrEmpty(ScadError);
    partial void OnScadErrorChanged(string value) => OnPropertyChanged(nameof(HasScadError));

    private System.Windows.Threading.DispatcherTimer? _scadDebounce;
    private System.Threading.CancellationTokenSource? _scadCts;
    [RelayCommand] private void CancelScad() => _scadCts?.Cancel();

    public EnclosureViewModel(Project project, Foundry.Core.Generation.ProjectGenerator? ai = null) : base(project)
    {
        _ai = ai;
        _scad = project.Enclosure.Scad ?? "";
        _ = LoadMeshAsync();
    }

    public Enclosure E => Project.Enclosure;
    public string WallText => E.Wall.ToString("0.0");
    public string LengthText => Dim(0);
    public string WidthText => Dim(1);
    public string HeightText => Dim(2);
    private string Dim(int i) => E.Inner is { } a && a.Length > i ? a[i].ToString("0") : "—";

    // purpose-built feature readouts
    public string LidText => E.Lid?.ToLowerInvariant() == "screw" ? "Screw-down" : "Snap-fit";
    public string MountText => E.Mount?.ToLowerInvariant() switch { "wall-tabs" => "Wall tabs", "flange" => "Perimeter flange", _ => "None" };
    public string StandoffText => E.Standoffs > 0 ? $"{E.Standoffs} bosses" : "None";
    public int VentCount => E.Vents.Sum(v => v.Count);
    public string VentText => VentCount > 0 ? $"{VentCount} slots ({string.Join("/", E.Vents.Select(v => v.Face))})" : "None";

    private async Task LoadMeshAsync()
    {
        try
        {
            var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync();
            if (client is null)
            {
                SidecarOnline = false;
                IsLoading = false;
                ShowOffline = true;
                SidecarStatus = $"CAD sidecar offline ({Foundry.Core.Sidecar.SidecarHost.Shared.StatusMessage})";
                return;
            }
            var schema = Foundry.Core.Sidecar.EnclosureSchema.ToJson(E);
            var mesh = await client.BuildEnclosureAsync(schema);
            StlBytes = mesh.Stl;
            SidecarOnline = true;
            IsLoading = false;
            MeshReady = true;
            SidecarStatus = $"{mesh.Kernel} · {mesh.Triangles} tris · {client.BaseUrl}";
        }
        catch (Exception ex)
        {
            SidecarOnline = false;
            IsLoading = false;
            ShowOffline = true;
            SidecarStatus = $"CAD sidecar error: {ex.Message}";
        }
    }

    // ----- v2 Phase B/C: Advanced OpenSCAD mode -----
    [RelayCommand]
    private async Task ToggleAdvanced()
    {
        Advanced = !Advanced;
        if (!Advanced) { await LoadMeshAsync(); return; }   // back to schema render
        if (!OpenScadInstalled) { ScadStatus = "OpenSCAD isn't installed — click INSTALL OPENSCAD."; return; }
        if (string.IsNullOrWhiteSpace(Scad)) await GenerateScad();
        else await RenderScad();
    }

    /// <summary>Ask the AI to write parametric OpenSCAD for this enclosure (PRD v2 Phase B).</summary>
    [RelayCommand]
    private async Task GenerateScad()
    {
        if (ScadBusy || _ai is null) return;
        ScadBusy = true;
        ScadStatus = "Writing OpenSCAD…";
        try
        {
            var (ok, scad, msg) = await _ai.GenerateEnclosureScadAsync(Project);
            if (!ok) { ScadStatus = msg; return; }
            Scad = scad;
            Project.Enclosure.Scad = scad;
            RebuildParameters();
            ScadStatus = $"Generated · {scad.Length} chars · {Parameters.Count} parameters · rendering…";
            await RenderScad();
        }
        catch (Exception ex) { ScadStatus = $"Failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    /// <summary>Render the current SCAD via the sidecar's OpenSCAD path; updates the 3D preview.</summary>
    [RelayCommand]
    private async Task RenderScad()
    {
        if (string.IsNullOrWhiteSpace(Scad)) { ScadStatus = "No SCAD yet — click REGENERATE."; return; }
        ScadBusy = true;
        _scadCts?.Cancel();
        _scadCts = new System.Threading.CancellationTokenSource();
        var ct = _scadCts.Token;
        try
        {
            var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync(ct);
            if (client is null) { ScadStatus = "CAD sidecar offline."; return; }
            ScadStatus = "Rendering with OpenSCAD…";
            var r = await client.RenderScadAsync(Scad, "stl", ct);
            if (!r.Ok)
            {
                ScadStatus = "OpenSCAD couldn't build the script.";
                ScadError = ExtractScadError(r.Error);
                return;
            }
            ScadError = "";
            StlBytes = r.Bytes;
            IsLoading = false; ShowOffline = false; MeshReady = true;
            ScadStatus = $"Rendered with OpenSCAD · {r.Bytes.Length:N0} bytes";
            // Mirror the active render in the on-model status badge so it no longer reads the stale
            // schema-build value once Advanced takes over. Schema rebuilds (LoadMeshAsync) overwrite this.
            SidecarStatus = $"openscad · {(r.Format.Length == 0 ? "stl" : r.Format)} · {r.Bytes.Length:N0} bytes · {client.BaseUrl}";
        }
        catch (OperationCanceledException) { ScadStatus = "Render cancelled."; }
        catch (Exception ex) { ScadStatus = $"Render failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    /// <summary>Have the AI fix an OpenSCAD compile error (mirrors firmware FIX BUILD).</summary>
    [RelayCommand]
    private async Task FixScad()
    {
        if (_ai is null || ScadBusy || string.IsNullOrWhiteSpace(ScadError)) return;
        ScadBusy = true;
        ScadStatus = "Asking the AI to fix the SCAD…";
        try
        {
            var (ok, scad, _) = await _ai.FixEnclosureScadAsync(Project, Scad, ScadError);
            if (!ok) { ScadStatus = "Couldn't generate a SCAD fix."; return; }
            Scad = scad; Project.Enclosure.Scad = scad;
            RebuildParameters();
            ScadStatus = "Reworked the SCAD — rendering…";
            await RenderScad();
        }
        catch (Exception ex) { ScadStatus = $"Fix failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    private void RebuildParameters()
    {
        Parameters.Clear();
        foreach (var p in Foundry.Core.Cad.ScadParameters.Parse(Scad))
            Parameters.Add(new ScadParamRow(p, OnParamChanged));
    }

    private void OnParamChanged(ScadParamRow row)
    {
        Scad = Foundry.Core.Cad.ScadParameters.Patch(Scad, row.Name, row.Value);
        Project.Enclosure.Scad = Scad;
        // debounce 600ms so dragging a slider doesn't fire one OpenSCAD call per pixel
        _scadDebounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _scadDebounce.Stop();
        _scadDebounce.Tick -= ScadDebounceTick;
        _scadDebounce.Tick += ScadDebounceTick;
        _scadDebounce.Start();
    }
    private async void ScadDebounceTick(object? sender, EventArgs e)
    {
        _scadDebounce?.Stop();
        await RenderScad();
    }

    private static string ExtractScadError(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("stderr", out var s)) return s.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("detail", out var d)) return d.GetString() ?? raw;
        }
        catch { }
        return raw;
    }

    /// <summary>Download a portable OpenSCAD to the app tools folder (one-time, ~22 MB).</summary>
    [RelayCommand]
    private async Task InstallOpenScad()
    {
        if (ScadBusy) return;
        ScadBusy = true;
        ScadStatus = "Downloading OpenSCAD (~22 MB)…";
        try
        {
            await Foundry.Core.Cad.OpenScadInstaller.DownloadAsync();
            OnPropertyChanged(nameof(OpenScadInstalled));
            ScadStatus = "OpenSCAD installed. Click GENERATE to write the parametric SCAD.";
        }
        catch (Exception ex) { ScadStatus = $"Install failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    public string ExportLabel => $"EXPORT {((ConfigStore.Load().EnclosureFormat ?? "STL").ToUpperInvariant() == "3MF" ? "3MF" : "STL")}";

    /// <summary>Export the enclosure mesh in the configured format (STL or 3MF) to the export folder (PRD F7).</summary>
    [RelayCommand]
    private async Task ExportStl()
    {
        try
        {
            var fmt = (ConfigStore.Load().EnclosureFormat ?? "STL").ToLowerInvariant() == "3mf" ? "3mf" : "stl";
            byte[]? data = (fmt == "stl") ? StlBytes : null;
            if (data is null)
            {
                var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync();
                if (client is null) { SidecarStatus = "can't export — CAD sidecar offline"; return; }
                var mesh = await client.BuildEnclosureAsync(Foundry.Core.Sidecar.EnclosureSchema.ToJson(E, fmt));
                data = mesh.Stl;
            }
            if (data is null || data.Length == 0) { SidecarStatus = "no mesh to export"; return; }
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"enclosure.{fmt}");
            File.WriteAllBytes(path, data);
            Foundry.Core.Diagnostics.AppLog.Info("export", $"enclosure {fmt.ToUpperInvariant()} → {path}");
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            SidecarStatus = $"{fmt.ToUpperInvariant()} exported to {path}";
        }
        catch (Exception ex) { SidecarStatus = $"export failed: {ex.Message}"; }
    }
}

// ---------------- Firmware ----------------
public sealed partial class FirmwareViewModel : TabViewModelBase
{
    private readonly Foundry.Core.Generation.ProjectGenerator? _fixer;
    [ObservableProperty] private FirmwareFile _activeFile;

    // v2 G1/G3: compile verification + AI build-fix
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private string _buildStatus = "";
    [ObservableProperty] private string _buildSeverity = "info";  // pass | fail | info
    [ObservableProperty] private bool _canFixBuild;
    public ObservableCollection<Foundry.Core.Firmware.BuildDiagnostic> BuildDiagnostics { get; } = new();
    public bool HasBuildStatus => !string.IsNullOrEmpty(BuildStatus);
    partial void OnBuildStatusChanged(string value) => OnPropertyChanged(nameof(HasBuildStatus));

    // Track A step 4/4: one-click flash to a USB-connected board.
    [ObservableProperty] private bool _isFlashing;
    [ObservableProperty] private string _flashStatus = "";
    [ObservableProperty] private string _flashSeverity = "info";  // pass | fail | info
    [ObservableProperty] private DetectedBoard? _selectedBoard;
    public ObservableCollection<DetectedBoard> Boards { get; } = new();
    public bool HasFlashStatus => !string.IsNullOrEmpty(FlashStatus);
    public bool HasBoardChoices => Boards.Count > 1;
    partial void OnFlashStatusChanged(string value) => OnPropertyChanged(nameof(HasFlashStatus));

    public FirmwareViewModel(Project project, Foundry.Core.Generation.ProjectGenerator? fixer = null) : base(project)
    {
        _fixer = fixer;
        // Firmware (incl. the netlist-derived pinmap.h) is generated in the Project; open the main sketch.
        var files = project.Firmware.Files;
        _activeFile = files.FirstOrDefault(f => f.Active)
            ?? (files.Count > 0 ? Foundry.Core.Generation.ProjectGenerator.PickMainFile(files) : new FirmwareFile());
    }

    public Firmware F => Project.Firmware;
    public string HeaderText => $"{F.Platform} · {F.Board} · {F.Files.Count} files";

    /// <summary>Compile the sketch with arduino-cli and surface diagnostics (PRD v2 G1).</summary>
    [RelayCommand]
    private async Task VerifyBuild()
    {
        if (IsBuilding) return;
        IsBuilding = true;
        BuildDiagnostics.Clear();
        BuildStatus = "Compiling…"; BuildSeverity = "info";
        try
        {
            var r = await Foundry.Core.Firmware.FirmwareBuilder.CompileAsync(Project);
            if (!r.Installed)
            {
                BuildSeverity = "info";
                BuildStatus = "Build toolchain (arduino-cli) isn't installed. Click INSTALL TOOLCHAIN to add it.";
                return;
            }
            foreach (var d in r.Diagnostics) BuildDiagnostics.Add(d);
            BuildSeverity = r.Ok ? "pass" : "fail";
            BuildStatus = r.Summary;
            CanFixBuild = !r.Ok && r.Diagnostics.Count > 0 && _fixer is not null;
        }
        catch (Exception ex) { BuildSeverity = "fail"; BuildStatus = $"Build failed to run: {ex.Message}"; }
        finally { IsBuilding = false; }
    }

    /// <summary>Have the AI fix the compile errors, then re-verify (PRD v2 G3).</summary>
    [RelayCommand]
    private async Task FixBuild()
    {
        if (IsBuilding || _fixer is null || BuildDiagnostics.Count == 0) return;
        IsBuilding = true;
        CanFixBuild = false;
        BuildSeverity = "info"; BuildStatus = "Asking the AI to fix the build errors…";
        try
        {
            var errors = string.Join("\n", BuildDiagnostics.Select(d => d.Display));
            var ok = await _fixer.FixFirmwareAsync(Project, errors);
            if (!ok) { BuildSeverity = "fail"; BuildStatus = "Couldn't generate a firmware fix. Try again or edit manually."; return; }
            // refresh the file list + active sketch, then re-verify
            OnPropertyChanged(nameof(F));
            ActiveFile = Foundry.Core.Generation.ProjectGenerator.PickMainFile(Project.Firmware.Files);
            BuildStatus = "Firmware updated — re-compiling…";
        }
        catch (Exception ex) { BuildSeverity = "fail"; BuildStatus = $"Fix failed: {ex.Message}"; }
        finally { IsBuilding = false; }
        await VerifyBuild();   // recompile to confirm
    }

    /// <summary>Download arduino-cli into the app tools folder on demand (PRD v2 G1).</summary>
    [RelayCommand]
    private async Task InstallToolchain()
    {
        if (IsBuilding) return;
        IsBuilding = true;
        BuildSeverity = "info"; BuildStatus = "Downloading arduino-cli…";
        try
        {
            await Foundry.Core.Firmware.FirmwareBuilder.DownloadCliAsync();
            BuildStatus = "arduino-cli installed. Click VERIFY BUILD to compile (the board core installs on first use).";
        }
        catch (Exception ex) { BuildSeverity = "fail"; BuildStatus = $"Install failed: {ex.Message}"; }
        finally { IsBuilding = false; }
    }

    /// <summary>Scan for USB-connected boards so the user can pick one when the choice is ambiguous (PRD Track A).</summary>
    [RelayCommand]
    private async Task DetectBoards()
    {
        if (IsFlashing) return;
        IsFlashing = true;
        FlashSeverity = "info"; FlashStatus = "Scanning for connected boards…";
        try
        {
            var boards = await Foundry.Core.Firmware.FirmwareBuilder.DetectPortsAsync(Project);
            Boards.Clear();
            foreach (var b in boards) Boards.Add(b);
            OnPropertyChanged(nameof(HasBoardChoices));
            SelectedBoard = Boards.FirstOrDefault();
            FlashStatus = Boards.Count switch
            {
                0 => "No board detected — plug in your board over USB and scan again.",
                1 => $"Found {Boards[0].Label} on {Boards[0].Port}. Click FLASH to upload.",
                _ => $"Found {Boards.Count} ports — pick the right one, then click FLASH.",
            };
        }
        catch (Exception ex) { FlashSeverity = "fail"; FlashStatus = $"Board scan failed: {ex.Message}"; }
        finally { IsFlashing = false; }
    }

    /// <summary>One-click flash: compile to an image and upload it with arduino-cli (PRD Track A).</summary>
    [RelayCommand]
    private async Task Flash()
    {
        if (IsFlashing) return;
        IsFlashing = true;
        FlashSeverity = "info"; FlashStatus = "Compiling and flashing…";
        try
        {
            var result = await Foundry.Core.Firmware.FirmwareBuilder.UploadAsync(Project, SelectedBoard);
            if (!result.Installed)
            {
                FlashSeverity = "info"; FlashStatus = result.Summary;   // "install arduino-cli…"
                return;
            }
            FlashSeverity = result.Ok ? "pass" : "fail";
            FlashStatus = string.IsNullOrEmpty(result.Detail) ? result.Summary : $"{result.Summary}\n{result.Detail}";
            if (result.Ok) Foundry.Core.Diagnostics.AppLog.Info("flash", result.Summary);
            else Foundry.Core.Diagnostics.AppLog.Warn("flash", result.Summary);
        }
        catch (Exception ex) { FlashSeverity = "fail"; FlashStatus = $"Flash failed: {ex.Message}"; }
        finally { IsFlashing = false; }
    }

    /// <summary>Copy the active file's source to the clipboard.</summary>
    [RelayCommand]
    private void CopyActive()
    {
        try { if (ActiveFile is not null) System.Windows.Clipboard.SetText(ActiveFile.Content); } catch { }
    }

    /// <summary>Export the generated firmware to a project folder and reveal it (PRD F7).</summary>
    [RelayCommand]
    private void Export()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to export the firmware project" };
        if (dlg.ShowDialog() != true) return;

        var dir = Path.Combine(dlg.FolderName, "firmware");
        FirmwareExporter.Export(F, dir);
        Foundry.Core.Diagnostics.AppLog.Info("export", $"firmware ({F.Files.Count} files) → {dir}");
        try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch { /* reveal is best-effort */ }
    }
}

// ---------------- Validation ----------------
public sealed class PowerSlice
{
    public required string Label { get; init; }
    public required int Ma { get; init; }
    public required string BrushKey { get; init; }
}

public sealed partial class ValidationViewModel : TabViewModelBase
{
    [ObservableProperty] private string _status = "";
    /// <summary>True while an AI fix is being generated for a finding (drives the on-page indicator).</summary>
    [ObservableProperty] private bool _isFixing;
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Observable copy of the findings so Re-run / Apply refresh the list live.</summary>
    public ObservableCollection<Finding> Findings { get; } = new();

    /// <summary>Raised after the findings change (re-run / auto-fix) so the rail badge can update.</summary>
    public event Action? FindingsChanged;

    /// <summary>Raised when a fix needs the AI to generate it (no deterministic netlist edit applies).</summary>
    public event Action<Finding>? FixRequested;

    public ValidationViewModel(Project project) : base(project)
    {
        Refresh();
        var (slices, total, peak, battery) = BuildPowerBudget();
        PowerBudget = slices; PowerTotal = total; PeakText = peak; BatteryText = battery;
    }

    public int FailCount => Project.Findings.Count(f => f.Severity == "fail");
    public int WarnCount => Project.Findings.Count(f => f.Severity == "warn");
    public int PassCount => Project.Findings.Count(f => f.Severity == "pass");
    public string OverallStatus => FailCount > 0 ? "FAIL" : WarnCount > 0 ? "WARN" : "PASS";
    public string PassText => $"{PassCount} / {Project.Findings.Count}";
    public string ChecksLabel => $"DETERMINISTIC RULES ENGINE · {Project.Findings.Count} CHECKS";

    // v2 G9: report card — a grade + a plain "safe to power on?" verdict.
    public string Grade => FailCount > 0 ? "F" : WarnCount == 0 ? "A" : WarnCount <= 2 ? "B" : WarnCount <= 5 ? "C" : "D";
    public string GradeSeverity => FailCount > 0 ? "fail" : WarnCount > 0 ? "warn" : "pass";
    public string Verdict => FailCount > 0
        ? "Not yet — resolve the failures before applying power."
        : WarnCount > 0
            ? "Likely OK — review the warnings, then verify before powering on."
            : "Deterministic checks pass — safe to power on (still verify before building).";

    private void Refresh()
    {
        Findings.Clear();
        foreach (var f in Project.Findings) Findings.Add(f);
        OnPropertyChanged(nameof(FailCount));
        OnPropertyChanged(nameof(WarnCount));
        OnPropertyChanged(nameof(PassCount));
        OnPropertyChanged(nameof(OverallStatus));
        OnPropertyChanged(nameof(PassText));
        OnPropertyChanged(nameof(ChecksLabel));
        OnPropertyChanged(nameof(Grade));
        OnPropertyChanged(nameof(GradeSeverity));
        OnPropertyChanged(nameof(Verdict));
        FindingsChanged?.Invoke();
    }

    /// <summary>Write the validation report to the configured export folder (PRD F7).</summary>
    [RelayCommand]
    private void ExportReport()
    {
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "validation-report.pdf");
            File.WriteAllBytes(path, PdfExporter.ValidationPdf(Project));
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            Status = $"Report exported to {path}";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void ReRun()
    {
        ProjectValidator.Revalidate(Project);
        Refresh();
        Foundry.Core.Diagnostics.AppLog.Info("validation", $"re-ran · {FailCount} fail · {WarnCount} warn · {PassCount} pass");
        Status = $"Re-ran {Project.Findings.Count} checks · {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void ApplyFix(Finding? finding)
    {
        if (finding is null) return;
        // Fast path: a deterministic netlist edit (remap to a free pin / connect a rail) for a single
        // issue. Grouped findings (many refs) go to the AI, which resolves them all in one pass.
        if (finding.Refs.Count <= 1 && ProjectValidator.CanAutoFix(finding) && ProjectValidator.TryAutoFix(Project, finding))
        {
            ProjectValidator.Revalidate(Project);
            Refresh();
            Status = $"Applied “{finding.Fix}” · re-validated ({Project.Findings.Count} checks)";
            return;
        }
        // Otherwise have the AI generate the fix (handled by the shell, which revises + re-validates).
        Status = $"Generating a fix for {finding.Code}…";
        FixRequested?.Invoke(finding);
    }

    // Power budget derived from the real component active currents.
    public IReadOnlyList<PowerSlice> PowerBudget { get; }
    public int PowerTotal { get; }
    public string PeakText { get; }
    public string BatteryText { get; }

    private (List<PowerSlice> slices, int total, string peak, string battery) BuildPowerBudget()
    {
        var palette = new[] { "Brush.Accent", "Brush.Info", "Brush.Ok", "Brush.Warn", "Brush.InkMute" };
        var draws = Project.Components.Where(c => c.CurrentMaActive > 0)
            .OrderByDescending(c => c.CurrentMaActive).ToList();

        var slices = new List<PowerSlice>();
        int i = 0;
        foreach (var c in draws.Take(5))
            slices.Add(new PowerSlice { Label = c.Alias, Ma = c.CurrentMaActive, BrushKey = palette[i++ % palette.Length] });
        var rest = draws.Skip(5).Sum(c => c.CurrentMaActive);
        if (rest > 0) slices.Add(new PowerSlice { Label = "other", Ma = rest, BrushKey = "Brush.InkMute" });

        var total = draws.Sum(c => c.CurrentMaActive);
        var batt = Project.Components.FirstOrDefault(c => c.CapacityMah > 0);
        return (slices, Math.Max(1, total),
            $"PEAK · {total} mA @ active",
            batt is not null ? $"{batt.CapacityMah} mAh · {batt.Name}" : "no battery defined");
    }
}

// ---------------- Guide ----------------
public sealed partial class GuideViewModel : TabViewModelBase
{
    public GuideViewModel(Project project) : base(project) { }

    public string StepsLabel => $"ASSEMBLY GUIDE · {Project.Assembly.Count} STEPS";

    /// <summary>Export a branded project-spec PDF to the configured folder (PRD F7).</summary>
    [RelayCommand]
    private void ExportPdf()
    {
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{SafeName(Project.Title)}-spec.pdf");
            File.WriteAllBytes(path, PdfExporter.ProjectPdf(Project, Rendering.WiringImage.Render(Project)));
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    /// <summary>Export the assembly guide to Markdown in the configured folder (PRD F7).</summary>
    [RelayCommand]
    private void ExportMarkdown()
    {
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "assembly-guide.md");
            File.WriteAllText(path, Exporters.GuideMarkdown(Project));
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private static string SafeName(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '-');
        return string.IsNullOrWhiteSpace(s) ? "foundry-project" : s;
    }
}
