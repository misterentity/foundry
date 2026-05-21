using Foundry.Core.Project;

namespace Foundry.Tests;

public class ProjectLibraryTests
{
    [Fact]
    public void SaveListLoadDelete_RoundTrips()
    {
        var p = DemoData.CreateSoilMoistureProject();
        p.Id = "test_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            ProjectStore.SaveToLibrary(p);

            var summaries = ProjectStore.ListSummaries();
            var row = summaries.FirstOrDefault(s => s.Id == p.Id);
            Assert.NotNull(row);
            Assert.Equal(p.Title, row!.Title);
            Assert.Equal(p.Kpis.Parts, row.Parts);

            var loaded = ProjectStore.LoadById(p.Id);
            Assert.NotNull(loaded);
            Assert.Equal(p.Title, loaded!.Title);
            Assert.Equal(p.Connections.Count, loaded.Connections.Count);
            Assert.Equal(p.Components.Count, loaded.Components.Count);   // components survive the round-trip
        }
        finally
        {
            ProjectStore.DeleteById(p.Id);
            Assert.Null(ProjectStore.LoadById(p.Id));
        }
    }
}
