using System.Globalization;
using System.IO.Compression;
using System.Text;
using Foundry.Core.Fabrication;
using Foundry.Core.Project;
using Foundry.Core.Sourcing;

namespace Foundry.Core.Export;

/// <summary>
/// Rendered artifacts the package needs but Core cannot produce itself: the wiring images come from WPF
/// visuals in Foundry.App, the mesh from the CAD sidecar over HTTP, the gerbers from KiCad. The caller
/// gathers them; this class only composes. Anything left null is reported as omitted, never faked.
/// </summary>
public sealed record PackageAssets
{
    public byte[]? WiringPng { get; init; }
    public byte[]? BreadboardPng { get; init; }

    /// <summary>Print-arranged enclosure mesh (the slicable one), with the extension it was built as.</summary>
    public byte[]? EnclosureMesh { get; init; }
    public string EnclosureMeshExt { get; init; } = "stl";

    /// <summary>A ready-made gerber/drill zip from the fabrication export, if one has been produced.</summary>
    public byte[]? GerberZip { get; init; }
}

/// <summary>What actually made it into the package, and what did not.</summary>
public sealed record PackageResult(string Path, long Bytes,
    IReadOnlyList<string> Included, IReadOnlyList<string> Omitted);

/// <summary>
/// A complete, self-describing hand-off of a project: the spec PDF, firmware sources, the printable
/// enclosure mesh, fabrication data, and every report — laid out in named folders under one root, with a
/// README that explains each file.
///
/// <para>
/// This supersedes <see cref="ProjectBundle"/> for sharing. That produced a <c>.foundryproj</c> holding the
/// project JSON plus a few text files: no PDF, no mesh, no gerbers, no wiring diagram, and no explanation
/// of what any of it was. A recipient without Foundry installed could not open the extension, and a
/// recipient WITH it still had to guess. The canonical <c>project.json</c> is still in here, so a package
/// re-imports exactly as a bundle did.
/// </para>
///
/// <para>
/// The README states what is present AND what is missing, with the reason. A hand-off that quietly omits
/// the enclosure because the CAD sidecar was offline is worse than one that says so — the recipient would
/// otherwise assume the design has no case.
/// </para>
/// </summary>
public static class ProjectPackage
{
    public const string Extension = ".zip";

    /// <summary>Compose the package at <paramref name="zipPath"/>. Never throws for a missing artifact.</summary>
    public static PackageResult Write(Project.Project project, string zipPath, PackageAssets? assets = null)
    {
        assets ??= new PackageAssets();

        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var root = Slug(project.Title);
        var included = new List<string>();
        var omitted = new List<string>();

        // Zip permits duplicate paths; extraction of one is then ambiguous and a file can be lost. The
        // generated firmware ALREADY contains a README.md, so a build guide of that name silently shadowed
        // it. Guard every entry, not just that one.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string name)
        {
            if (used.Add(name)) return name;
            var folder = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? "";
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var n = 2; ; n++)
            {
                var candidate = (folder.Length == 0 ? "" : folder + "/") + $"{stem}-{n}{ext}";
                if (used.Add(candidate)) return candidate;
            }
        }

        // Build in memory so a failure part-way cannot leave a truncated .zip where a good one was.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string name, string content, string label)
            {
                try
                {
                    var e = zip.CreateEntry($"{root}/{Unique(name)}", CompressionLevel.Optimal);
                    using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                    w.Write(content);
                    included.Add(label);
                }
                catch (Exception ex) { omitted.Add($"{label} — {ex.Message}"); }
            }

