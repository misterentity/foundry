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
    /// <summary>Checks the engine could not complete — it lacked a fact, so it reached no verdict.</summary>
    public int UnprovenCount => Project.Findings.Count(f => f.Severity == "unproven");
    public string OverallStatus => FailCount > 0 ? "FAIL"
        : WarnCount > 0 ? "WARN"
        : UnprovenCount > 0 ? "UNPROVEN" : "PASS";
    public string PassText => $"{PassCount} / {Project.Findings.Count}";
    public string ChecksLabel => $"DETERMINISTIC RULES ENGINE · {Project.Findings.Count} CHECKS";

    // v2 G9: report card — a grade + a plain "safe to power on?" verdict.
    //
    // An UNPROVEN check is not a passed check. Grading on fails and warns alone meant a design whose
    // checks could not be completed still scored "A" and was told it was safe to power on — the engine
    // certifying exactly what it had not looked at. There is no letter for "I don't know", so it says so.
    public string Grade => FailCount > 0 ? "F"
        : UnprovenCount > 0 ? "?"
        : WarnCount == 0 ? "A" : WarnCount <= 2 ? "B" : WarnCount <= 5 ? "C" : "D";
    public string GradeSeverity => FailCount > 0 ? "fail"
        : WarnCount > 0 ? "warn"
        : UnprovenCount > 0 ? "unproven" : "pass";
    public string Verdict => FailCount > 0
        ? "Not yet — resolve the failures before applying power."
        : WarnCount > 0
            ? "Likely OK — review the warnings, then verify before powering on."
            : UnprovenCount > 0
                ? $"Can't say — {UnprovenCount} check{(UnprovenCount == 1 ? "" : "s")} couldn't be completed. Nothing here failed, but nothing here proves it's safe either."
                : "Deterministic checks pass — safe to power on (still verify before building).";

    private void Refresh()
    {
        Findings.Clear();
        foreach (var f in Project.Findings) Findings.Add(f);
        OnPropertyChanged(nameof(FailCount));
        OnPropertyChanged(nameof(WarnCount));
        OnPropertyChanged(nameof(PassCount));
        OnPropertyChanged(nameof(UnprovenCount));
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
