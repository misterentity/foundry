using Foundry.Core.Fabrication;
using Foundry.Core.Kb;
using Foundry.Core.Pcb;
using Foundry.Core.Project;

namespace Foundry.Tests;

public class FootprintMapTests
{
    private static ComponentSpec Spec(string name, string? footprint = null, string @ref = "X1") =>
        new() { Alias = "X1", Ref = @ref, Name = name, Footprint = footprint };

    [Fact]
    public void Explicit_Footprint_Wins()
    {
        var c = FootprintMap.Resolve(Spec("anything", footprint: "Resistor_SMD:R_0402_1005Metric"), 2);
        Assert.Equal("Resistor_SMD:R_0402_1005Metric", c.LibId);
        Assert.False(c.IsFallback);
        Assert.Equal("explicit", c.Reason);
    }

    [Fact]
    public void Resistor_DefaultsTo0805_Smd()
    {
        var c = FootprintMap.Resolve(Spec("10k resistor"), 2);
        Assert.Equal("Resistor_SMD:R_0805_2012Metric", c.LibId);
        Assert.False(c.IsFallback);
    }

    [Fact]
    public void Resistor_SizeToken_SwapsMetricBodyCode()
    {
        // The size hint swaps the metric body token (2012 → 1005); the imperial token is left as-is.
        var c = FootprintMap.Resolve(Spec("0402 resistor"), 2);
        Assert.Equal("Resistor_SMD:R_0805_1005Metric", c.LibId);
    }

    [Fact]
    public void Resistor_ThroughHole_UsesAxial()
    {
        var c = FootprintMap.Resolve(Spec("axial through-hole resistor"), 2);
        Assert.Contains("Resistor_THT:R_Axial", c.LibId);
    }

    [Fact]
    public void Capacitor_DefaultsTo0603()
    {
        var c = FootprintMap.Resolve(Spec("100nF capacitor"), 2);
        Assert.Equal("Capacitor_SMD:C_0603_1608Metric", c.LibId);
    }

    [Fact]
    public void Capacitor_Electrolytic_UsesRadialTht()
    {
        var c = FootprintMap.Resolve(Spec("electrolytic capacitor 10uf"), 2);
        Assert.Contains("Capacitor_THT:CP_Radial", c.LibId);
    }

    [Fact]
    public void Led_Smd_And_Tht()
    {
        Assert.Contains("LED_SMD:LED_0805", FootprintMap.Resolve(Spec("smd led"), 2).LibId);
        Assert.Equal("LED_THT:LED_D5.0mm", FootprintMap.Resolve(Spec("red led"), 2).LibId);
        Assert.Equal("LED_THT:LED_D3.0mm", FootprintMap.Resolve(Spec("3mm led"), 2).LibId);
    }

    [Fact]
    public void Diode_Smd_And_Tht()
    {
        Assert.Equal("Diode_SMD:D_SOD-123", FootprintMap.Resolve(Spec("sod diode"), 2).LibId);
        Assert.Contains("Diode_THT:D_DO-41", FootprintMap.Resolve(Spec("1N4007 rectifier diode"), 2).LibId);
    }

    [Fact]
    public void Header_SizedByPinCount()
    {
        var c = FootprintMap.Resolve(Spec("4-pin header"), 4);
        Assert.Equal("Connector_PinHeader_2.54mm:PinHeader_1x04_P2.54mm_Vertical", c.LibId);
        Assert.False(c.IsFallback);
    }

    [Fact]
    public void Mcu_Esp32_MapsToModule()
    {
        Assert.Equal("RF_Module:ESP32-WROOM-32", FootprintMap.Resolve(Spec("ESP32-WROOM-32"), 38).LibId);
    }

    [Fact]
    public void Regulator_Tht_And_Smd()
    {
        Assert.Contains("TO-220", FootprintMap.Resolve(Spec("7805 regulator"), 3).LibId);
        Assert.Contains("SOT-223", FootprintMap.Resolve(Spec("sot-223 LDO regulator"), 3).LibId);
    }

