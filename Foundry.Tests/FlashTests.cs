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
}
