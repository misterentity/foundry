using System.Text.Json;
using Foundry.Core.Project;
using Foundry.Core.Sidecar;

namespace Foundry.Tests;

public class EnclosureSchemaTests
{
    [Fact]
    public void ToJson_EmitsSidecarSchema()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var json = EnclosureSchema.ToJson(p.Enclosure);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("box_enclosure", root.GetProperty("type").GetString());
        Assert.Equal(62, root.GetProperty("inner")[0].GetDouble());
        Assert.Equal(2.0, root.GetProperty("wall_mm").GetDouble());          // snake_case for the sidecar
        Assert.Equal("snap", root.GetProperty("lid").GetProperty("style").GetString());
        Assert.Equal(p.Enclosure.Cutouts.Count, root.GetProperty("cutouts").GetArrayLength());
        // a circular cutout carries its diameter
        Assert.Contains(root.GetProperty("cutouts").EnumerateArray(),
            c => c.TryGetProperty("d", out var d) && d.GetDouble() == 12);
    }
}