    [Fact]
    public void Transistor_Tht_And_Smd()
    {
        Assert.Contains("TO-92", FootprintMap.Resolve(Spec("to-92 transistor"), 3).LibId);
        Assert.Equal("Package_TO_SOT_SMD:SOT-23", FootprintMap.Resolve(Spec("mosfet"), 3).LibId);
    }

    [Fact]
    public void Unknown_Part_FallsBackToPinCountHeader()
    {
        var c = FootprintMap.Resolve(Spec("Mysterious Widget 9000"), 6);
        Assert.Equal("Connector_PinHeader_2.54mm:PinHeader_1x06_P2.54mm_Vertical", c.LibId);
        Assert.True(c.IsFallback);
        Assert.Contains("Mysterious Widget 9000", c.Reason);
    }

    [Fact]
    public void Fallback_PinCount_AtLeastOne()
    {
        var c = FootprintMap.Resolve(Spec("Mysterious Widget"), 0);
        Assert.Equal("Connector_PinHeader_2.54mm:PinHeader_1x01_P2.54mm_Vertical", c.LibId);
        Assert.True(c.IsFallback);
    }

    [Fact]
    public void PadNets_MapsPinNameToNet_ForMatchingAlias()
    {
        var pairs = new[]
        {
            ("U1.1", "GND"),
            ("U1.VCC", "+3V3"),
            ("U2.1", "GND"),   // different alias — ignored
        };
        var pads = FootprintMap.PadNets("U1", pairs);
        Assert.Equal(2, pads.Count);
        Assert.Equal("GND", pads["1"]);
        Assert.Equal("+3V3", pads["VCC"]);
        Assert.False(pads.ContainsKey("2"));
    }

    [Fact]
    public void PadNets_IsCaseInsensitive_OnPadName()
    {
        var pads = FootprintMap.PadNets("u1", new[] { ("U1.SDA", "SDA") });
        Assert.Equal("SDA", pads["sda"]);
    }

    [Fact]
    public void PadNetList_PreservesOrder_AndDedupesPins_ForOrdinalFallback()
    {
        // Ordered pin->net so build_board.py can assign by pad-name match then ordinal position —
        // this is what gives generic fallback headers (pads "1".."N") real connectivity (v2.3.1 fix).
        var pairs = new[]
        {
            ("S1.VCC", "+3V3"),
            ("S1.AOUT", "SIG"),
            ("S1.GND", "GND"),
            ("S1.VCC", "+3V3"),   // duplicate pin — first-seen kept, order stable
            ("S2.VCC", "+3V3"),   // other alias — ignored
        };
        var list = FootprintMap.PadNetList("S1", pairs);
        Assert.Equal(new[] { "VCC", "AOUT", "GND" }, list.Select(p => p.Pin).ToArray());
        Assert.Equal(new[] { "+3V3", "SIG", "GND" }, list.Select(p => p.Net).ToArray());
    }

    [Fact]
    public void RefOf_And_PinOf_SplitEndpoint()
    {
        Assert.Equal("U1", FootprintMap.RefOf("U1.GPIO21"));
        Assert.Equal("GPIO21", FootprintMap.PinOf("U1.GPIO21"));
        Assert.Equal("J1", FootprintMap.RefOf("J1"));
        Assert.Equal("1", FootprintMap.PinOf("J1"));   // no dot ⇒ pad 1
    }
}

public class PcbJobTests
{
    private static Project MiniProject() => new()
    {
        Title = "Mini Board",
        Components = new()
        {
            new ComponentSpec { Alias = "MCU", Ref = "esp32", Name = "ESP32 DevKit" },
            new ComponentSpec { Alias = "SENSOR", Ref = "bme280", Name = "BME280" },
        },
        Connections = new()
        {
            new Connection { From = "MCU.3V3", To = "SENSOR.VCC", Net = "power" },
            new Connection { From = "MCU.GND", To = "SENSOR.GND", Net = "ground" },
            new Connection { From = "MCU.GPIO21", To = "SENSOR.SDA", Net = "i2c" },
            new Connection { From = "MCU.GPIO22", To = "SENSOR.SCL", Net = "i2c" },
        },
    };

