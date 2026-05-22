using Foundry.Core.Firmware;
namespace Foundry.Tests;
public class BuildTests
{
    [Fact]
    public void Parse_Json_Success_NoDiagnostics()
    {
        var (ok, diags) = FirmwareBuilder.Parse("{\"success\":true,\"builder_result\":{\"diagnostics\":[]}}", "", 0);
        Assert.True(ok);
        Assert.Empty(diags);
    }

    [Fact]
    public void Parse_Json_Errors_AreExtracted()
    {
        var json = "{\"success\":false,\"builder_result\":{\"diagnostics\":[" +
                   "{\"severity\":\"ERROR\",\"message\":\"'PIN_LED' was not declared\",\"file\":\"/tmp/foundrybuild/foundrybuild.ino\",\"line\":13,\"column\":3}]}}";
        var (ok, diags) = FirmwareBuilder.Parse(json, "", 1);
        Assert.False(ok);
        var d = Assert.Single(diags);
        Assert.Equal("error", d.Severity);
        Assert.Equal("foundrybuild.ino", d.File);
        Assert.Equal(13, d.Line);
        Assert.Contains("PIN_LED", d.Message);
    }

    [Fact]
    public void Parse_FallsBackToStderr_WhenNotJson()
    {
        var (ok, diags) = FirmwareBuilder.Parse("not json", "main.ino:5: error: expected ';'", 1);
        Assert.False(ok);
        Assert.Contains(diags, d => d.Message.Contains("error:"));
    }

    [Theory]
    [InlineData("ESP32 DevKit", "esp32:esp32:esp32")]
    [InlineData("Arduino Uno", "arduino:avr:uno")]
    [InlineData("Raspberry Pi Pico", "rp2040:rp2040:rpipico")]
    public void Fqbn_InfersFromComponents(string part, string expected)
    {
        var p = new Foundry.Core.Project.Project { Title = part, Components = new() { new Foundry.Core.Kb.ComponentSpec { Ref = "mcu", Alias = "MCU", Name = part } } };
        Assert.Equal(expected, FirmwareBuilder.Fqbn(p));
    }
}
