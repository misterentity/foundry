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
                Status = Foundry.Core.Sourcing.BomPricing.GroupStatus(g),
            })
            .OrderByDescending(s => s.Cost).ToList();
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Raised when the user asks to regenerate the whole project (handled by the shell).</summary>
    public event Action? RebuildRequested;

    [RelayCommand] private void Rebuild() => RebuildRequested?.Invoke();

    public IReadOnlyList<Finding> TopFindings { get; }
    public IReadOnlyList<SourcingRow> Sourcing { get; }
    public string CostText => $"${Project.Kpis.Cost:0.00}";

    // ---- counts that used to be literals from the original design comp ----
    //
    // "4 subsystems · 9 nets" and "2 warn · 1 info · 2 pass" were hardcoded strings sitting directly beneath
    // genuinely bound values, so they read as computed. They happened to be near-right for the demo and were
    // wrong for every generated project — and "info" is not even a severity this engine produces.

    public string ArchitectureCountText =>
        $"{Count(Project.Subsystems.Count, "subsystem")} · {Count(NetCount, "net")}";

    /// <summary>Distinct nets, not connections — several wires share one net on a bus.</summary>
    private int NetCount => Project.Connections
        .Select(c => c.Net).Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public string FindingsCountText
    {
        get
        {
            var parts = new[] { "fail", "warn", "unproven", "pass" }
                .Select(s => (sev: s, n: Project.Findings.Count(f => f.Severity == s)))
                .Where(x => x.n > 0)
                .Select(x => $"{x.n} {x.sev}")
                .ToList();
            return parts.Count > 0 ? string.Join(" · ", parts) : "not validated";
        }
    }

    /// <summary>Severity driving the findings chip's colour, so it cannot read "warn" while a fail is present.</summary>
    public string FindingsSeverity => Project.Validation;

    // ---- print estimate ----
    //
    // The comp read "2h 14m @ 0.2mm". The layer height was never computed by anything, and PrintTime is
    // empty for every generated project (ProjectGenerator sets ""), so the line was the demo's own value
    // hardcoded into the view. It now shows only when there is a real figure to show.
    public string PrintTimeText => Project.Enclosure.PrintTime;
    public bool HasPrintTime => !string.IsNullOrWhiteSpace(Project.Enclosure.PrintTime);
    /// <summary>Only true when a provider actually reported healthy stock for every line.</summary>
    public bool AllInStock =>
        Project.Bom.Count > 0 && Project.Bom.All(b => b.IsLive && !b.LowStock);

    public string StockText => Foundry.Core.Sourcing.BomPricing.StockSummary(Project.Bom);

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
        catch (Exception ex) { Foundry.Core.Diagnostics.AppLog.Error("export", $"PDF export failed: {ex.Message}"); }
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
        catch (Exception ex) { Foundry.Core.Diagnostics.AppLog.Error("export", $"Bundle export failed: {ex.Message}"); }
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
        catch (Exception ex) { Foundry.Core.Diagnostics.AppLog.Error("export", $"Cart export failed: {ex.Message}"); }
    }
}
