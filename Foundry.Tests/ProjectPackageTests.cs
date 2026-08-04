using System.IO.Compression;
using Foundry.Core.Export;
using Foundry.Core.Validation;
using Foundry.Core.Project;

namespace Foundry.Tests;

// The shareable hand-off. ProjectBundle produced a .foundryproj holding project.json plus a few text files:
// no PDF, no mesh, no gerbers, no wiring diagram, and nothing telling the recipient what any of it was.
public class ProjectPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foundry-pkg-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectPackageTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Zip => Path.Combine(_dir, "out.zip");

    /// <summary>A real 1x1 PNG — the PDF engine decodes the wiring image, so a stub byte[] is not enough.</summary>
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private static Project Demo() => DemoData.CreateSoilMoistureProject();

    private static List<string> Entries(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return z.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>Read one entry by its path RELATIVE to the archive root folder.</summary>
    private static string Read(string zip, string relative)
    {
        using var z = ZipFile.OpenRead(zip);
        var e = z.Entries.Single(x => x.FullName.Split('/', 2)[1].Equals(relative, StringComparison.OrdinalIgnoreCase));
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    // ---- structure ---------------------------------------------------------------------------------

    [Fact]
    public void EverythingLivesUnderOneNamedFolder()
    {
        ProjectPackage.Write(Demo(), Zip);
        var roots = Entries(Zip).Select(e => e.Split('/')[0]).Distinct().ToList();

        // A zip that explodes loose files into the recipient's Downloads folder is a bad hand-off.
        Assert.Single(roots);
        Assert.Contains("Soil", roots[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDeliverablesArePresent()
    {
        ProjectPackage.Write(Demo(), Zip);
        var names = Entries(Zip);

        foreach (var expected in new[]
                 {
                     "README.md", "project-spec.pdf", "electronics/bom.csv", "electronics/netlist.net",
                     "electronics/pinout.csv", "reports/validation.md", "docs/assembly-guide.md",
                     "foundry/project.json", "firmware/BUILD.md",
                 })
            Assert.Contains(names, n => n.EndsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TheFirmwareSourcesAreIncluded()
    {
        var p = Demo();
        ProjectPackage.Write(p, Zip);
        var names = Entries(Zip);

        Assert.NotEmpty(p.Firmware.Files);
        foreach (var f in p.Firmware.Files)
            Assert.Contains(names, n => n.EndsWith($"firmware/{f.Name}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThePdfIsARealPdf()
    {
        ProjectPackage.Write(Demo(), Zip);
        using var z = ZipFile.OpenRead(Zip);
        var e = z.Entries.First(x => x.FullName.EndsWith("project-spec.pdf", StringComparison.Ordinal));

        using var s = e.Open();
        var head = new byte[5];
        s.ReadExactly(head);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(head));
        Assert.True(e.Length > 1000, "a one-page-per-section spec should not be a stub");
    }

    // ---- supplied assets ---------------------------------------------------------------------------

    [Fact]
    public void SuppliedAssetsAreWrittenVerbatim()
    {
        var stl = new byte[] { 1, 2, 3, 4, 5 };
        var png = TinyPng;
        ProjectPackage.Write(Demo(), Zip, new PackageAssets
        {
            EnclosureMesh = stl, EnclosureMeshExt = "stl", WiringPng = png,
        });

        using var z = ZipFile.OpenRead(Zip);
        var mesh = z.Entries.First(e => e.FullName.EndsWith("enclosure/enclosure.stl", StringComparison.Ordinal));
        using var ms = new MemoryStream();
        mesh.Open().CopyTo(ms);
        Assert.Equal(stl, ms.ToArray());
    }

    [Fact]
    public void The3mfExtensionIsHonoured()
    {
        ProjectPackage.Write(Demo(), Zip, new PackageAssets
        {
            EnclosureMesh = new byte[] { 1 }, EnclosureMeshExt = "3mf",
        });
        Assert.Contains(Entries(Zip), n => n.EndsWith("enclosure/enclosure.3mf", StringComparison.Ordinal));
    }

    // ---- honesty about what is missing -------------------------------------------------------------
    //
    // A hand-off that quietly omits the enclosure because the sidecar was offline is worse than one that
    // says so: the recipient assumes the design simply has no case.

    [Fact]
    public void AMissingMeshIsReported_NotSilentlyDropped()
    {
        var r = ProjectPackage.Write(Demo(), Zip);

        Assert.DoesNotContain(Entries(Zip), n => n.Contains("enclosure.stl", StringComparison.Ordinal));
        Assert.Contains(r.Omitted, o => o.Contains("enclosure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Not included", Read(Zip, "README.md"));
        Assert.Contains("enclosure", Read(Zip, "README.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenEverythingIsSupplied_NothingIsListedAsMissing()
    {
        var r = ProjectPackage.Write(Demo(), Zip, new PackageAssets
        {
            WiringPng = TinyPng, BreadboardPng = TinyPng,
            EnclosureMesh = new byte[] { 1 }, GerberZip = new byte[] { 1 },
        });
        Assert.True(r.Omitted.Count == 0, "omitted: " + string.Join(" | ", r.Omitted));
        Assert.DoesNotContain("Not included", Read(Zip, "README.md"));
    }

    // A wiring image the PDF engine cannot decode must cost the recipient one FIGURE, not the entire
    // specification -- the same document also carries the BOM, architecture and validation.
    [Fact]
    public void AnUndecodableWiringImage_StillProducesTheSpecPdf()
    {
        var r = ProjectPackage.Write(Demo(), Zip, new PackageAssets { WiringPng = new byte[] { 1, 2, 3 } });

        Assert.Contains("project-spec.pdf", r.Included);
        Assert.Contains(r.Omitted, o => o.Contains("wiring diagram figure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Entries(Zip), n => n.EndsWith("project-spec.pdf", StringComparison.Ordinal));
    }

    // The generated firmware already ships a README.md; a build guide of that name silently shadowed it.
    [Fact]
    public void TheFirmwaresOwnReadmeIsNotShadowedByTheBuildGuide()
    {
        var p = Demo();
        Assert.Contains(p.Firmware.Files, f => f.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase));

        ProjectPackage.Write(p, Zip);
        var names = Entries(Zip);

        Assert.Contains(names, n => n.EndsWith("firmware/README.md", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("firmware/BUILD.md", StringComparison.Ordinal));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void NoTwoEntriesEverShareAPath()
    {
        var p = Demo();
        p.Firmware.Files.Add(new FirmwareFile { Name = "main.ino", Content = "// a duplicate name" });

        ProjectPackage.Write(p, Zip);
        var names = Entries(Zip);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // The list must name the firmware's own files, not the guide this packager writes.
    [Fact]
    public void TheReadmeListsEveryFirmwareSource_AndNotTheBuildGuide()
    {
        var p = Demo();
        ProjectPackage.Write(p, Zip);
        var readme = Read(Zip, "README.md");

        foreach (var f in p.Firmware.Files)
            Assert.Contains($"`firmware/{f.Name}`", readme);
        Assert.DoesNotContain("- `firmware/BUILD.md`", readme);
    }

    [Fact]
    public void TheResultListsWhatWentIn()
    {
        var r = ProjectPackage.Write(Demo(), Zip);
        Assert.Contains("README.md", r.Included);
        Assert.Contains("foundry/project.json", r.Included);
        Assert.Equal(new FileInfo(Zip).Length, r.Bytes);
    }

    // ---- the README carries the caveats that matter ------------------------------------------------

    [Fact]
    public void TheReadmeLeadsWithTheValidationVerdict()
    {
        var p = Demo();
        ProjectValidator.Revalidate(p);
        ProjectPackage.Write(p, Zip);
        var readme = Read(Zip, "README.md");

        Assert.Contains("Before you build this", readme);
        Assert.Contains("design aid", readme, StringComparison.OrdinalIgnoreCase);
        // unproven must not be allowed to read as a pass in a document someone else acts on.
        Assert.Contains("unproven", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not** a pass", readme);
    }

    [Fact]
    public void AFailingDesign_SaysSoInBold()
    {
        var p = Demo();
        p.Validation = "fail";
        ProjectPackage.Write(p, Zip);
        Assert.Contains("FAILING", Read(Zip, "README.md"));
    }

    // Prices are generated estimates unless a distributor answered — the recipient is the person most
    // likely to act on them, and least able to know.
    [Fact]
    public void TheReadmeSaysWhetherPricesAreRealOrEstimated()
    {
        var p = Demo();
        ProjectPackage.Write(p, Zip);
        Assert.Contains("estimates", Read(Zip, "README.md"), StringComparison.OrdinalIgnoreCase);

        foreach (var b in p.Bom) { b.PriceSource = "DigiKey"; b.PricedAtUtc = DateTime.UtcNow; }
        var zip2 = Path.Combine(_dir, "live.zip");
        ProjectPackage.Write(p, zip2);
        Assert.Contains("live distributor pricing", Read(zip2, "README.md"));
    }

    [Fact]
    public void TheFirmwareReadmeExplainsTheGeneratedPinMap()
    {
        ProjectPackage.Write(Demo(), Zip);
        var fw = Read(Zip, "firmware/BUILD.md");

        Assert.Contains("pinmap.h", fw);
        Assert.Contains("derived from the netlist", fw);
        Assert.Contains("arduino-cli", fw);
    }

    [Fact]
    public void TheEnclosureReadmeWarnsThatTheExportIsPrintArranged()
    {
        ProjectPackage.Write(Demo(), Zip, new PackageAssets { EnclosureMesh = new byte[] { 1 } });
        var enc = Read(Zip, "enclosure/README.md");

        Assert.Contains("print-arranged", enc);
        Assert.Contains("not slicable", enc);   // about the in-app preview arrangement
    }

    // ---- it must come back in ----------------------------------------------------------------------

    [Fact]
    public void APackageReImportsAsTheSameProject()
    {
        var p = Demo();
        ProjectPackage.Write(p, Zip);

        var back = ProjectBundle.Import(Zip);

        Assert.Equal(p.Title, back.Title);
        Assert.Equal(p.Components.Count, back.Components.Count);
        Assert.Equal(p.Connections.Count, back.Connections.Count);
        Assert.Equal(p.Firmware.Files.Count, back.Firmware.Files.Count);
    }

    [Fact]
    public void TheOlderFoundryprojBundleStillImports()
    {
        var proj = Path.Combine(_dir, "old.foundryproj");
        ProjectBundle.Export(Demo(), proj);
        Assert.Equal(Demo().Title, ProjectBundle.Import(proj).Title);
    }

    [Fact]
    public void AZipThatIsNotAFoundryPackage_IsRejectedClearly()
    {
        var junk = Path.Combine(_dir, "junk.zip");
        using (var z = ZipFile.Open(junk, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");

        var ex = Assert.Throws<InvalidDataException>(() => ProjectBundle.Import(junk));
        Assert.Contains("project.json", ex.Message);
    }

    // ---- naming -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Cap. Soil Moisture Sentinel", "Cap-Soil-Moisture-Sentinel")]
    [InlineData("BitTick / BTC Ticker", "BitTick-BTC-Ticker")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("", "foundry-project")]
    [InlineData("???", "foundry-project")]
    public void SlugMakesASafeFolderName(string title, string expected) =>
        Assert.Equal(expected, ProjectPackage.Slug(title));

    [Fact]
    public void SlugStaysShortEnoughToExtractSomewhereDeep() =>
        Assert.True(ProjectPackage.Slug(new string('x', 200)).Length <= 60);

    [Fact]
    public void SlugNeverContainsAPathSeparator()
    {
        foreach (var t in new[] { "a/b", "a\\b", "../../etc", "C:\\Windows" })
        {
            var s = ProjectPackage.Slug(t);
            Assert.DoesNotContain('/', s);
            Assert.DoesNotContain('\\', s);
            Assert.DoesNotContain("..", s, StringComparison.Ordinal);
        }
    }

    // ---- rewriting an existing package --------------------------------------------------------------

    [Fact]
    public void ExportingTwiceOverwritesCleanly()
    {
        ProjectPackage.Write(Demo(), Zip);
        var first = new FileInfo(Zip).Length;
        ProjectPackage.Write(Demo(), Zip);

        Assert.Equal(first, new FileInfo(Zip).Length);
        Assert.NotEmpty(Entries(Zip));   // still a readable archive, not appended garbage
    }
}
