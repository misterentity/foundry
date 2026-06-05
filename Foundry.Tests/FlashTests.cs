using Foundry.Core.Firmware;
using Foundry.Core.Kb;

namespace Foundry.Tests;

public class FlashTests
{
    // A realistic `arduino-cli board list --format json` payload (newer detected_ports shape):
    // one identified Uno on COM3, one bare/unidentified port on COM5.
    private const string DetectedPortsJson = """
    {
      "detected_ports": [
        {
          "port": { "address": "COM3", "label": "COM3", "protocol": "serial", "protocol_label": "Serial Port (USB)" },
          "matching_boards": [ { "name": "Arduino Uno", "fqbn": "arduino:avr:uno" } ]
        },
        {
          "port": { "address": "COM5", "label": "COM5", "protocol": "serial", "protocol_label": "Serial Port" }
        }
      ]
    }
    """;

    // Older arduino-cli emitted a bare array of port entries.
    private const string BareArrayJson = """
    [
      {
        "port": { "address": "/dev/ttyACM0", "label": "/dev/ttyACM0", "protocol": "serial" },
        "matching_boards": [ { "name": "Raspberry Pi Pico", "fqbn": "rp2040:rp2040:rpipico" } ]
      }
    ]
    """;

    [Fact]
    public void ParseBoardList_DetectedPortsShape_ReadsAddressAndFqbn()
    {
        var boards = FirmwareBuilder.ParseBoardList(DetectedPortsJson);

        Assert.Equal(2, boards.Count);
        var uno = boards.Single(b => b.Port == "COM3");
        Assert.Equal("arduino:avr:uno", uno.Fqbn);
        Assert.Contains("Arduino Uno", uno.Label);

        // The bare/unidentified port still surfaces, with a null FQBN.
        var bare = boards.Single(b => b.Port == "COM5");
        Assert.Null(bare.Fqbn);
        Assert.Contains("Unknown board", bare.Label);
    }

    [Fact]
    public void ParseBoardList_BareArrayShape_IsSupported()
    {
        var boards = FirmwareBuilder.ParseBoardList(BareArrayJson);

        var pico = Assert.Single(boards);
        Assert.Equal("/dev/ttyACM0", pico.Port);
        Assert.Equal("rp2040:rp2040:rpipico", pico.Fqbn);
        Assert.Contains("Raspberry Pi Pico", pico.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ParseBoardList_EmptyOrMalformed_ReturnsEmpty(string json)
    {
        Assert.Empty(FirmwareBuilder.ParseBoardList(json));
    }

    [Theory]
    [InlineData("ESP32 DevKit", "esp32:esp32:esp32")]
    [InlineData("Arduino Uno", "arduino:avr:uno")]
    [InlineData("Raspberry Pi Pico", "rp2040:rp2040:rpipico")]
    public void Fqbn_InfersFromComponents(string part, string expected)
    {
        var p = new Foundry.Core.Project.Project
        {
            Title = part,
            Components = new() { new ComponentSpec { Ref = "mcu", Alias = "MCU", Name = part } },
        };
        Assert.Equal(expected, FirmwareBuilder.Fqbn(p));
    }

    private static Foundry.Core.Project.Project ProjectFor(string mcuName) => new()
    {
        Title = mcuName,
        Components = new() { new ComponentSpec { Ref = "mcu", Alias = "MCU", Name = mcuName } },
    };

    [Fact]
    public void BuildFlashPlan_InferredEsp32_DetectedAvr_FlagsVendorMismatch()
    {
        // Firmware written for an ESP32, but an Arduino Uno is on the port: the PHYSICAL board wins and the
        // cross-family mismatch is flagged so the UI can warn before a brick.
        var plan = FirmwareBuilder.BuildFlashPlan(ProjectFor("ESP32 dev board"),
            new DetectedBoard("COM3", "arduino:avr:uno", "Arduino Uno"));
        Assert.True(plan.VendorMismatch);
        Assert.Equal("arduino:avr:uno", plan.Fqbn);                         // physical board wins
        Assert.Equal(FirmwareBuilder.FqbnSource.PortPreferredOverInferred, plan.Source);
        Assert.Contains("brick", plan.MismatchWarning ?? "", System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFlashPlan_MatchingVendor_PrefersDetectedConcreteFqbn_NoMismatch()
    {
        var plan = FirmwareBuilder.BuildFlashPlan(ProjectFor("Arduino Uno"),
            new DetectedBoard("COM5", "arduino:avr:nano", "Arduino Nano"));
        Assert.False(plan.VendorMismatch);                                  // same vendor (arduino)
        Assert.Equal("arduino:avr:nano", plan.Fqbn);                        // detected concrete board wins
        Assert.Equal(FirmwareBuilder.FqbnSource.PortPreferredOverInferred, plan.Source);
    }

    [Fact]
    public void BuildFlashPlan_UnidentifiedPort_FallsBackToInferred()
    {
        var plan = FirmwareBuilder.BuildFlashPlan(ProjectFor("ESP32 dev board"),
            new DetectedBoard("COM3", null, "Unknown"));
        Assert.False(plan.VendorMismatch);
        Assert.Equal("esp32:esp32:esp32", plan.Fqbn);
        Assert.Equal(FirmwareBuilder.FqbnSource.Inferred, plan.Source);
    }

    [Theory]
    [InlineData("arduino:avr:uno", true)]
    [InlineData("esp32:esp32:esp32:PartitionScheme=huge_app", true)]
    [InlineData("arduino avr uno", false)]
    [InlineData("arduino:avr:uno; rm -rf /", false)]
    [InlineData("", false)]
    public void IsValidFqbn_RejectsInjectionAndMalformed(string fqbn, bool ok) =>
        Assert.Equal(ok, FirmwareBuilder.IsValidFqbn(fqbn));

    [Theory]
    [InlineData("COM3", true)]
    [InlineData("/dev/ttyUSB0", true)]
    [InlineData("COM3 && calc", false)]
    [InlineData("", false)]
    public void IsValidPort_RejectsInjectionAndMalformed(string port, bool ok) =>
        Assert.Equal(ok, FirmwareBuilder.IsValidPort(port));

    [Fact]
    public async Task UploadAsync_MultipleBoardsWithNoTarget_RefusesToAutoFlash()
    {
        // With >1 connected board and no explicit target, UploadAsync must NOT flash the first one.
        // (Only meaningful when arduino-cli is present and ≥2 boards are attached; otherwise it returns a
        // NotInstalled/NoBoard result, which is also not a silent flash.)
        var r = await FirmwareBuilder.UploadAsync(ProjectFor("Arduino Uno"), target: null);
        Assert.False(r.Ok);   // never a silent first-port flash
    }
}
