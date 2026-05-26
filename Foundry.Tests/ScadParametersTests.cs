using Foundry.Core.Cad;

namespace Foundry.Tests;

public class ScadParametersTests
{
    private const string Sample = """
// enclosure
wall_thickness = 2.0;
inner_l = 62;
inner_w = 48;
inner_h = 26;
lid_screws = 4;
$fn = 32;

module body() {
  thickness = 5;   // local, should NOT be top-level
  cube([inner_l, inner_w, inner_h]);
}
""";

    [Fact]
    public void Parse_ExtractsTopLevelNumericParams()
    {
        var ps = ScadParameters.Parse(Sample);
        Assert.Equal(5, ps.Count);                       // $fn is hidden; local "thickness" not at col 0
        Assert.Contains(ps, p => p.Name == "wall_thickness" && p.Value == 2.0);
        Assert.Contains(ps, p => p.Name == "inner_l" && p.Value == 62);
        Assert.DoesNotContain(ps, p => p.Name == "$fn");
        Assert.DoesNotContain(ps, p => p.Name == "thickness");
    }

    [Fact]
    public void Patch_UpdatesOnlyTopLevelDeclaration()
    {
        var patched = ScadParameters.Patch(Sample, "wall_thickness", 3.5);
        Assert.Contains("wall_thickness = 3.5;", patched);
        Assert.Contains("cube([inner_l, inner_w, inner_h])", patched);   // body untouched
    }

    [Fact]
    public void Parse_ThicknessHasSmallStep_CountHasIntStep()
    {
        var ps = ScadParameters.Parse("wall_thickness = 2.0;\nlid_screws = 4;");
        var wall = ps.Single(p => p.Name == "wall_thickness");
        Assert.Equal(0.1, wall.Step);
        var n = ps.Single(p => p.Name == "lid_screws");
        Assert.Equal(1, n.Step);
    }
}
