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
        OnPropertyChanged(nameof(CanCancelPcb));
        RoutePcbCommand.NotifyCanExecuteChanged();
        DesignPcbCommand.NotifyCanExecuteChanged();
        ExportFabCommand.NotifyCanExecuteChanged();
        DesignAndExportFabCommand.NotifyCanExecuteChanged();
        CancelPcbCommand.NotifyCanExecuteChanged();
    }
    public bool CanDesignPcb => !IsExportingPcb;

    // Track B v2.6 capstone: export the standard 2-layer fab file set (Gerbers + Excellon drill) from the
    // last DRC-clean board and bundle it into a single board-house-ready ZIP. Mirrors the not-installed UX.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFabZip))]
    private string? _lastFabZipPath;
    public bool HasFabZip => !string.IsNullOrEmpty(LastFabZipPath);

    // Fab-gate provenance: a board may be exported only when BOTH its connectivity is verified (P0-1: no
    // unmapped/ordinal-guessed pins) AND its DRC came back clean (P0-6). Setting a new board path resets both
    // to "unverified" until the build/route/DRC/design flow explicitly marks them — so a best-effort or
    // DRC-FAIL board never has the fab button enabled.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportFab))]
    private bool _connectivityVerified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportFab))]
    private bool _lastDrcClean;
    partial void OnConnectivityVerifiedChanged(bool value) => ExportFabCommand.NotifyCanExecuteChanged();
    partial void OnLastDrcCleanChanged(bool value) => ExportFabCommand.NotifyCanExecuteChanged();
    partial void OnLastPcbPathChanged(string? value)
    {
        ConnectivityVerified = false;
        LastDrcClean = false;
    }
    public bool CanExportFab => !IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath) && ConnectivityVerified && LastDrcClean;

    // P0-3: a fresh CTS per PCB operation so the user can CANCEL a long build/route/DRC/export. The Core
    // subprocesses honor the token (ProcessRunner kills the whole tree); on cancel they surface "Cancelled".
    private CancellationTokenSource? _pcbCts;
    public bool CanCancelPcb => IsExportingPcb;
    [RelayCommand(CanExecute = nameof(CanCancelPcb))]
    private void CancelPcb() => _pcbCts?.Cancel();

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
