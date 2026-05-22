using Foundry.Core.Project;

namespace Foundry.Tests;

public class BundleTests
{
    [Fact]
    public void Bundle_RoundTrips_TheProject()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var zip = Path.Combine(Path.GetTempPath(), $"fb_{Guid.NewGuid():N}{ProjectBundle.Extension}");
        try
        {
            ProjectBundle.Export(p, zip);
            Assert.True(File.Exists(zip));

            var back = ProjectBundle.Import(zip);
            Assert.Equal(p.Title, back.Title);
            Assert.Equal(p.Bom.Count, back.Bom.Count);
            Assert.Equal(p.Connections.Count, back.Connections.Count);
            Assert.Equal(p.Components.Count, back.Components.Count);
            // the canonical JSON survives byte-for-byte
            Assert.Equal(ProjectStore.Serialize(p), ProjectStore.Serialize(back));
        }
        finally { if (File.Exists(zip)) File.Delete(zip); }
    }

    [Fact]
    public void Import_RejectsNonBundle()
    {
        var zip = Path.Combine(Path.GetTempPath(), $"bad_{Guid.NewGuid():N}.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            z.CreateEntry("notes.txt");
        try { Assert.Throws<InvalidDataException>(() => ProjectBundle.Import(zip)); }
        finally { if (File.Exists(zip)) File.Delete(zip); }
    }
}