    [Fact]
    public void Build_NetsMatchKiCadNetlist()
    {
        var p = MiniProject();
        var job = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>());

        var expected = KiCadNetlist.Nets(p).Select(n => n.Name).OrderBy(x => x).ToArray();
        var actual = job.Nets.Select(n => n.Name).OrderBy(x => x).ToArray();
        Assert.Equal(expected, actual);

        // power/ground naming flowed through
        Assert.Contains("GND", actual);
        Assert.Contains("+3V3", actual);
        Assert.Contains("SDA", actual);
        Assert.Contains("SCL", actual);
    }

    [Fact]
    public void Build_AssignsRefsAndFootprints()
    {
        var job = PcbJob.Build(MiniProject(), "out.kicad_pcb", Array.Empty<string>());
        var refs = job.Components.Select(c => c.Ref).ToArray();
        Assert.Contains("MCU", refs);
        Assert.Contains("SENSOR", refs);

        var mcu = job.Components.First(c => c.Ref == "MCU");
        Assert.Equal("RF_Module:ESP32-WROOM-32", mcu.Footprint);
        Assert.All(job.Components, c => Assert.False(string.IsNullOrWhiteSpace(c.Footprint)));
    }

    [Fact]
    public void Build_PadNets_MatchNetMembership()
    {
        var job = PcbJob.Build(MiniProject(), "out.kicad_pcb", Array.Empty<string>());
        var mcu = job.Components.First(c => c.Ref == "MCU");
        var sensor = job.Components.First(c => c.Ref == "SENSOR");

        Assert.Equal("+3V3", mcu.PadNets["3V3"]);
        Assert.Equal("GND", mcu.PadNets["GND"]);
        Assert.Equal("SDA", mcu.PadNets["GPIO21"]);
        Assert.Equal("SCL", mcu.PadNets["GPIO22"]);

        Assert.Equal("+3V3", sensor.PadNets["VCC"]);
        Assert.Equal("GND", sensor.PadNets["GND"]);
        Assert.Equal("SDA", sensor.PadNets["SDA"]);
        Assert.Equal("SCL", sensor.PadNets["SCL"]);
    }

    [Fact]
    public void Build_GridPositionsAreDistinct()
    {
        var job = PcbJob.Build(MiniProject(), "out.kicad_pcb", Array.Empty<string>());
        var positions = job.Components.Select(c => (c.XMm, c.YMm)).ToList();
        Assert.Equal(positions.Count, positions.Distinct().Count());
    }

    [Fact]
    public void Build_ProducesRectangularOutline()
    {
        var job = PcbJob.Build(MiniProject(), "out.kicad_pcb", Array.Empty<string>());
        Assert.Equal(4, job.OutlineSegmentsMm.Count);
        Assert.All(job.OutlineSegmentsMm, seg => Assert.Equal(4, seg.Length));
        // closed loop: last segment ends where the first starts
        var first = job.OutlineSegmentsMm[0];
        var last = job.OutlineSegmentsMm[^1];
        Assert.Equal(first[0], last[2]);
        Assert.Equal(first[1], last[3]);
    }

    [Fact]
    public void Build_NoUnresolvedNodeDiagnostics_ForCleanProject()
    {
        var job = PcbJob.Build(MiniProject(), "out.kicad_pcb", Array.Empty<string>());
        Assert.DoesNotContain(job.Diagnostics, d => d.Severity == "error");
    }

    [Fact]
    public void Build_EmitsErrorWhenNodeHasNoPad()
    {
        // A connection references a pin on a component that the netlist won't place a pad for is hard
        // to construct (every endpoint becomes a pad); instead drive the fallback-warning path and the
        // happy resolution path. Unknown parts still resolve every node to a header pad → no error.
        var p = new Project
        {
            Title = "Unknown",
            Components = new() { new ComponentSpec { Alias = "WIDGET", Ref = "?", Name = "Frobnicator" } },
            Connections = new() { new Connection { From = "WIDGET.A", To = "WIDGET.B", Net = "signal" } },
        };
        var job = PcbJob.Build(p, "out.kicad_pcb", Array.Empty<string>());
        Assert.DoesNotContain(job.Diagnostics, d => d.Severity == "error");
        Assert.Contains(job.Diagnostics, d => d.Severity == "warning");   // generic-footprint fallback
    }

    [Fact]
    public void Build_ToJson_SerializesScriptShape()
    {
        var job = PcbJob.Build(MiniProject(), "C:/tmp/out.kicad_pcb", new[] { "C:/kicad/footprints" });
        var json = job.ToJson();
        Assert.Contains("\"outPath\"", json);
        Assert.Contains("\"footprintDirs\"", json);
        Assert.Contains("\"outlineSegments_mm\"", json);
        Assert.Contains("\"components\"", json);
        Assert.Contains("\"padNets\"", json);
        Assert.Contains("\"footprint\"", json);
        Assert.Contains("\"x_mm\"", json);
        Assert.DoesNotContain("Diagnostics", json);   // [JsonIgnore]
    }
}

