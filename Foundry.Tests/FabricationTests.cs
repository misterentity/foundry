using Foundry.Core.Fabrication;
using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Tests;

public class FabricationTests
{
    private static Project MiniProject() => new()
    {
        Title = "Mini",
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
    public void KiCad_EmitsComponentsAndNets()
    {
        var net = KiCadNetlist.Export(MiniProject());

        Assert.StartsWith("(export (version \"D\")", net);
        Assert.Contains("(comp (ref \"MCU\")", net);
        Assert.Contains("(comp (ref \"SENSOR\")", net);
        Assert.Contains("(value \"ESP32 DevKit\")", net);
        // a power net joins the two VCC/3V3 nodes
        Assert.Contains("(net (code", net);
        Assert.Contains("(node (ref \"MCU\") (pin \"3V3\"))", net);
        Assert.Contains("(node (ref \"SENSOR\") (pin \"VCC\"))", net);
        // ground net is named GND
        Assert.Contains("(name \"GND\")", net);
        // balanced parens
        Assert.Equal(net.Count(c => c == '('), net.Count(c => c == ')'));
    }

    [Fact]
    public void KiCad_UnionsSharedEndpointsIntoOneNet()
    {
        // a 3-node net: A.OUT -> B.IN and B.IN -> C.IN should be one electrical net of 3 nodes
        var p = new Project
        {
            Title = "Bus",
            Connections = new()
            {
                new Connection { From = "A.OUT", To = "B.IN", Net = "signal" },
                new Connection { From = "B.IN", To = "C.IN", Net = "signal" },
            },
        };
        var net = KiCadNetlist.Export(p);
        // exactly one (net ...) block (the three nodes unioned)
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(net, @"\(net \(code").Count);
        Assert.Contains("(node (ref \"A\")", net);
        Assert.Contains("(node (ref \"B\")", net);
        Assert.Contains("(node (ref \"C\")", net);
    }

    [Fact]
    public void PinReport_CsvHasHeaderAndRows()
    {
        var csv = PinReport.Csv(MiniProject());
        Assert.StartsWith("Component,Pin,Net,Connected To", csv);
        Assert.Contains("MCU,3V3,power,SENSOR.VCC", csv);
    }
}

public class CartTests
{
    [Fact]
    public void MouserBomCsv_HasHeaderAndRows()
    {
        var bom = new System.Collections.Generic.List<Foundry.Core.Project.BomLine>
        {
            new() { Qty = 2, Name = "ESP32", Mpn = "ESP32-DEVKITC-32E", Price = 8.5 },
        };
        var csv = Foundry.Core.Sourcing.CartLinks.MouserBomCsv(bom);
        Assert.StartsWith("Mfr Part Number,Quantity", csv);
        Assert.Contains("ESP32-DEVKITC-32E,2", csv);
    }
}
