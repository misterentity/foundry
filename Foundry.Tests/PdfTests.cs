using Foundry.Core.Export;
using Foundry.Core.Project;

namespace Foundry.Tests;

public class PdfTests
{
    [Fact]
    public void ProjectPdf_ProducesValidPdfBytes()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var bytes = PdfExporter.ProjectPdf(p);

        Assert.True(bytes.Length > 2000, $"PDF too small: {bytes.Length} bytes");
        // %PDF- magic header
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, bytes.Take(5).ToArray());
    }

    [Fact]
    public void ValidationPdf_ProducesValidPdfBytes()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var bytes = PdfExporter.ValidationPdf(p);
        Assert.True(bytes.Length > 1500);
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, bytes.Take(5).ToArray());
    }
}