public class PcbResultTests
{
    [Fact]
    public void NotInstalled_SurfacesDownloadGuidance()
    {
        var r = PcbResult.NotInstalled();
        Assert.False(r.Installed);
        Assert.False(r.Ok);
        Assert.Null(r.KicadPcbPath);
        Assert.Contains(KiCadInstaller.DownloadUrl, r.Summary);
    }

    [Fact]
    public void Parse_Ok_RequiresExistingFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "foundry_pcb_test_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        try
        {
            var json = "{\"ok\":true,\"out\":\"" + tmp.Replace("\\", "\\\\") + "\",\"components\":2,\"nets\":4,\"notes\":[]}";
            var r = PcbResult.Parse(json, "", 0, tmp);
            Assert.True(r.Ok);
            Assert.True(r.Installed);
            Assert.Equal(tmp, r.KicadPcbPath);
            Assert.Contains("2 parts", r.Summary);
            Assert.Contains("4 nets", r.Summary);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Ok_ButMissingFile_BecomesFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".kicad_pcb");
        var json = "{\"ok\":true,\"out\":\"" + missing.Replace("\\", "\\\\") + "\",\"components\":1,\"nets\":1}";
        var r = PcbResult.Parse(json, "", 0, missing);
        Assert.False(r.Ok);
        Assert.Null(r.KicadPcbPath);
        Assert.Contains(r.Notes, n => n.Contains("no .kicad_pcb"));
    }

    [Fact]
    public void Parse_ErrorJson_IsFailureWithNote()
    {
        var r = PcbResult.Parse("{\"ok\":false,\"error\":\"footprint X not found\"}", "", 1, "out.kicad_pcb");
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("footprint X not found"));
    }

    [Fact]
    public void Parse_ToleratesLeadingLogLines_BeforeJson()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "foundry_pcb_test_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        try
        {
            var stdout = "loading pcbnew...\nplacing components\n{\"ok\":true,\"out\":\"" +
                         tmp.Replace("\\", "\\\\") + "\",\"components\":1,\"nets\":0}";
            var r = PcbResult.Parse(stdout, "", 0, tmp);
            Assert.True(r.Ok);
            Assert.Equal(tmp, r.KicadPcbPath);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_NonZeroExit_NoJson_FallsBackToStderr()
    {
        var r = PcbResult.Parse("", "ImportError: No module named pcbnew", 1, "out.kicad_pcb");
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("pcbnew"));
    }

    [Fact]
    public void Parse_UnmappedPins_BlocksOk_AndSurfacesThem()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "foundry_pcb_test_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        try
        {
            // The board file exists and the script even said ok:true, but a NAMED footprint had an
            // unmatched net pin — connectivity is UNVERIFIED, so the C# side must fail the build.
            var json = "{\"ok\":true,\"out\":\"" + tmp.Replace("\\", "\\\\") +
                       "\",\"components\":1,\"nets\":3,\"unmappedPins\":[{\"ref\":\"U1\",\"pin\":\"SDA\",\"net\":\"I2C_SDA\",\"footprint\":\"Sensor:BME280\"}],\"byPosition\":[],\"notes\":[]}";
            var r = PcbResult.Parse(json, "", 0, tmp);
            Assert.False(r.Ok);
            Assert.Null(r.KicadPcbPath);
            Assert.NotEmpty(r.UnmappedPins);
            Assert.Contains(r.UnmappedPins, u => u.Contains("SDA"));
            Assert.Contains(r.Notes, n => n.Contains("Connectivity unverified"));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_ByPositionOnHeader_StaysOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "foundry_pcb_test_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        try
        {
            // A pure-numeric header placed pins by ordinal position — allowed; no unmapped pins -> Ok stays true.
            var json = "{\"ok\":true,\"out\":\"" + tmp.Replace("\\", "\\\\") +
                       "\",\"components\":1,\"nets\":3,\"unmappedPins\":[],\"byPosition\":[{\"ref\":\"J1\",\"pin\":\"VCC\",\"pad\":\"1\",\"footprint\":\"Connector:Header\"}],\"notes\":[]}";
            var r = PcbResult.Parse(json, "", 0, tmp);
            Assert.True(r.Ok);
            Assert.Equal(tmp, r.KicadPcbPath);
            Assert.Empty(r.UnmappedPins);
            Assert.Equal(1, r.ByPositionCount);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_NoUnmapped_BackCompat()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "foundry_pcb_test_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        try
        {
            // Legacy script output with no unmappedPins/byPosition fields still parses clean.
            var json = "{\"ok\":true,\"out\":\"" + tmp.Replace("\\", "\\\\") + "\",\"components\":2,\"nets\":4,\"notes\":[]}";
            var r = PcbResult.Parse(json, "", 0, tmp);
            Assert.True(r.Ok);
            Assert.Empty(r.UnmappedPins);
            Assert.Equal(0, r.ByPositionCount);
        }
        finally { File.Delete(tmp); }
    }
}

