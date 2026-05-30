using System.IO.Compression;
using Foundry.Core.Pcb;
using Foundry.Core.Pcb.Fab;

namespace Foundry.Tests;

// ---- FabOptions defaults --------------------------------------------------------------------------

public class FabOptionsTests
{
    [Fact]
    public void Default_Is2LayerSet_SeparateTh_WithDrillMap()
    {
        var o = FabOptions.Default;
        Assert.Equal(FabOptions.DefaultLayers, o.Layers);
        Assert.True(o.SeparateTh);
        Assert.True(o.GenerateDrillMap);
    }

    [Fact]
    public void DefaultLayers_AreTheNineKiCad9Tokens()
    {
        var tokens = FabOptions.DefaultLayers.Split(',');
        Assert.Equal(9, tokens.Length);
        Assert.Equal(new[]
        {
            "F.Cu", "B.Cu", "F.Paste", "B.Paste",
            "F.Silkscreen", "B.Silkscreen", "F.Mask", "B.Mask", "Edge.Cuts",
        }, tokens);
    }
}

// ---- GerberExporter.BuildGerberArgs / BuildDrillArgs (pure recipe) -------------------------------

public class GerberExporterArgsTests
{
    [Fact]
    public void BuildGerberArgs_EmitsExactRecipe_WithStandardLayerSet()
    {
        var args = GerberExporter.BuildGerberArgs("C:/tmp/board.kicad_pcb", "C:/tmp/out");

        Assert.Contains("pcb export gerbers", args);
        Assert.Contains("--output \"C:/tmp/out\"", args);
        Assert.Contains($"--layers \"{FabOptions.DefaultLayers}\"", args);
        Assert.Contains("--subtract-soldermask", args);
        Assert.Contains("--use-drill-file-origin", args);
        Assert.Contains("\"C:/tmp/board.kicad_pcb\"", args);
    }

    [Fact]
    public void BuildGerberArgs_KeepsProtelAndX2_NoOptOutFlags()
    {
        var args = GerberExporter.BuildGerberArgs("b.kicad_pcb", "out");
        Assert.DoesNotContain("--no-protel-ext", args);
        Assert.DoesNotContain("--no-x2", args);
    }

    [Fact]
    public void BuildDrillArgs_EmitsExcellonMmDecimalAndPlotOrigin()
    {
        var args = GerberExporter.BuildDrillArgs("C:/tmp/board.kicad_pcb", "C:/tmp/out");

        Assert.Contains("pcb export drill", args);
        Assert.Contains("--format excellon", args);
        Assert.Contains("--drill-origin plot", args);
        Assert.Contains("--excellon-units mm", args);
        Assert.Contains("--excellon-zeros-format decimal", args);
        Assert.Contains("\"C:/tmp/board.kicad_pcb\"", args);
    }

    [Fact]
    public void BuildDrillArgs_SeparateTh_AndDrillMap_OnByDefault()
    {
        var args = GerberExporter.BuildDrillArgs("b.kicad_pcb", "out");
        Assert.Contains("--excellon-separate-th", args);
        Assert.Contains("--generate-map", args);
        Assert.Contains("--map-format gerberx2", args);
    }

    [Fact]
    public void BuildDrillArgs_OptionsOff_OmitsSeparateThAndMap()
    {
        var args = GerberExporter.BuildDrillArgs("b.kicad_pcb", "out",
            new FabOptions(SeparateTh: false, GenerateDrillMap: false));
        Assert.DoesNotContain("--excellon-separate-th", args);
        Assert.DoesNotContain("--generate-map", args);
        Assert.DoesNotContain("--map-format", args);
    }

    [Fact]
    public void BuildDrillArgs_OutputDir_HasNoTrailingSeparatorInsideQuotes()
    {
        var args = GerberExporter.BuildDrillArgs("b.kicad_pcb", "C:/tmp/out");
        // A quoted path ending in a backslash escapes the closing quote on Windows and breaks kicad-cli,
        // so the output dir must be quoted WITHOUT a trailing separator.
        Assert.Contains("--output \"C:/tmp/out\"", args);
    }

    [Fact]
    public void BuildDrillArgs_StripsCallerTrailingSeparator()
    {
        // Even if the caller passes a trailing separator, it must be stripped (no '...\"' in the args).
        var args = GerberExporter.BuildDrillArgs("b.kicad_pcb", @"C:\tmp\out\");
        Assert.Contains("--output \"C:\\tmp\\out\"", args);
        Assert.DoesNotContain("\\\"", args.Substring(args.IndexOf("--output", System.StringComparison.Ordinal)));
    }
}

// ---- FabFileSet.Validate (pure, extension-presence) ----------------------------------------------

public class FabFileSetTests
{
    private static readonly string[] Complete =
    {
        "board-F_Cu.gtl", "board-B_Cu.gbl", "board-Edge_Cuts.gm1",
        "board-PTH.drl", "board-NPTH.drl",
    };

