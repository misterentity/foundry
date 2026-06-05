using System.Text.RegularExpressions;
using Foundry.Core.Kb;
using Foundry.Core.Pcb;
using Foundry.Core.Project;

namespace Foundry.Tests;

// Live end-to-end PCB tests that RUN the real KiCad toolchain when it is present (and skip cleanly when
// not). Unlike the rest of the PCB suite, these do NOT short-circuit when KiCad is installed — they are
// the positive path the auto-PCB moat depends on, so a regression in net→pad assignment cannot ship green.
// Headline assertion: a real MCU board addressed by LOGICAL pin names is REFUSED (connectivity unverified),
// never silently mis-wired by ordinal pad position (the bug P0-1 fixes). The pcb-live CI lane runs these
// against a choco-installed KiCad; on a bare dev box they skip.
public class PcbLiveToolchainTests
{
    private static bool KiCadPresent => KiCadInstaller.Locate() is not null;

    private static string OutDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "foundry_live_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task RealMcuBoard_AddressedByLogicalPins_IsRefusedNotMiswired()
    {
        if (!KiCadPresent) return;   // skip on bare boxes; pcb-live CI runs this for real

        // ESP32 resolves to a real footprint whose pads are all numeric ("1".."39"), wired here by GPIO name.
        // Foundry has no GPIO-name→pad map, so the honest gate must REFUSE rather than ordinal-map GPIO34→pad 1.
        var p = new Project
        {
            Title = "ESP32 logical pins",
            Components = new()
            {
                new ComponentSpec { Alias = "U1", Ref = "esp32", Name = "ESP32-WROOM-32" },
                new ComponentSpec { Alias = "C1", Ref = "cap", Name = "100nF capacitor" },
            },
            Connections = new()
            {
                new Connection { From = "U1.3V3", To = "C1.1", Net = "power" },
                new Connection { From = "U1.GND", To = "C1.2", Net = "gnd" },
                new Connection { From = "U1.GPIO34", To = "C1.1", Net = "power" },
            },
        };
        var outDir = OutDir();
        try
        {
            var r = await PcbBuilder.BuildAsync(p, outDir);
            Assert.True(r.Installed);
            Assert.False(r.Ok);                 // refused — connectivity could not be verified
            Assert.Null(r.KicadPcbPath);
            Assert.NotEmpty(r.UnmappedPins);
            Assert.Contains(r.UnmappedPins, u => u.Contains("GPIO34") || u.Contains("3V3"));
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    [Fact]
    public async Task PassivesBoard_AddressedByNumericPins_BuildsVerified_AndWiresNets()
    {
        if (!KiCadPresent) return;

        // Passives addressed by numeric pin (R1.1/R1.2/…) match the footprint's numeric pads by NAME — a
        // verifiable board. result.Ok ⇒ every net pin landed on a real pad; assert the nets exist on the board.
        var p = new Project
        {
            Title = "RC divider",
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
            var r = await PcbBuilder.BuildAsync(p, outDir);
            Assert.True(r.Installed);
            Assert.True(r.Ok);                  // every numeric pin matched a pad by name
            Assert.Empty(r.UnmappedPins);
            Assert.NotNull(r.KicadPcbPath);

            // Read the board back and assert pad→net == INTENT (naming-independent): the pads we connected
            // must end up on the SAME physical net. This is the assertion that would have caught the
            // ordinal-mismap bug end-to-end.
            var padNets = ReadPadNets(await File.ReadAllTextAsync(r.KicadPcbPath!));
            string Net(string @ref, string pad) => padNets[@ref][pad];

            Assert.Equal(Net("R1", "1"), Net("R2", "1"));   // vin
            Assert.Equal(Net("R1", "2"), Net("C1", "1"));   // vout
            Assert.Equal(Net("R2", "2"), Net("C1", "2"));   // gnd
            // and the three nets are genuinely distinct (not all collapsed onto one)
            Assert.Equal(3, new[] { Net("R1", "1"), Net("R1", "2"), Net("R2", "2") }.Distinct().Count());
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    // ---- minimal .kicad_pcb readback: ref -> (pad name -> net name) ---------------------------------

    /// <summary>Parse footprint blocks into ref → (pad → net name). Paren-matching skips quoted strings so
    /// net names that contain parens (e.g. "Net-(003)") don't throw off the depth count.</summary>
    private static Dictionary<string, Dictionary<string, string>> ReadPadNets(string board)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while ((i = board.IndexOf("(footprint", i, StringComparison.Ordinal)) >= 0)
        {
            int end = MatchParen(board, i);
            var block = board[i..end];
            i = end;

            var refM = Regex.Match(block, "\\(property \"Reference\" \"([^\"]+)\"");
            if (!refM.Success) continue;

            var padMap = new Dictionary<string, string>();
            // KiCad 10 pad nets are (net "name"); older KiCad is (net <code> "name") — accept both.
            foreach (Match pm in Regex.Matches(block, "\\(pad \"([^\"]+)\"[\\s\\S]*?\\(net (?:\\d+\\s+)?\"([^\"]*)\"\\)"))
                padMap[pm.Groups[1].Value] = pm.Groups[2].Value;
            result[refM.Groups[1].Value] = padMap;
        }
        return result;
    }

    private static int MatchParen(string s, int open)
    {
        int depth = 0; bool inStr = false;
        for (int j = open; j < s.Length; j++)
        {
            char c = s[j];
            if (inStr) { if (c == '\\') j++; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return j + 1; }
        }
        return s.Length;
    }
}