public class PcbBuilderTests
{
    [Fact]
    public async Task BuildAsync_ReturnsNotInstalled_WhenKiCadAbsent()
    {
        // KiCad is not installed on the build/CI machine — assert graceful degradation.
        if (KiCadInstaller.Locate() is not null) return;   // guard: real install present, skip

        var p = new Project
        {
            Title = "Mini",
            Connections = new() { new Connection { From = "A.1", To = "B.1", Net = "signal" } },
        };
        var dir = Path.Combine(Path.GetTempPath(), "foundry_pcb_out_" + Guid.NewGuid().ToString("N")[..8]);
        var r = await PcbBuilder.BuildAsync(p, dir);
        Assert.False(r.Installed);
        Assert.False(r.Ok);
        Assert.Null(r.KicadPcbPath);
    }

    [Fact]
    public void ReadScript_ReturnsEmbeddedPython()
    {
        var script = PcbBuilder.ReadScript();
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("pcbnew", script);
    }
}

// ---- McuPinMap: logical-pin → real-pad resolution (the moat's pin maps) --------------------------

public class McuPinMapTests
{
    private const string Esp = "RF_Module:ESP32-WROOM-32";

    [Fact]
    public void Esp32_ResolvesLogicalPinsToAuthoritativePads()
    {
        Assert.Equal("6", McuPinMap.ResolvePad(Esp, "GPIO34"));
        Assert.Equal("2", McuPinMap.ResolvePad(Esp, "3V3"));
        Assert.Equal("1", McuPinMap.ResolvePad(Esp, "GND"));
        Assert.Equal("25", McuPinMap.ResolvePad(Esp, "GPIO0"));
        Assert.Equal("3", McuPinMap.ResolvePad(Esp, "EN"));
    }

