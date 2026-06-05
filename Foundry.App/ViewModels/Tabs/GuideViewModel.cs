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
        catch (Exception ex) { Foundry.Core.Diagnostics.AppLog.Error("export", $"Guide PDF export failed: {ex.Message}"); }
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
        catch (Exception ex) { Foundry.Core.Diagnostics.AppLog.Error("export", $"Guide Markdown export failed: {ex.Message}"); }
    }

    private static string SafeName(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '-');
        return string.IsNullOrWhiteSpace(s) ? "foundry-project" : s;
    }
}
