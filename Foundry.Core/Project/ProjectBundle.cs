using System.IO.Compression;
using Foundry.Core.Export;
using Foundry.Core.Fabrication;
using Foundry.Core.Sourcing;

namespace Foundry.Core.Project;

/// <summary>
/// A shareable/backup-able project bundle (PRD v2 G14): a single `.foundryproj` zip containing the
/// canonical Project JSON plus the generated deliverables (firmware, BOM, netlist, guide). The
/// Project JSON round-trips exactly; the extra artifacts are conveniences for the recipient.
/// </summary>
public static class ProjectBundle
{
    public const string Extension = ".foundryproj";
    private const string ProjectEntry = "project.json";

    public static void Export(Project project, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void Write(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }

        Write(ProjectEntry, ProjectStore.Serialize(project));            // authoritative — round-trips
        try { Write("bom.csv", CartLinks.DigiKeyBomCsv(project.Bom)); } catch { }
        try { Write("netlist.net", KiCadNetlist.Export(project)); } catch { }
        try { Write("pinout.csv", PinReport.Csv(project)); } catch { }
        try { Write("assembly-guide.md", Exporters.GuideMarkdown(project)); } catch { }
        try { Write("validation.md", Exporters.ValidationReport(project)); } catch { }
        foreach (var f in project.Firmware.Files)
            try { Write("firmware/" + SafeName(f.Name), f.Content); } catch { }
    }

    /// <summary>Read the Project back from a bundle (the canonical project.json entry).</summary>
    public static Project Import(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(ProjectEntry)
            ?? throw new InvalidDataException("Not a Foundry bundle — project.json is missing.");
        using var r = new StreamReader(entry.Open());
        return ProjectStore.Deserialize(r.ReadToEnd());
    }

    private static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.txt" : name;
    }
}
