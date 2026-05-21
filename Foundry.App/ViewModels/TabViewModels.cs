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

public sealed class OverviewViewModel : TabViewModelBase
{
    public OverviewViewModel(Project project) : base(project)
    {
        TopFindings = project.Findings.Take(3).ToList();
    }

    public IReadOnlyList<Finding> TopFindings { get; }
    public string CostText => $"${Project.Kpis.Cost:0.00}";

    public IReadOnlyList<SourcingRow> Sourcing { get; } = new[]
    {
        new SourcingRow { Distributor="DigiKey", Lines=4, Cost=18.13, Status="ok" },
        new SourcingRow { Distributor="Mouser",  Lines=3, Cost=8.61,  Status="ok" },
        new SourcingRow { Distributor="Amazon",  Lines=2, Cost=11.68, Status="warn" },
    };
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
}

public sealed partial class BomViewModel : TabViewModelBase
{
    [ObservableProperty] private string _sourcingStatus;
    [ObservableProperty] private bool _isRefreshing;

    public ObservableCollection<BomRow> Rows { get; }

    public BomViewModel(Project project) : base(project)
    {
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
public sealed class WiringViewModel : TabViewModelBase
{
    public WiringViewModel(Project project) : base(project) { }
    public int NetCount => Project.Connections.Count;
}

// ---------------- Enclosure ----------------
public sealed partial class EnclosureViewModel : TabViewModelBase
{
    [ObservableProperty] private string _view = "ISO";
    [ObservableProperty] private bool _meshReady;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _showOffline;
    [ObservableProperty] private bool _sidecarOnline;
    [ObservableProperty] private string _sidecarStatus = "connecting to CAD sidecar…";
    [ObservableProperty] private byte[]? _stlBytes;

    public EnclosureViewModel(Project project) : base(project)
    {
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

    /// <summary>Save the generated STL to the configured export folder (PRD F7).</summary>
    [RelayCommand]
    private void ExportStl()
    {
        if (StlBytes is null) { SidecarStatus = "no mesh to export — sidecar offline"; return; }
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "enclosure.stl");
            File.WriteAllBytes(path, StlBytes);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            SidecarStatus = $"STL exported to {path}";
        }
        catch (Exception ex) { SidecarStatus = $"export failed: {ex.Message}"; }
    }
}

// ---------------- Firmware ----------------
public sealed partial class FirmwareViewModel : TabViewModelBase
{
    [ObservableProperty] private FirmwareFile _activeFile;

    public FirmwareViewModel(Project project) : base(project)
    {
        // Firmware (incl. the netlist-derived pinmap.h) is generated in the Project; just bind it.
        _activeFile = project.Firmware.Files.FirstOrDefault() ?? new FirmwareFile();
    }

    public Firmware F => Project.Firmware;
    public string HeaderText => $"{F.Platform} · {F.Board} · {F.Files.Count} files";

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

    /// <summary>Observable copy of the findings so Re-run / Apply refresh the list live.</summary>
    public ObservableCollection<Finding> Findings { get; } = new();

    /// <summary>Raised after the findings change (re-run / auto-fix) so the rail badge can update.</summary>
    public event Action? FindingsChanged;

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
        Status = $"Re-ran {Project.Findings.Count} checks · {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void ApplyFix(Finding? finding)
    {
        if (finding is null) return;
        if (!ProjectValidator.CanAutoFix(finding))
        {
            Status = $"{finding.Code}: “{finding.Fix}” needs a manual change — no safe automatic edit.";
            return;
        }
        if (ProjectValidator.TryAutoFix(Project, finding))
        {
            ProjectValidator.Revalidate(Project);
            Refresh();
            Status = $"Applied “{finding.Fix}” · re-validated ({Project.Findings.Count} checks)";
        }
        else
        {
            Status = $"Couldn’t auto-fix {finding.Code} — no free pin/rail available.";
        }
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

    /// <summary>Export a branded project-spec PDF to the configured folder (PRD F7).</summary>
    [RelayCommand]
    private void ExportPdf()
    {
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{SafeName(Project.Title)}-spec.pdf");
            File.WriteAllBytes(path, PdfExporter.ProjectPdf(Project));
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