    [Fact]
    public void Validate_CompleteSet_Passes()
    {
        var v = FabFileSet.Validate(Complete);
        Assert.True(v.Ok);
        Assert.Empty(v.Missing);
    }

    [Fact]
    public void Validate_AcceptsFullPaths_ProbesByExtensionNotStem()
    {
        var v = FabFileSet.Validate(new[]
        {
            "/work/foundry_fab/anything.gtl",
            "/work/foundry_fab/whatever.gbl",
            "/work/foundry_fab/outline.gm1",
            "/work/foundry_fab/holes.drl",
        });
        Assert.True(v.Ok);
    }

    [Fact]
    public void Validate_MissingBackCopper_Fails_NamesMissing()
    {
        var v = FabFileSet.Validate(new[] { "a.gtl", "edge.gm1", "h.drl" });
        Assert.False(v.Ok);
        Assert.Contains(v.Missing, m => m.Contains("gbl"));
    }

    [Fact]
    public void Validate_MissingDrill_Fails()
    {
        var v = FabFileSet.Validate(new[] { "a.gtl", "b.gbl", "edge.gm1" });
        Assert.False(v.Ok);
        Assert.Contains(v.Missing, m => m.Contains("drl"));
    }

    [Fact]
    public void Validate_Empty_FailsWithAllClassesMissing()
    {
        var v = FabFileSet.Validate(Array.Empty<string>());
        Assert.False(v.Ok);
        Assert.Equal(4, v.Missing.Count);
    }

    [Fact]
    public void Validate_NullAndBlankEntries_AreIgnored_NeverThrows()
    {
        var v = FabFileSet.Validate(new[] { null!, "", "   ", "a.gtl", "b.gbl", "e.gm1", "h.drl" });
        Assert.True(v.Ok);
    }

    [Fact]
    public void Validate_ExtensionMatchIsCaseInsensitive()
    {
        var v = FabFileSet.Validate(new[] { "A.GTL", "B.GBL", "E.GM1", "H.DRL" });
        Assert.True(v.Ok);
    }
}

// ---- FabExportResult.NotInstalled / Failed / Parse (pure success gate) ---------------------------

public class FabExportResultFactoryTests
{
    [Fact]
    public void NotInstalled_SurfacesKiCadDownloadGuidance()
    {
        var r = FabExportResult.NotInstalled();
        Assert.False(r.Installed);
        Assert.False(r.Ok);
        Assert.Null(r.ZipPath);
        Assert.Empty(r.Files);
        Assert.Empty(r.Notes);
        Assert.Contains(KiCadInstaller.DownloadUrl, r.Summary);
    }

    [Fact]
    public void Failed_IsInstalledButNotOk_CarriesNotesAndFiles()
    {
        var r = FabExportResult.Failed("boom", new[] { "detail" }, new[] { "partial.gtl" });
        Assert.True(r.Installed);
        Assert.False(r.Ok);
        Assert.Null(r.ZipPath);
        Assert.Equal("boom", r.Summary);
        Assert.Contains("detail", r.Notes);
        Assert.Contains("partial.gtl", r.Files);
    }

    [Fact]
    public void Failed_NullNotesAndFiles_AreEmpty()
    {
        var r = FabExportResult.Failed("nope");
        Assert.Empty(r.Notes);
        Assert.Empty(r.Files);
    }
}

public class FabExportResultParseTests
{
    private static readonly string[] Complete =
    {
        "board-F_Cu.gtl", "board-B_Cu.gbl", "board-Edge_Cuts.gm1", "board-PTH.drl",
    };

    [Fact]
    public void Parse_AllPresent_BothExitsZero_ZipWritten_IsOk()
    {
        var r = FabExportResult.Parse(0, null, 0, null, Complete, "C:/out/board-fab.zip", 4);

        Assert.True(r.Installed);
        Assert.True(r.Ok);
        Assert.Equal("C:/out/board-fab.zip", r.ZipPath);
        Assert.Equal(Complete, r.Files);
        Assert.Contains("board-fab.zip", r.Summary);
    }

    [Fact]
    public void Parse_GerberExitNonZero_FailsBeforeFileGate_CarriesStderr()
    {
        var r = FabExportResult.Parse(2, "kicad-cli: cannot open board", 0, null, Complete, null, 0);
        Assert.True(r.Installed);
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("cannot open board"));
    }

