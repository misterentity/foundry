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

    public static string ToJson(Enclosure enclosure)
    {
        var schema = new SchemaDto
        {
            Type = "box_enclosure",
            Inner = enclosure.Inner,
            WallMm = enclosure.Wall,
            Lid = new LidDto { Style = enclosure.Lid },
            Standoffs = enclosure.Standoffs,
            Mount = enclosure.Mount,
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
