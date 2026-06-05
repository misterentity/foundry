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
    public async Task RealMcuBoard_WithUnmappedPin_IsStillRefusedNotMiswired()
    {
        if (!KiCadPresent) return;   // skip on bare boxes; pcb-live CI runs this for real

        // GPIO34/3V3/GND are in the ESP32 pin map (they resolve), but GPIO99 is NOT a real ESP32 pin — it has
        // no pad, so the honest gate must REFUSE rather than ordinal-guess it. Coverage is incremental + safe.
        var p = new Project
        {
            Title = "ESP32 with an unmapped pin",
            Components = new()
            {
                new ComponentSpec { Alias = "U1", Ref = "esp32", Name = "ESP32-WROOM-32" },
                new ComponentSpec { Alias = "C1", Ref = "cap", Name = "100nF capacitor" },
            },
            Connections = new()
            {
                new Connection { From = "U1.3V3", To = "C1.1", Net = "power" },
                new Connection { From = "U1.GPIO99", To = "C1.2", Net = "sig" },   // no such pad → unmapped
            },
        };
        var outDir = OutDir();
        try
        {
            var r = await PcbBuilder.BuildAsync(p, outDir);
            Assert.True(r.Installed);
            Assert.False(r.Ok);                 // refused — an unmapped pin means connectivity is unverified
            Assert.Null(r.KicadPcbPath);
            Assert.Contains(r.UnmappedPins, u => u.Contains("GPIO99"));
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    [Fact]
    public async Task Esp32Board_MappedGpioPins_BuildVerified_OnAuthoritativePads()
    {
        if (!KiCadPresent) return;

        // The moat working: an ESP32 wired by logical GPIO name now builds VERIFIED, with each net landing on
        // the datasheet-correct physical pad (GPIO34→pad 6, 3V3→pad 2, GND→pad 1) — read back from the board.
        var p = new Project
        {
            Title = "ESP32 mapped GPIO",
            Components = new()
            {
                new ComponentSpec { Alias = "U1", Ref = "esp32", Name = "ESP32-WROOM-32" },
                new ComponentSpec { Alias = "C1", Ref = "cap", Name = "100nF capacitor" },
                new ComponentSpec { Alias = "R1", Ref = "r1", Name = "10k resistor" },
            },
            Connections = new()
            {
                new Connection { From = "U1.3V3", To = "C1.1", Net = "power" },
                new Connection { From = "U1.GND", To = "C1.2", Net = "gnd" },
                new Connection { From = "U1.GPIO34", To = "R1.1", Net = "sig" },
                new Connection { From = "R1.2", To = "C1.2", Net = "gnd" },
            },
        };
        var outDir = OutDir();
        try
        {
            var r = await PcbBuilder.BuildAsync(p, outDir);
            Assert.True(r.Installed);
            Assert.True(r.Ok);                  // GPIO34/3V3/GND all resolved to real pads
            Assert.Empty(r.UnmappedPins);
            Assert.NotNull(r.KicadPcbPath);

            var padNets = ReadPadNets(await File.ReadAllTextAsync(r.KicadPcbPath!));
            string Net(string @ref, string pad) => padNets[@ref][pad];

            // each logical pin landed on its authoritative ESP32-WROOM-32 pad, sharing the right net
            Assert.Equal(Net("U1", "2"), Net("C1", "1"));   // 3V3 (pad 2) ↔ C1.1  (power)
            Assert.Equal(Net("U1", "1"), Net("C1", "2"));   // GND (pad 1) ↔ C1.2  (gnd)
            Assert.Equal(Net("U1", "6"), Net("R1", "1"));   // GPIO34 (pad 6) ↔ R1.1 (sig)
            Assert.Equal(3, new[] { Net("U1", "2"), Net("U1", "1"), Net("U1", "6") }.Distinct().Count());
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

    [Fact]
    public async Task PicoBoard_MappedGpioPins_BuildVerified_OnAuthoritativePads()
    {
        if (!KiCadPresent) return;

        // Raspberry Pi Pico wired by GPIO name → resolves to KiCad 10's Module:RaspberryPi_Pico_Common_SMD
        // footprint and the curated/symbol pin map (GPIO0→pad 1, 3V3→pad 36, GND→pad 3). Exercises both the
        // pin map AND the KiCad-10 footprint-id fix.
        var p = new Project
        {
            Title = "Pico mapped GPIO",
            Components = new()
            {
                new ComponentSpec { Alias = "U1", Ref = "pico", Name = "Raspberry Pi Pico" },
                new ComponentSpec { Alias = "C1", Ref = "cap", Name = "100nF capacitor" },
                new ComponentSpec { Alias = "R1", Ref = "r1", Name = "10k resistor" },
            },
            Connections = new()
            {
                new Connection { From = "U1.3V3", To = "C1.1", Net = "power" },
                new Connection { From = "U1.GND", To = "C1.2", Net = "gnd" },
                new Connection { From = "U1.GPIO0", To = "R1.1", Net = "sig" },
                new Connection { From = "R1.2", To = "C1.2", Net = "gnd" },
            },
        };
        var outDir = OutDir();
        try
        {
            var r = await PcbBuilder.BuildAsync(p, outDir);
            Assert.True(r.Installed);
            Assert.True(r.Ok);
            Assert.Empty(r.UnmappedPins);
            Assert.NotNull(r.KicadPcbPath);

            var padNets = ReadPadNets(await File.ReadAllTextAsync(r.KicadPcbPath!));
            string Net(string @ref, string pad) => padNets[@ref][pad];
            Assert.Equal(Net("U1", "36"), Net("C1", "1"));   // 3V3  → pad 36
            Assert.Equal(Net("U1", "3"), Net("C1", "2"));    // GND  → pad 3
            Assert.Equal(Net("U1", "1"), Net("R1", "1"));    // GPIO0 → pad 1
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

            // Split on each pad so a net is bound to ITS pad (footprints have many unconnected pads with no
            // net — a cross-pad regex would mis-attribute the next pad's net). KiCad 10 pad nets are
            // (net "name"); older KiCad is (net <code> "name") — accept both. First-seen wins (dup pad names).
            var padMap = new Dictionary<string, string>();
            var chunks = block.Split("(pad \"");
            for (int c = 1; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                int q = chunk.IndexOf('"');
                if (q < 0) continue;
                var padName = chunk[..q];
                var net = Regex.Match(chunk, "\\(net (?:\\d+\\s+)?\"([^\"]*)\"\\)");
                if (net.Success && !padMap.ContainsKey(padName)) padMap[padName] = net.Groups[1].Value;
            }
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
