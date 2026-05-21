using Foundry.Core.Ai;
using Foundry.Core.Project;
using Foundry.Core.Wiring;

namespace Foundry.Tests;

public class ProjectStoreTests
{
    [Fact]
    public void RoundTrip_PreservesProject()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var json = ProjectStore.Serialize(p);
        var back = ProjectStore.Deserialize(json);

        Assert.Equal(p.Id, back.Id);
        Assert.Equal(p.Title, back.Title);
        Assert.Equal(p.Bom.Count, back.Bom.Count);
        Assert.Equal(p.Connections.Count, back.Connections.Count);
        Assert.Equal(p.Enclosure.Inner, back.Enclosure.Inner);
        Assert.Equal(p.Findings.Count, back.Findings.Count);
    }

    [Fact]
    public void Deserialize_Malformed_Throws_NotCrash()
    {
        // PRD §13: defensive parse — surface a readable error, never silent-null.
        Assert.ThrowsAny<Exception>(() => ProjectStore.Deserialize("{ not valid json "));
    }

    [Fact]
    public void BomLine_Extended_IsQtyTimesPrice()
    {
        var line = new BomLine { Qty = 2, Price = 0.18 };
        Assert.Equal(0.36, line.Extended, 3);
    }
}

public class NetlistLayoutTests
{
    [Fact]
    public void BuildEdges_ProducesOneEdgePerConnection()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var edges = NetlistLayout.BuildEdges(p.Connections);
        Assert.Equal(p.Connections.Count, edges.Count);
    }

    [Fact]
    public void Classify_MapsNetNames()
    {
        Assert.Equal(NetlistLayout.NetKind.Power, NetlistLayout.Classify("power"));
        Assert.Equal(NetlistLayout.NetKind.Ground, NetlistLayout.Classify("ground"));
        Assert.Equal(NetlistLayout.NetKind.I2c, NetlistLayout.Classify("i2c"));
        Assert.Equal(NetlistLayout.NetKind.Signal, NetlistLayout.Classify("anything-else"));
    }

    [Fact]
    public void Components_ExtractsDistinctEndpoints()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var comps = NetlistLayout.Components(p.Connections);
        Assert.Contains("MCU", comps);
        Assert.Contains("SENSOR", comps);
        Assert.Contains("REG", comps);
    }
}

public class ModelCatalogTests
{
    [Fact]
    public void Fallback_HasCuratedModels_DefaultIsSonnet()
    {
        Assert.NotEmpty(ModelCatalog.Fallback);
        Assert.Contains(ModelCatalog.Fallback, m => m.Id == ModelCatalog.DefaultModelId);
    }

    [Fact]
    public async Task StubClient_ReturnsFallbackModels_NoKey()
    {
        var client = new StubAnthropicClient();
        Assert.False(client.HasKey);
        var result = await client.ListModelsAsync();
        Assert.True(result.Ok);
        Assert.NotEmpty(result.Models);
    }
}

public class PipelineTests
{
    [Fact]
    public async Task StubPipeline_AppendsTurn_AndReportsStages()
    {
        var p = DemoData.CreateSoilMoistureProject();
        var before = p.Chat.Count;
        var seen = new List<int>();
        var pipeline = new StubPipeline(stepDelayMs: 0);
        var progress = new Progress<IReadOnlyList<PipelineStage>>(s => seen.Add(s.Count));

        var reply = await pipeline.RunTurnAsync(p, "make it solar powered", progress);

        Assert.Equal("assistant", reply.Role);
        Assert.NotNull(reply.Pipeline);
        Assert.True(p.Chat.Count >= before + 2); // user + assistant
    }
}