            void Bytes(string name, byte[]? content, string label, string whyMissing)
            {
                if (content is null || content.Length == 0) { omitted.Add($"{label} — {whyMissing}"); return; }
                try
                {
                    var e = zip.CreateEntry($"{root}/{Unique(name)}", CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(content, 0, content.Length);
                    included.Add(label);
                }
                catch (Exception ex) { omitted.Add($"{label} — {ex.Message}"); }
            }

            void Try(string name, Func<string> make, string label)
            {
                string content;
                try { content = make(); }
                catch (Exception ex) { omitted.Add($"{label} — {ex.Message}"); return; }
                Text(name, content, label);
            }

            // ---- the human entry point -------------------------------------------------------------
            // Written last in spirit but first in the archive; the manifest it prints is filled in after
            // the rest, so it can describe what really happened. Placeholder now, rewritten below.

            // ---- specification ---------------------------------------------------------------------
            byte[]? pdf = null;
            try { pdf = PdfExporter.ProjectPdf(project, assets.WiringPng); }
            catch (Exception ex)
            {
                // A wiring image the PDF engine cannot decode must not cost the recipient the ENTIRE
                // specification — the diagram is one figure in a document that also carries the BOM,
                // architecture and validation. Retry without it and say the figure is missing.
                Diagnostics.AppLog.Warn("export", $"spec PDF failed with the wiring image ({ex.Message}) — retrying without it.");
                try
                {
                    pdf = PdfExporter.ProjectPdf(project, null);
                    omitted.Add("the wiring diagram figure inside project-spec.pdf — the image could not be decoded");
                }
                catch (Exception inner) { omitted.Add($"project-spec.pdf — could not be rendered ({inner.Message})"); }
            }
            if (pdf is not null) Bytes("project-spec.pdf", pdf, "project-spec.pdf", "");

            // ---- electronics -----------------------------------------------------------------------
            Try("electronics/bom.csv", () => Exporters.BomCsv(project), "electronics/bom.csv");
            Try("electronics/bom-digikey.csv", () => CartLinks.DigiKeyBomCsv(project.Bom), "electronics/bom-digikey.csv");
            Try("electronics/netlist.net", () => KiCadNetlist.Export(project), "electronics/netlist.net");
            Try("electronics/pinout.csv", () => PinReport.Csv(project), "electronics/pinout.csv");
            Bytes("electronics/wiring-diagram.png", assets.WiringPng, "electronics/wiring-diagram.png",
                "not rendered (the wiring view was unavailable)");
            Bytes("electronics/breadboard.png", assets.BreadboardPng, "electronics/breadboard.png",
                "not rendered (the breadboard view was unavailable)");

            // ---- firmware --------------------------------------------------------------------------
            if (project.Firmware.Files.Count == 0)
                omitted.Add("firmware/ — the project has no generated firmware");
            else
            {
                // The project's own files first, so they keep their names unaltered; the build guide is
                // BUILD.md rather than README.md because generated firmware already ships a README.md.
                foreach (var f in project.Firmware.Files)
                    Text($"firmware/{SafeName(f.Name)}", f.Content, $"firmware/{SafeName(f.Name)}");
                Text("firmware/BUILD.md", FirmwareReadme(project), "firmware/BUILD.md");
            }

            // ---- enclosure -------------------------------------------------------------------------
            var ext = string.IsNullOrWhiteSpace(assets.EnclosureMeshExt) ? "stl" : assets.EnclosureMeshExt.ToLowerInvariant();
            Bytes($"enclosure/enclosure.{ext}", assets.EnclosureMesh, $"enclosure/enclosure.{ext}",
                "the CAD sidecar was offline or the mesh could not be built");
            if (assets.EnclosureMesh is { Length: > 0 })
                Text("enclosure/README.md", EnclosureReadme(project, ext), "enclosure/README.md");

            // ---- fabrication -----------------------------------------------------------------------
            Bytes("fabrication/gerbers.zip", assets.GerberZip, "fabrication/gerbers.zip",
                "no gerber export has been produced for this project yet");

            // ---- reports ---------------------------------------------------------------------------
            Try("reports/validation.md", () => Exporters.ValidationReport(project), "reports/validation.md");
            Try("docs/assembly-guide.md", () => Exporters.GuideMarkdown(project), "docs/assembly-guide.md");

            // ---- the re-importable original --------------------------------------------------------
            Try("foundry/project.json", () => ProjectStore.Serialize(project), "foundry/project.json");

            // README last: it reports the manifest above, so it must be built after everything else.
            Text("README.md", Readme(project, included, omitted), "README.md");
        }

        var bytes = buffer.ToArray();
        Exporters.WriteBytesUnlocked(zipPath, bytes);
        Diagnostics.AppLog.Info("export",
            $"project package → {zipPath} · {included.Count} artifacts, {omitted.Count} omitted");

