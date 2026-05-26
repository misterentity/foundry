using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Config;
using Foundry.Core.Export;
using Foundry.Core.Firmware;
using Foundry.Core.Project;
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
    public WiringViewModel(Project project) : base(project) { }
    public int NetCount => Project.Connections.Count;
    [ObservableProperty] private string _status = "";

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
