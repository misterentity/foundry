using Foundry.Core.Ai;
using Foundry.Core.Config;
using Foundry.Core.Export;
using Foundry.Core.Project;

namespace Foundry.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void RoundTrip_PreservesSettings()
    {
        var cfg = new AppConfig
        {
            ModelId = "claude-opus-4-6", MaxOutputTokens = 4096, Temperature = 0.4,
            FirmwarePlatform = "MicroPython", EnclosureFormat = "3MF", Units = "mm",
            OutputFolder = Path.Combine(Path.GetTempPath(), "foundry-out"),
        };
        var path = Path.Combine(Path.GetTempPath(), $"foundry_cfg_{Guid.NewGuid():N}.json");
        try
        {
            ConfigStore.Save(cfg, path);
            var back = ConfigStore.Load(path);
            Assert.Equal(cfg.ModelId, back.ModelId);
            Assert.Equal(cfg.MaxOutputTokens, back.MaxOutputTokens);
            Assert.Equal(cfg.FirmwarePlatform, back.FirmwarePlatform);
            Assert.Equal(cfg.EnclosureFormat, back.EnclosureFormat);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = ConfigStore.Load(Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.json"));
        Assert.Equal(ModelCatalog.DefaultModelId, cfg.ModelId);
        Assert.Equal(16384, cfg.MaxOutputTokens);
    }
}

public class ExportTests
{
    [Fact]
    public void BomCsv_HasHeaderRowPerLineAndSubtotal()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var csv = Exporters.BomCsv(p);
        var lines = csv.Replace("\r\n", "\n").TrimEnd().Split('\n');

        Assert.StartsWith("Qty,Component,MPN,Unit,Extended,Stock,Distributor,Lead,Note", lines[0]);
        Assert.Equal(p.Bom.Count + 2, lines.Length); // header + lines + subtotal
        Assert.Contains(lines, l => l.Contains("ESP32-DEVKITC-32E"));
        Assert.Contains(lines, l => l.EndsWith("Subtotal"));
    }

    [Fact]
    public void GuideMarkdown_HasDisclaimerAndAllSteps()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var md = Exporters.GuideMarkdown(p);

        Assert.Contains("# Cap. Soil Moisture Sentinel — Assembly Guide", md);
        Assert.Contains("Design aid — verify before you build", md);
        foreach (var step in p.Assembly)
            Assert.Contains(step.Title, md);
    }
}
