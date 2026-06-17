using System.IO.Compression;
using Foundry.Core.Kb;
using Foundry.Core.Pcb;
using Foundry.Core.Pcb.Fab;
using Foundry.Core.Project;

namespace Foundry.Tests;

// Live END-TO-END test of the ORDERABLE artifact: prompt-shaped project -> build -> route (FreeRouting) ->
// DRC -> gerber/drill -> fab ZIP, run against the REAL KiCad + Java + FreeRouting toolchain. This is the half
// of the moat (route/DRC/gerber) that the rest of the suite only exercised against fakes — here we assert the
// design actually reaches a DRC-CLEAN verdict and that a real, validated gerber ZIP is produced (the thing a
// user would send to a board house). Skips cleanly when the toolchain is incomplete; the pcb-live CI lane
// installs KiCad + Java 25 + the FreeRouting jar so it runs for real and gates the release.
public class PcbLiveFabTests
{
    private static bool ToolchainPresent =>
        KiCadInstaller.Locate() is not null && FreeRoutingInstaller.Locate() is not null;

    private static string OutDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "foundry_fab_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task PassivesBoard_DesignsToDrcClean_AndExportsAValidGerberZip()
    {
        if (!ToolchainPresent) return;   // bare box: needs KiCad + Java 25 + FreeRouting jar (pcb-live CI has them)

        // A tiny, fully-verifiable board (numeric passive pins match footprint pads by name — no pin-map risk),
        // so any DRC error/unconnected is a real routing/build defect, not a mis-map. This is the orderable path.
        var p = new Project
        {
            Title = "RC fab e2e",
            Components = new()
            {
                new ComponentSpec { Alias = "R1", Ref = "r1", Name = "10k resistor" },
                new ComponentSpec { Alias = "R2", Ref = "r2", Name = "10k resistor" },
                new ComponentSpec { Alias = "C1", Ref = "c1", Name = "100nF capacitor" },
            },
            Connections = new()
            {
                new Connection { From = "R1.1", To = "R2.1", Net = "vin" },
                new Connection { From = "R1.2", To = "C1.1", Net = "vout" },
                new Connection { From = "R2.2", To = "C1.2", Net = "gnd" },
            },
        };

        var outDir = OutDir();
        try
        {
            var (design, fab) = await PcbDesigner.DesignAndExportFabAsync(p, outDir);

            // 1) The board actually reached a DRC-CLEAN verdict on the real toolchain (not a fake).
            Assert.True(design.Installed);
            Assert.True(design.Ok, $"design did not reach DRC-clean: {design.Summary} | {string.Join(" / ", design.Trace)}");
            Assert.NotNull(design.Report);
            Assert.True(design.Report!.Clean);
            Assert.Equal(0, design.Report.ErrorCount);
            Assert.Equal(0, design.Report.UnconnectedCount);

            // 2) A real, validated fab ZIP was produced — the orderable output.
            Assert.True(fab.Installed);
            Assert.True(fab.Ok, $"fab export failed: {fab.Summary} | {string.Join(" / ", fab.Notes)}");
            Assert.NotNull(fab.ZipPath);
            Assert.True(File.Exists(fab.ZipPath!), $"fab ZIP not on disk: {fab.ZipPath}");

            // 3) The ZIP physically contains the standard 2-layer orderable set: front + back copper, board
            //    outline, and an Excellon drill. Assert against the real archive, not just the result record.
            using var zip = ZipFile.OpenRead(fab.ZipPath!);
            var names = zip.Entries.Select(e => e.Name).ToList();
            Assert.Contains(names, n => n.EndsWith(".gtl", StringComparison.OrdinalIgnoreCase)); // front copper
            Assert.Contains(names, n => n.EndsWith(".gbl", StringComparison.OrdinalIgnoreCase)); // back copper
            Assert.Contains(names, n => n.EndsWith(".gm1", StringComparison.OrdinalIgnoreCase)); // edge cuts
            Assert.Contains(names, n => n.EndsWith(".drl", StringComparison.OrdinalIgnoreCase)); // drill
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }
}
