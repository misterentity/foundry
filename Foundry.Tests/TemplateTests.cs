using Foundry.Core.Project;

namespace Foundry.Tests;

public class TemplateTests
{
    [Fact]
    public void Template_Save_List_Load_RoundTrips_WithFreshId()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var marker = "ZZTest_" + Guid.NewGuid().ToString("N")[..6];
        var id = TemplateStore.Save(p, marker);
        try
        {
            Assert.StartsWith("t_", id);
            Assert.Contains(TemplateStore.List(), s => s.Id == id && s.Title == marker);

            var instance = TemplateStore.Load(id);
            Assert.NotNull(instance);
            Assert.StartsWith("p_", instance!.Id);                  // template instantiates into a new project
            Assert.NotEqual(id, instance.Id);
            Assert.Equal(p.Bom.Count, instance.Bom.Count);
            Assert.Equal(p.Connections.Count, instance.Connections.Count);
        }
        finally { TemplateStore.Delete(id); }
    }
}
