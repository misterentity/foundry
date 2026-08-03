using Foundry.Core.Ai;
using Foundry.Core.Generation;
using Foundry.Core.Kb;

namespace Foundry.Tests;

public class GenerationTests
{
    private sealed class FakeAi : IAnthropicClient
    {
        private readonly string _json;
        public FakeAi(string json) => _json = json;
        public bool HasKey => true;
        public Task<ModelListResult> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModelListResult(true, ModelCatalog.Fallback, null));
        public Task<string> CompleteAsync(string system, string user, string model, CancellationToken ct = default) =>
            Task.FromResult(_json);
    }

    private const string Fixture = """
    Here is your design:
    {
      "title": "Plant Pal",
      "summary": "A battery soil sensor.",
      "subsystems": [{"role":"Controller","name":"ESP32","mpn":"ESP32-DEVKITC-32E","specs":[["Logic","3.3 V"]]}],
      "components": [
        {"alias":"MCU","ref":"esp32","name":"ESP32","logicV":3.3,"inputV":[3.0,5.5],"currentMa":80,
         "pins":[{"name":"3V3","kind":"power"},{"name":"GND","kind":"ground"},
                 {"name":"GPIO34","kind":"analog","inputOnly":true},{"name":"GPIO0","kind":"bidir","strapping":true}]},
        {"alias":"SENSOR","ref":"cap","name":"Soil sensor","logicV":3.3,"currentMa":5,
         "pins":[{"name":"VCC","kind":"power"},{"name":"GND","kind":"ground"},{"name":"AOUT","kind":"output"}]}
      ],
      "bom": [{"qty":1,"name":"ESP32","mpn":"ESP32-DEVKITC-32E","price":8.5,"stock":1442,"lead":"Stock","dist":"DigiKey","note":"MCU"},
              {"qty":1,"name":"Soil sensor","mpn":"SEN-CAP-01","price":4.2,"stock":300,"lead":"Stock","dist":"Amazon","note":""}],
      "connections": [{"from":"MCU.3V3","to":"SENSOR.VCC","net":"power"},
                      {"from":"MCU.GND","to":"SENSOR.GND","net":"ground"},
                      {"from":"MCU.GPIO34","to":"SENSOR.AOUT","net":"signal"},
                      {"from":"MCU.GPIO0","to":"SENSOR.AOUT","net":"signal"}],
      "enclosure": {"inner":[60,40,25],"wall":2.0,"lid":"snap","standoffs":4,
                    "cutouts":[{"face":"side","shape":"rect","size":[9.5,6.5],"pos":[12,18],"label":"USB-C"}]},
      "firmwarePlatform": "Arduino C++",
      "assembly": [{"title":"Wire it","body":"Connect the sensor.","chips":["GPIO34"]}]
    }
    """;

    [Fact]
    public async Task Generate_ParsesProject_AndRunsEngines()
    {
        var gen = new ProjectGenerator(new FakeAi(Fixture), "claude-sonnet-4-6");
        var result = await gen.GenerateAsync("a soil sensor");

        Assert.True(result.Ok, result.Message);
        var p = result.Project!;
        Assert.Equal("Plant Pal", p.Title);
        Assert.Equal(2, p.Bom.Count);
        Assert.Equal(2, p.Kpis.Parts);
        Assert.Equal(12.70, p.Kpis.Cost, 2);          // 8.5 + 4.2
        Assert.Equal(85, p.Kpis.CurrentMa);            // 80 + 5 from component specs

        // firmware pin map is generated from the netlist
        Assert.Contains(p.Firmware.Files, f => f.Name is "pinmap.h" && f.Content.Contains("PIN_SENSOR_AOUT"));

        // engine ran: GPIO34 input-only is driven by SENSOR.AOUT (output) on GPIO0 conflict etc.
        Assert.NotEmpty(p.Findings);
        Assert.Contains(p.Findings, f => f.Code == "PIN-04");   // GPIO0 strapping
        Assert.Contains(p.Findings, f => f.Code == "PIN-CONF"); // GPIO34 & GPIO0 both wired to SENSOR.AOUT? no — AOUT appears twice
    }

    [Fact]
    public async Task Generate_NoKey_ReturnsGate()
    {
        var stub = new StubAnthropicClient();
        var gen = new ProjectGenerator(stub);
        var result = await gen.GenerateAsync("anything");
        Assert.False(result.Ok);
        Assert.Contains("key", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generate_Garbage_FailsGracefully()
    {
        var gen = new ProjectGenerator(new FakeAi("not json at all"), "claude-sonnet-4-6");
        var result = await gen.GenerateAsync("x");
        Assert.False(result.Ok);
        Assert.NotNull(result.Message);
    }
}

// ---- AI response truncation detection (max_tokens) ----------------------------------------------

public class AnthropicTruncationTests
{
    [Theory]
    [InlineData("max_tokens", true)]
    [InlineData("MAX_TOKENS", true)]
    [InlineData("end_turn", false)]
    [InlineData("stop_sequence", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsTruncated_DetectsMaxTokensStopReason(string? stopReason, bool expected) =>
        Assert.Equal(expected, Foundry.Core.Ai.AnthropicClient.IsTruncated(stopReason));
}

// The I²C address is the field the collision rule reads. It had no producer at all -- absent from the
// system-prompt schema and unparsed by MapComponents -- so the rule was dead code that reported PASS.
// These pin the parse, including the forms models actually emit instead of a clean JSON number.
public class I2cAddressParsingTests
{
    private static ComponentSpec One(string addrJson)
    {
        var json = $$"""
        {"components":[{"alias":"D1","ref":"d1","name":"BME280","logicV":3.3,{{addrJson}}
                        "pins":[{"name":"SDA","kind":"bidir"}]}]}
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return Assert.Single(ProjectGenerator.MapComponents(doc.RootElement));
    }

    [Theory]
    [InlineData("\"i2cAddr\":\"0x76\",", 0x76)]
    [InlineData("\"i2cAddr\":\"0X3C\",", 0x3C)]
    [InlineData("\"i2cAddr\":118,", 0x76)]      // plain decimal
    [InlineData("\"i2cAddr\":\"118\",", 0x76)]  // decimal as a string
    [InlineData("\"i2cAddr\":\"76h\",", 0x76)]  // assembler style
    [InlineData("\"i2cAddr\":\" 0x48 \",", 0x48)]
    public void TheAddressFormsModelsEmit_AllParse(string addrJson, int expected) =>
        Assert.Equal(expected, One(addrJson).I2cAddress);

    // Out-of-range values are dropped, not trusted. 0x00-0x07 and 0x78-0x7F are reserved, and an 8-bit
    // address (0xEC = 0x76 << 1) is a different number entirely -- checking against it would be worse
    // than not checking, because it reports a verdict. Dropping leaves the bus honestly unproven.
    [Theory]
    [InlineData("\"i2cAddr\":\"0xEC\",")]       // 8-bit form of 0x76
    [InlineData("\"i2cAddr\":0,")]
    [InlineData("\"i2cAddr\":\"0x03\",")]       // reserved
    [InlineData("\"i2cAddr\":\"0x7F\",")]       // reserved
    [InlineData("\"i2cAddr\":300,")]
    [InlineData("\"i2cAddr\":-1,")]
    [InlineData("\"i2cAddr\":\"unknown\",")]
    [InlineData("\"i2cAddr\":null,")]
    [InlineData("")]                             // field absent entirely
    public void AnAddressOutsideTheAddressableRange_IsDroppedNotTrusted(string addrJson) =>
        Assert.Null(One(addrJson).I2cAddress);

    [Fact]
    public void TheBoundariesOfTheAddressableRange_AreKept()
    {
        Assert.Equal(0x08, One("\"i2cAddr\":\"0x08\",").I2cAddress);
        Assert.Equal(0x77, One("\"i2cAddr\":\"0x77\",").I2cAddress);
    }
}