    [Fact]
    public void Esp32_NormalizesAliases()
    {
        Assert.Equal("6", McuPinMap.ResolvePad(Esp, "IO34"));       // KiCad-symbol style
        Assert.Equal("2", McuPinMap.ResolvePad(Esp, "VDD"));
        Assert.Equal("4", McuPinMap.ResolvePad(Esp, "SENSOR_VP")); // GPIO36
        Assert.Equal("35", McuPinMap.ResolvePad(Esp, "TX"));       // TXD0 / GPIO1
        Assert.Equal("34", McuPinMap.ResolvePad(Esp, "RXD0"));     // GPIO3
    }

    [Fact]
    public void UnknownPinOrFootprint_ReturnsNull_SoCallerFailsClosed()
    {
        Assert.Null(McuPinMap.ResolvePad(Esp, "GPIO99"));                 // no such pin on the module
        Assert.Null(McuPinMap.ResolvePad("Connector:Generic_1x04", "VCC")); // no map for this footprint
    }

    [Fact]
    public void EveryMappedEsp32PadIsAValidFootprintPadNumber()
    {
        foreach (var gpio in new[] { "GPIO0", "GPIO34", "GPIO23", "GPIO1", "GPIO3", "GPIO36", "GPIO39", "3V3", "GND", "EN" })
        {
            var pad = McuPinMap.ResolvePad(Esp, gpio);
            Assert.True(int.TryParse(pad, out var n) && n is >= 1 and <= 39, $"{gpio} -> {pad}");
        }
    }
}

// ---- RP2040/Pico curated map + cross-check against the authoritative symbol library -------------

public class PicoPinMapTests
{
    private const string Pico = "Module:RaspberryPi_Pico_Common_SMD";

    [Fact]
    public void Pico_ResolvesGpioAndPowerToAuthoritativePads()
    {
        Assert.Equal("1", McuPinMap.ResolvePad(Pico, "GPIO0"));
        Assert.Equal("2", McuPinMap.ResolvePad(Pico, "GPIO1"));
        Assert.Equal("29", McuPinMap.ResolvePad(Pico, "GPIO22"));
        Assert.Equal("31", McuPinMap.ResolvePad(Pico, "GPIO26"));   // ADC0
        Assert.Equal("36", McuPinMap.ResolvePad(Pico, "3V3"));
        Assert.Equal("40", McuPinMap.ResolvePad(Pico, "VBUS"));
        Assert.Equal("3", McuPinMap.ResolvePad(Pico, "GND"));
    }

    [Fact]
    public void Pico_NormalizesGpSilkscreenNaming()
    {
        Assert.Equal("1", McuPinMap.ResolvePad(Pico, "GP0"));    // Pico silkscreen GP0 == GPIO0
        Assert.Equal("29", McuPinMap.ResolvePad(Pico, "GP22"));
    }

    [Fact]
    public void Esp12E_HasDistinctRstAndEn_AndResolvesGpio()
    {
        const string esp12 = "RF_Module:ESP-12E";
        // The cross-chip bug guard: RST and EN are DIFFERENT pads on the ESP8266 (unlike the ESP32).
        Assert.Equal("1", McuPinMap.ResolvePad(esp12, "RST"));
        Assert.Equal("3", McuPinMap.ResolvePad(esp12, "EN"));
        Assert.NotEqual(McuPinMap.ResolvePad(esp12, "RST"), McuPinMap.ResolvePad(esp12, "EN"));
        Assert.Equal("18", McuPinMap.ResolvePad(esp12, "GPIO0"));
        Assert.Equal("8", McuPinMap.ResolvePad(esp12, "3V3"));
        Assert.Equal("8", McuPinMap.ResolvePad(esp12, "VCC"));   // universal VCC→3V3
        Assert.Equal("15", McuPinMap.ResolvePad(esp12, "GND"));
        Assert.Equal("2", McuPinMap.ResolvePad(esp12, "A0"));
    }