    [Fact]
    public void Parse_DrillExitNonZero_Fails()
    {
        var r = FabExportResult.Parse(0, null, 3, "drill blew up", Complete, null, 0);
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("drill blew up"));
    }

    [Fact]
    public void Parse_ExitsZero_ButMissingFile_FailsAtFileGate()
    {
        // Missing the back copper gerber → the file-set gate trips even though both exits are 0.
        var incomplete = new[] { "board-F_Cu.gtl", "board-Edge_Cuts.gm1", "board-PTH.drl" };
        var r = FabExportResult.Parse(0, null, 0, null, incomplete, "C:/out/board-fab.zip", 3);
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("Missing"));
        Assert.Null(r.ZipPath);
    }

    [Fact]
    public void Parse_ExitsAndFilesOk_ButNoZip_FailsAtZipGate()
    {
        var r = FabExportResult.Parse(0, null, 0, null, Complete, null, 0);
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("ZIP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_ExitsAndFilesOk_ButZeroZipEntries_Fails()
    {
        var r = FabExportResult.Parse(0, null, 0, null, Complete, "C:/out/board-fab.zip", 0);
        Assert.False(r.Ok);
    }
}

// ---- GerberExporter.ExportAsync — degrade + real ZIP assembly with fake files --------------------

public class GerberExporterExportTests
{
    [Fact]
    public async Task ExportAsync_ReturnsNotInstalled_WhenKiCadAbsent()
    {
        // KiCad isn't installed here — assert graceful degradation, never a throw.
        if (KiCadInstaller.Locate() is not null) return;   // guard: real install present, skip

        var tmp = Path.Combine(Path.GetTempPath(),
            "foundry_fab_in_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        var outDir = Path.Combine(Path.GetTempPath(), "foundry_fab_out_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var r = await GerberExporter.ExportAsync(tmp, outDir);
            Assert.False(r.Installed);
            Assert.False(r.Ok);
            Assert.Null(r.ZipPath);
        }
        finally
        {
            File.Delete(tmp);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public async Task ExportAsync_NotInstalled_TakesPrecedenceOverMissingInput()
    {
        if (KiCadInstaller.Locate() is not null) return;
        var r = await GerberExporter.ExportAsync("Z:/does/not/exist.kicad_pcb",
            Path.Combine(Path.GetTempPath(), "foundry_fab_out_" + Guid.NewGuid().ToString("N")[..8]));
        Assert.False(r.Installed);
        Assert.False(r.Ok);
    }

    // The ZIP packaging step is driven directly with REAL temp files (not kicad-cli): a complete
    // fab set in a work dir must zip into exactly those entries. This mirrors ExportAsync's bundle step.
    [Fact]
    public void ZipAssembly_FromCompleteFabSet_ContainsExactlyThoseEntries()
    {
        var work = Path.Combine(Path.GetTempPath(), "foundry_fabzip_work_" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(Path.GetTempPath(), "foundry_fabzip_out_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var expected = new[]
        {
            "board-F_Cu.gtl", "board-B_Cu.gbl", "board-Edge_Cuts.gm1",
            "board-PTH.drl", "board-NPTH.drl",
        };
        try
        {
            foreach (var n in expected) File.WriteAllText(Path.Combine(work, n), "fake gerber data");

            var produced = Directory.GetFiles(work);
            Assert.True(FabFileSet.Validate(produced).Ok);

            Directory.CreateDirectory(outDir);
            var zipPath = Path.Combine(outDir, "board-fab.zip");
            ZipFile.CreateFromDirectory(work, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            using var zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Select(e => e.FullName).OrderBy(x => x).ToArray();
            Assert.Equal(expected.OrderBy(x => x).ToArray(), entries);

            var result = FabExportResult.Parse(0, null, 0, null,
                produced.Select(Path.GetFileName).ToList()!, zipPath, zip.Entries.Count);
            Assert.True(result.Ok);
            Assert.Equal(zipPath, result.ZipPath);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
}

// ---- PcbDesigner.DesignAndExportFabAsync — degrade end-to-end (KiCad absent) ---------------------

public class PcbDesignerFabExportTests
{
    [Fact]
    public async Task DesignAndExportFab_DegradesToNotInstalled_WhenKiCadAbsent()
    {
        if (KiCadInstaller.Locate() is not null) return;

        var project = new Foundry.Core.Project.Project { Title = "Tiny" };
        var outDir = Path.Combine(Path.GetTempPath(), "foundry_fab_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (design, fab) = await PcbDesigner.DesignAndExportFabAsync(project, outDir);
            Assert.False(design.Installed);
            Assert.False(fab.Installed);
            Assert.False(fab.Ok);
            Assert.Null(fab.ZipPath);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
}
