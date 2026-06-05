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
        try { await CompileCore(); }
        finally { IsBuilding = false; }
    }

    /// <summary>The compile pass itself — does NOT touch IsBuilding, so the fix→re-verify sequence can hold the
    /// re-entrancy guard across both steps (a shared guard prevents overlapping arduino-cli compiles).</summary>
    private async Task CompileCore()
    {
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
    }

    /// <summary>Have the AI fix the compile errors, then re-verify (PRD v2 G3).</summary>
    [RelayCommand]
    private async Task FixBuild()
    {
        if (IsBuilding || _fixer is null || BuildDiagnostics.Count == 0) return;
        IsBuilding = true;   // held across fix AND the re-compile below — no window for a concurrent build
        CanFixBuild = false;
        try
        {
            BuildSeverity = "info"; BuildStatus = "Asking the AI to fix the build errors…";
            var errors = string.Join("\n", BuildDiagnostics.Select(d => d.Display));
            var ok = await _fixer.FixFirmwareAsync(Project, errors);
            if (!ok) { BuildSeverity = "fail"; BuildStatus = "Couldn't generate a firmware fix. Try again or edit manually."; return; }
            // refresh the file list + active sketch, then re-verify (same guard)
            OnPropertyChanged(nameof(F));
            ActiveFile = Foundry.Core.Generation.ProjectGenerator.PickMainFile(Project.Firmware.Files);
            BuildStatus = "Firmware updated — re-compiling…";
            await CompileCore();
        }
        catch (Exception ex) { BuildSeverity = "fail"; BuildStatus = $"Fix failed: {ex.Message}"; }
        finally { IsBuilding = false; }
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

        // Resolve the exact target board FIRST so the confirmation names the real port + resolved board, and
        // never silently flash the first of several connected boards.
        var board = SelectedBoard;
        if (board is null)
        {
            var detected = await Foundry.Core.Firmware.FirmwareBuilder.DetectPortsAsync(Project);
            if (detected.Count == 0)
            {
                FlashSeverity = "info"; FlashStatus = "No board detected — connect a board, then Detect boards.";
                return;
            }
            if (detected.Count > 1)
            {
                Boards.Clear();
                foreach (var b in detected) Boards.Add(b);
                FlashSeverity = "info"; FlashStatus = "Multiple boards detected — pick one under Detect boards, then Flash.";
                return;
            }
            board = detected[0];
        }

        var plan = Foundry.Core.Firmware.FirmwareBuilder.BuildFlashPlan(Project, board);

        // Flashing is the only IRREVERSIBLE hardware action — require an explicit confirm, defaulting to Cancel.
        var text = plan.VendorMismatch ? plan.MismatchWarning + "\n\n" + plan.ConfirmText : plan.ConfirmText;
        var confirm = System.Windows.MessageBox.Show(text, "Foundry — confirm flash",
            System.Windows.MessageBoxButton.OKCancel,
            plan.VendorMismatch ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Cancel);
        if (confirm != System.Windows.MessageBoxResult.OK)
        {
            FlashSeverity = "info"; FlashStatus = "Flash cancelled.";
            return;
        }

        IsFlashing = true;
        FlashSeverity = "info"; FlashStatus = $"Compiling and flashing {plan.Fqbn} → {plan.Port}…";
        try
        {
            var result = await Foundry.Core.Firmware.FirmwareBuilder.UploadAsync(Project, board, forceMismatch: plan.VendorMismatch);
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
