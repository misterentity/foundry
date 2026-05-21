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

    private async Task LoadMeshAsync()
    {
        try
        {
            var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync();
            if (client is null)
            {
                SidecarOnline = false;
                SidecarStatus = $"sidecar offline — showing schematic preview ({Foundry.Core.Sidecar.SidecarHost.Shared.StatusMessage})";
                return;
            }
            var schema = Foundry.Core.Sidecar.EnclosureSchema.ToJson(E);
            var mesh = await client.BuildEnclosureAsync(schema);
            StlBytes = mesh.Stl;
            SidecarOnline = true;
            MeshReady = true;
            SidecarStatus = $"{mesh.Kernel} · {mesh.Triangles} tris · {client.BaseUrl}";
        }
        catch (Exception ex)
        {
            SidecarOnline = false;
            SidecarStatus = $"sidecar error — showing schematic preview ({ex.Message})";
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

public sealed class ValidationViewModel : TabViewModelBase
{
    public ValidationViewModel(Project project) : base(project) { }

    public int FailCount => Project.Findings.Count(f => f.Severity == "fail");
    public int WarnCount => Project.Findings.Count(f => f.Severity == "warn");
    public int PassCount => Project.Findings.Count(f => f.Severity == "pass");
    public string OverallStatus => FailCount > 0 ? "FAIL" : WarnCount > 0 ? "WARN" : "PASS";
    public string PassText => $"{PassCount} / 27";

    public IReadOnlyList<PowerSlice> PowerBudget { get; } = new[]
    {
        new PowerSlice { Label="Wi-Fi TX",    Ma=48, BrushKey="Brush.Accent" },
        new PowerSlice { Label="MCU active",  Ma=18, BrushKey="Brush.Info" },
        new PowerSlice { Label="Sensor read", Ma=12, BrushKey="Brush.Ok" },
        new PowerSlice { Label="ADC + boost", Ma=4,  BrushKey="Brush.Warn" },
        new PowerSlice { Label="Quiescent",   Ma=2,  BrushKey="Brush.InkMute" },
    };
    public int PowerTotal => 84;
}

// ---------------- Guide ----------------
public sealed partial class GuideViewModel : TabViewModelBase
{
    public GuideViewModel(Project project) : base(project) { }

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
}
