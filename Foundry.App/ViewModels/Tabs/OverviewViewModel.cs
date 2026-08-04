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
            // Writes a numbered sibling when the PDF is already open in a viewer, and returns what it used.
            var written = Foundry.Core.Export.Exporters.WriteBytesUnlocked(
                path, Foundry.Core.Export.PdfExporter.ProjectPdf(Project, wiring));
            Foundry.Core.Diagnostics.AppLog.Info("export", $"project PDF → {written}");
            Process.Start(new ProcessStartInfo { FileName = written, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A failed export used to be logged and nothing else, so the button silently did nothing.
            Foundry.Core.Diagnostics.AppLog.Error("export", $"PDF export failed: {ex.Message}");
            System.Windows.MessageBox.Show($"Couldn't export the PDF:\n\n{ex.Message}", "Foundry — export",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
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

    /// <summary>True while the package is being assembled — the mesh build is a round-trip to the sidecar.</summary>
    [ObservableProperty] private bool _packaging;

    /// <summary>Button text; packaging takes seconds because it renders the PDF and builds the mesh.</summary>
    public string PackageButtonLabel => Packaging ? "PACKAGING…" : "PACKAGE";
    partial void OnPackagingChanged(bool value) => OnPropertyChanged(nameof(PackageButtonLabel));

    /// <summary>
    /// Export the complete project as one shareable zip: spec PDF, firmware, printable enclosure mesh,
    /// fabrication data and every report, under a named folder with a README explaining each file.
    ///
    /// <para>
    /// Core composes the archive but cannot produce the wiring images (WPF visuals) or the mesh (an HTTP
    /// round-trip to the CAD sidecar), so they are gathered here and handed in. Anything unavailable is
    /// listed in the package README rather than dropped silently.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ExportBundle()
    {
        if (Packaging) return;
        Packaging = true;
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ProjectPackage.Slug(Project.Title) + ProjectPackage.Extension);

            // WPF visuals must be rendered on the UI thread; we are already on it.
            var wiring = TryRender(() => Rendering.WiringImage.Render(Project), "wiring diagram");
            var breadboard = TryRender(() => Rendering.WiringImage.RenderBreadboard(Project), "breadboard view");

            var (mesh, meshExt) = await TryBuildMeshAsync();

            var result = await Task.Run(() => ProjectPackage.Write(Project, path, new PackageAssets
            {
                WiringPng = wiring,
                BreadboardPng = breadboard,
                EnclosureMesh = mesh,
                EnclosureMeshExt = meshExt,
            }));

            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });

            var omitted = result.Omitted.Count == 0
                ? ""
                : $"\n\nNot included ({result.Omitted.Count}):\n• " + string.Join("\n• ", result.Omitted);
            System.Windows.MessageBox.Show(
                $"Packaged {result.Included.Count} files ({result.Bytes / 1024.0 / 1024.0:0.#} MB) to:\n{result.Path}{omitted}",
                "Foundry — project package", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // This used to log and nothing else, so a failed export looked like a button that did nothing.
            Foundry.Core.Diagnostics.AppLog.Error("export", $"project package failed: {ex.Message}");
            System.Windows.MessageBox.Show($"Couldn't build the package:\n\n{ex.Message}", "Foundry — export",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally { Packaging = false; }
    }

    private static byte[]? TryRender(Func<byte[]?> render, string what)
    {
        try { return render(); }
        catch (Exception ex)
        {
            Foundry.Core.Diagnostics.AppLog.Warn("export", $"{what} could not be rendered: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The print-arranged mesh, or null when the sidecar is unavailable. Always "print", never the preview
    /// arrangement — the file someone else receives has to be slicable.
    /// </summary>
    private async Task<(byte[]? Mesh, string Ext)> TryBuildMeshAsync()
    {
        var fmt = (ConfigStore.Load().EnclosureFormat ?? "STL").Equals("3mf", StringComparison.OrdinalIgnoreCase)
            ? "3mf" : "stl";
        try
        {
            if (Project.Enclosure.Inner is not { Length: >= 3 } inner || inner.Take(3).All(v => v <= 0))
                return (null, fmt);

            var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync();
            if (client is null) return (null, fmt);

            var mesh = await client.BuildEnclosureAsync(Foundry.Core.Sidecar.EnclosureSchema.ToJson(
                Project.Enclosure, fmt, arrange: "print",
                board: Foundry.Core.Cad.EnclosureFit.PlaceBoard(Project)));
            return (mesh.Stl, fmt);
        }
        catch (Exception ex)
        {
            Foundry.Core.Diagnostics.AppLog.Warn("export", $"enclosure mesh unavailable for the package: {ex.Message}");
            return (null, fmt);
        }
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