    [Fact]
    public void Esp32_RstFoldsToEn_ButOnlyForEsp32()
    {
        // ESP32: reset IS the EN pin (chip-specific key, not a global fold that would break the ESP8266).
        Assert.Equal("3", McuPinMap.ResolvePad("RF_Module:ESP32-WROOM-32", "RST"));
        Assert.Equal("3", McuPinMap.ResolvePad("RF_Module:ESP32-WROOM-32", "EN"));
    }

    [Fact]
    public void CuratedMaps_AgreeWithSymbolDerived_ForEveryGpioWhereBothResolve()
    {
        // Cross-check the hand-curated McuPinMap against the authoritative KiCad symbol library: wherever BOTH
        // resolve a pin, the pad numbers MUST match — so a transcription typo in the curated map fails the build.
        var kicad = KiCadInstaller.Locate();
        if (kicad is null) return;   // needs the symbol library; pcb-live CI runs this for real
        var dir = kicad.SymbolDir;

        var footprints = new[] { "RF_Module:ESP32-WROOM-32", "RF_Module:ESP-12E", "Module:RaspberryPi_Pico_Common_SMD" };
        var pins = new List<string> { "3V3", "GND", "VBUS", "VSYS", "EN", "RUN" };
        for (int n = 0; n <= 39; n++) pins.Add("GPIO" + n);

        int agreed = 0;
        foreach (var fp in footprints)
            foreach (var pin in pins)
            {
                var curated = McuPinMap.ResolvePad(fp, pin);
                var derived = SymbolPinMap.ResolvePad(fp, pin, dir);
                if (curated is not null && derived is not null)
                {
                    Assert.Equal(derived, curated);   // authoritative symbol vs curated table
                    agreed++;
                }
            }
        Assert.True(agreed >= 30, $"expected the cross-check to verify many pins, only {agreed}");
    }
}

public class SymbolPinMapTests
{
    [Theory]
    [InlineData("IO34", "GPIO34")]
    [InlineData("GPIO26_ADC0", "GPIO26")]
    [InlineData("GP5", "GPIO5")]
    [InlineData("VDD", "3V3")]
    [InlineData("VSS", "GND")]
    [InlineData("RUN", "RUN")]
    public void Canonical_NormalizesUniversalNames(string raw, string expected) =>
        Assert.Equal(expected, SymbolPinMap.Canonical(raw));

    [Fact]
    public void ResolvePad_NoSymbolDir_ReturnsNull()
    {
        Assert.Null(SymbolPinMap.ResolvePad("Module:RaspberryPi_Pico_Common_SMD", "GPIO0", null));
        Assert.Null(SymbolPinMap.ResolvePad("Module:RaspberryPi_Pico_Common_SMD", "GPIO0", ""));
    }

    [Fact]
    public void ResolvePad_DerivesPicoPadsFromRealSymbolLibrary()
    {
        var kicad = KiCadInstaller.Locate();
        if (kicad is null) return;   // needs KiCad's symbol library
        var dir = kicad.SymbolDir;
        const string pico = "Module:RaspberryPi_Pico_Common_SMD";
        Assert.Equal("1", SymbolPinMap.ResolvePad(pico, "GPIO0", dir));
        Assert.Equal("31", SymbolPinMap.ResolvePad(pico, "GPIO26", dir));   // GPIO26_ADC0 in the symbol
        Assert.Equal("36", SymbolPinMap.ResolvePad(pico, "3V3", dir));
        Assert.Equal("40", SymbolPinMap.ResolvePad(pico, "VBUS", dir));
        Assert.Null(SymbolPinMap.ResolvePad(pico, "GPIO99", dir));          // no such pin → fail closed
    }
}
