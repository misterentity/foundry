using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Core.Project;

namespace Foundry.Core.Sidecar;

/// <summary>
/// Builds the closed enclosure schema (PRD §8.5) the CAD sidecar consumes, from the Project's
/// enclosure parameters. Cutouts and dimensions derive from the component footprints so ports
/// always line up; Claude fills the schema, the sidecar builds geometry deterministically.
/// </summary>
public static class EnclosureSchema
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <param name="arrange">
    /// <c>exploded</c> stacks the lid above the base for the 3D preview; <c>print</c> lays both flat on
    /// the plate. EXPORT must use <c>print</c> — the exploded offset was previously baked into the mesh
    /// that got written to disk, so every STL contained a lid floating above the base with overlapping
    /// XY, which no slicer can build without tens of millimetres of support.
    /// </param>
    public static string ToJson(Enclosure enclosure, string format = "stl", string arrange = "exploded")
    {
        var schema = new SchemaDto
        {
            Arrange = arrange.Equals("print", StringComparison.OrdinalIgnoreCase) ? "print" : "exploded",
            Type = "box_enclosure",
            Inner = enclosure.Inner,
            WallMm = enclosure.Wall,
            Lid = new LidDto { Style = enclosure.Lid },
            Standoffs = enclosure.Standoffs,
            Mount = enclosure.Mount,
            Format = (format ?? "stl").ToLowerInvariant() == "3mf" ? "3mf" : "stl",
            Vents = enclosure.Vents.Select(v => new VentDto { Face = v.Face, Count = v.Count }).ToList(),
            Cutouts = enclosure.Cutouts.Select(c => new CutoutDto
            {
                Face = c.Face,
                Shape = c.Shape,
                Size = c.Size,
                D = c.D,
                Pos = c.Pos,
                For = c.Label,
            }).ToList(),
        };
        return JsonSerializer.Serialize(schema, Opts);
    }

    private sealed class SchemaDto
    {
        public string Type { get; set; } = "box_enclosure";
        public double[] Inner { get; set; } = Array.Empty<double>();
        public double WallMm { get; set; }
        public LidDto? Lid { get; set; }
        public int Standoffs { get; set; }
        public string Mount { get; set; } = "none";
        public List<VentDto> Vents { get; set; } = new();
        public List<CutoutDto> Cutouts { get; set; } = new();
        public string Format { get; set; } = "stl";
        public string Arrange { get; set; } = "exploded";
    }
    private sealed class LidDto { public string Style { get; set; } = "snap"; }
    private sealed class VentDto { public string Face { get; set; } = "left"; public int Count { get; set; } }
    private sealed class CutoutDto
    {
        public string Face { get; set; } = "side";
        public string Shape { get; set; } = "rect";
        public double[]? Size { get; set; }
        public double? D { get; set; }
        public double[] Pos { get; set; } = Array.Empty<double>();
        public string For { get; set; } = "";
    }
}