        return new PackageResult(zipPath, bytes.LongLength, included, omitted);
    }

    // ---- README ---------------------------------------------------------------------------------

    internal static string Readme(Project.Project p, IReadOnlyList<string> included, IReadOnlyList<string> omitted)
    {
        var s = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        s.AppendLine($"# {Title(p)}");
        s.AppendLine();
        if (!string.IsNullOrWhiteSpace(p.Prompt))
        {
            s.AppendLine($"> {p.Prompt.Trim()}");
            s.AppendLine();
        }
        s.AppendLine($"Generated by Foundry {AppInfo.Version} · packaged {DateTime.Now:yyyy-MM-dd HH:mm}");
        s.AppendLine();
        s.AppendLine("---");
        s.AppendLine();

        // ---- the verdict, up front ----
        s.AppendLine("## Before you build this");
        s.AppendLine();
        s.AppendLine(VerdictLine(p));
        s.AppendLine();
        var counts = SeverityCounts(p);
        if (counts.Count > 0)
        {
            s.AppendLine("| Severity | Checks |");
            s.AppendLine("|---|---|");
            foreach (var (sev, n) in counts) s.AppendLine($"| {sev} | {n} |");
            s.AppendLine();
        }
        s.AppendLine("`unproven` means a check could not be completed — it is **not** a pass. " +
                     "See `reports/validation.md` for the detail.");
        s.AppendLine();
        s.AppendLine("Foundry is a design aid. Everything here is generated and must be reviewed by someone " +
                     "competent to review it before you apply power.");
        s.AppendLine();

        // ---- at a glance ----
        s.AppendLine("## At a glance");
        s.AppendLine();
        s.AppendLine("| | |");
        s.AppendLine("|---|---|");
        s.AppendLine($"| Components | {p.Kpis.Parts} |");
        s.AppendLine($"| Estimated cost | ${p.Kpis.Cost.ToString("0.00", inv)} |");
        if (p.Kpis.CurrentMa > 0) s.AppendLine($"| Active draw | {p.Kpis.CurrentMa} mA |");
        if (p.Kpis.BatteryDays > 0) s.AppendLine($"| Battery life | ~{p.Kpis.BatteryDays} days |");
        if (p.Firmware.Files.Count > 0) s.AppendLine($"| Firmware | {p.Firmware.Platform} |");
        if (Dims(p) is { } dims) s.AppendLine($"| Enclosure (inner) | {dims} |");
        if (p.Kpis.PrintGrams > 0) s.AppendLine($"| Print mass | ~{p.Kpis.PrintGrams} g |");
        s.AppendLine();
        s.AppendLine(PricingNote(p));
        s.AppendLine();

        // ---- contents ----
        s.AppendLine("## What's in this package");
        s.AppendLine();
        s.AppendLine("| Path | What it is |");
        s.AppendLine("|---|---|");
        foreach (var (path, what) in Descriptions(p))
            if (included.Contains(path))
                s.AppendLine($"| `{path}` | {what} |");
        s.AppendLine();

        // Everything under firmware/ except the build guide this class generates — the firmware's OWN
        // README.md is one of its sources and belongs in the list.
        var firmwareFiles = included.Where(i => i.StartsWith("firmware/", StringComparison.Ordinal)
                                             && !i.EndsWith("firmware/BUILD.md", StringComparison.Ordinal)).ToList();
        if (firmwareFiles.Count > 0)
        {
            s.AppendLine("**Firmware sources**");
            s.AppendLine();
            foreach (var f in firmwareFiles) s.AppendLine($"- `{f}`");
            s.AppendLine();
            s.AppendLine("See `firmware/BUILD.md` to build and flash.");
            s.AppendLine();
        }

        if (omitted.Count > 0)
        {
            s.AppendLine("## Not included");
            s.AppendLine();
            s.AppendLine("Listed so nothing is missing silently:");
            s.AppendLine();
            foreach (var o in omitted) s.AppendLine($"- {o}");
            s.AppendLine();
        }

        // ---- reuse ----
        s.AppendLine("## Opening this in Foundry");
        s.AppendLine();
        s.AppendLine("`foundry/project.json` is the complete, authoritative design. In Foundry choose " +
                     "**Import bundle** on the projects screen and select either this `.zip` or that file — " +
                     "the netlist, components, firmware and enclosure all round-trip exactly.");
        s.AppendLine();

        return s.ToString();
    }

    private static string VerdictLine(Project.Project p) => p.Validation switch
    {
        "fail" => "**This design has FAILING checks.** Do not build it until they are resolved — see `reports/validation.md`.",
        "warn" => "**This design passed with warnings.** Review them before building.",
        "unproven" => "**Some checks could not be completed**, so this is not a clean bill of health.",
        "pass" => "Deterministic checks passed. Still verify against the datasheets before applying power.",
        _ => "This design has not been validated.",
    };

    private static string PricingNote(Project.Project p)
    {
        var live = p.Bom.Count(b => b.IsLive);
        if (p.Bom.Count == 0) return "";
        return live == 0
            ? "> Prices are **generated estimates**, not distributor quotes, and stock/lead time were not " +
              "checked. The `Source` column in `electronics/bom.csv` marks every line `EST`."
            : live < p.Bom.Count
                ? $"> {live} of {p.Bom.Count} BOM lines carry live distributor pricing (`LIVE` in the " +
                  "`Source` column); the rest are estimates."
                : "> All BOM lines carry live distributor pricing at the time of packaging.";
    }

    private static IReadOnlyList<(string Sev, int N)> SeverityCounts(Project.Project p) =>
        new[] { "fail", "warn", "unproven", "pass" }
            .Select(sev => (sev, p.Findings.Count(f => f.Severity == sev)))
            .Where(x => x.Item2 > 0)
            .ToList();

    private static IReadOnlyList<(string Path, string What)> Descriptions(Project.Project p) => new[]
    {
        ("project-spec.pdf", "The full illustrated specification — architecture, BOM, wiring, validation."),
        ("electronics/bom.csv", "Bill of materials. `Source` marks each line `LIVE` (distributor-quoted) or `EST`."),
        ("electronics/bom-digikey.csv", "The same BOM in DigiKey's cart-upload format."),
        ("electronics/netlist.net", "KiCad netlist — the authoritative wiring, importable into a PCB tool."),
        ("electronics/pinout.csv", "Every MCU pin assignment, derived from the netlist."),
        ("electronics/wiring-diagram.png", "Schematic-style wiring diagram."),
        ("electronics/breadboard.png", "Breadboard layout view."),
        ($"enclosure/enclosure.stl", "Printable enclosure, arranged flat on the plate for slicing."),
        ($"enclosure/enclosure.3mf", "Printable enclosure, arranged flat on the plate for slicing."),
        ("enclosure/README.md", "Enclosure dimensions, print settings and fit notes."),
        ("fabrication/gerbers.zip", "Gerber + drill files for PCB fabrication."),
        ("reports/validation.md", "Every deterministic check with its verdict and reasoning."),
        ("docs/assembly-guide.md", "Step-by-step build instructions."),
        ("firmware/BUILD.md", "How to build and flash the firmware."),
        ("foundry/project.json", "The complete design, re-importable into Foundry."),
    };

    // ---- per-folder READMEs ------------------------------------------------------------------------

    internal static string FirmwareReadme(Project.Project p)
    {
        var s = new StringBuilder();
        var micropython = p.Firmware.Platform.Contains("python", StringComparison.OrdinalIgnoreCase);

        s.AppendLine($"# Firmware — {Title(p)}");
        s.AppendLine();
        s.AppendLine($"**Platform:** {p.Firmware.Platform}  ");
        if (!string.IsNullOrWhiteSpace(p.Firmware.Board)) s.AppendLine($"**Board:** `{p.Firmware.Board}`  ");
        s.AppendLine();

        s.AppendLine("## Pin assignments are generated");
        s.AppendLine();
        s.AppendLine($"`{(micropython ? "pinmap.py" : "pinmap.h")}` is derived from the netlist, not hand-written. " +
                     "Every pin the firmware touches comes from there. If you rewire the design, regenerate it " +
                     "rather than editing the constants — that file is the link between the schematic and the code.");
        s.AppendLine();

        if (p.Firmware.Libraries.Count > 0)
        {
            s.AppendLine("## Libraries");
            s.AppendLine();
            foreach (var l in p.Firmware.Libraries)
                s.AppendLine($"- {l.Key}{(string.IsNullOrWhiteSpace(l.Value) ? "" : $" ({l.Value})")}");
            s.AppendLine();
        }

        s.AppendLine("## Build and flash");
        s.AppendLine();
        if (micropython)
        {
            s.AppendLine("MicroPython is not compiled — copy the sources to the board:");
            s.AppendLine();
            s.AppendLine("```");
            s.AppendLine("mpremote connect auto fs cp pinmap.py :pinmap.py");
            s.AppendLine("mpremote connect auto fs cp main.py :main.py");
            s.AppendLine("mpremote connect auto reset");
            s.AppendLine("```");
        }
        else
        {
            var fqbn = string.IsNullOrWhiteSpace(p.Firmware.Board) ? "<fqbn>" : p.Firmware.Board;
            s.AppendLine("With [arduino-cli](https://arduino.github.io/arduino-cli/):");
            s.AppendLine();
            s.AppendLine("```");
            s.AppendLine($"arduino-cli compile --fqbn {fqbn} .");
            s.AppendLine($"arduino-cli upload  --fqbn {fqbn} -p <COM port> .");
            s.AppendLine("```");
            s.AppendLine();
            s.AppendLine("The primary sketch must sit in a folder of the same name for the Arduino toolchain — " +
                         "rename the sketch, or its folder, so the two match.");
        }
        s.AppendLine();
        s.AppendLine("Credentials and other secrets are placeholders in the config file; fill them in before flashing.");
        s.AppendLine();
        return s.ToString();
    }

    internal static string EnclosureReadme(Project.Project p, string ext)
    {
        var s = new StringBuilder();
        s.AppendLine($"# Enclosure — {Title(p)}");
        s.AppendLine();
        if (Dims(p) is { } dims) s.AppendLine($"**Inner dimensions:** {dims}  ");
        s.AppendLine($"**Wall:** {p.Enclosure.Wall.ToString("0.#", CultureInfo.InvariantCulture)} mm  ");
        s.AppendLine($"**Lid:** {p.Enclosure.Lid}  ");
        if (p.Enclosure.Standoffs > 0) s.AppendLine($"**PCB standoffs:** {p.Enclosure.Standoffs} mm  ");
        if (p.Kpis.PrintGrams > 0) s.AppendLine($"**Estimated material:** ~{p.Kpis.PrintGrams} g  ");
        if (!string.IsNullOrWhiteSpace(p.Enclosure.PrintTime)) s.AppendLine($"**Estimated print time:** {p.Enclosure.PrintTime}  ");
        s.AppendLine();

        s.AppendLine("## Printing");
        s.AppendLine();
        s.AppendLine($"`enclosure.{ext}` is exported **print-arranged** — every body is laid flat on the plate and " +
                     "separated, so it slices as-is. The preview inside Foundry shows the parts assembled instead; " +
                     "that arrangement is not slicable and is never what gets exported.");
        s.AppendLine();
        s.AppendLine("No supports should be needed for the base. Print the lid face-down.");
        s.AppendLine();

        if (p.Enclosure.Cutouts.Count > 0)
        {
            s.AppendLine("## Openings");
            s.AppendLine();
            s.AppendLine("| Label | Face | Size |");
            s.AppendLine("|---|---|---|");
            foreach (var c in p.Enclosure.Cutouts)
                s.AppendLine($"| {(string.IsNullOrWhiteSpace(c.Label) ? c.Ref ?? "—" : c.Label)} | {c.Face} | {c.DimsText} |");
            s.AppendLine();
            s.AppendLine("Check these against your actual parts before printing — a port in the wrong place is the " +
                         "most expensive mistake a case can make, because it prints perfectly and is simply wrong.");
            s.AppendLine();
        }
        return s.ToString();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string Title(Project.Project p) =>
        string.IsNullOrWhiteSpace(p.Title) ? "Untitled project" : p.Title.Trim();

    private static string? Dims(Project.Project p)
    {
        var i = p.Enclosure.Inner;
        if (i is not { Length: >= 3 } || i.Take(3).All(v => v <= 0)) return null;
        var inv = CultureInfo.InvariantCulture;
        return $"{i[0].ToString("0.#", inv)} × {i[1].ToString("0.#", inv)} × {i[2].ToString("0.#", inv)} mm";
    }

    /// <summary>
    /// Folder-safe project name, used both for the archive root (so it extracts into one tidy directory)
    /// and by callers naming the output file.
    /// </summary>
    public static string Slug(string title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return "foundry-project";

        // Everything that is not alphanumeric collapses to '-'. Deliberately includes '.', '/' and '\':
        // this string becomes a path segment inside the archive, so it must not be able to introduce a
        // separator or a traversal component.
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');

        var slug = sb.ToString();
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        slug = slug.Trim('-', '.');

        // Long names push entries past path limits once extracted somewhere already deep.
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-', '.');
        return slug.Length == 0 ? "foundry-project" : slug;
    }

    private static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.txt" : name;
    }
}
