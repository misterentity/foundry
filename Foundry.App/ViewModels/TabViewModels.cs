using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Firmware;
using Foundry.Core.Project;
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
public sealed class BomViewModel : TabViewModelBase
{
    public BomViewModel(Project project) : base(project) { }
    public double Total => Project.Bom.Sum(l => l.Extended);
    public string TotalText => $"${Total:0.00}";
    public int Units => Project.Bom.Sum(l => l.Qty);
    public string SubtotalLabel => $"Subtotal · {Project.Bom.Count} lines · {Units} units";
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
    public EnclosureViewModel(Project project) : base(project) { }
    public Enclosure E => Project.Enclosure;
    public string WallText => E.Wall.ToString("0.0");
    public string LengthText => E.Inner[0].ToString("0");
    public string WidthText => E.Inner[1].ToString("0");
    public string HeightText => E.Inner[2].ToString("0");
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
public sealed class GuideViewModel : TabViewModelBase
{
    public GuideViewModel(Project project) : base(project) { }
}
