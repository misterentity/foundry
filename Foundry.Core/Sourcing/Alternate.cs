namespace Foundry.Core.Sourcing;

/// <summary>A suggested substitute part for a BOM line (PRD v2 G10).</summary>
public sealed class Alternate
{
    public string Name { get; set; } = "";
    public string Mpn { get; set; } = "";
    public double Price { get; set; }
    public string Note { get; set; } = "";
    public string Replaces { get; set; } = "";   // the original part name this would swap out

    public string PriceText => Price > 0 ? $"${Price:0.00}" : "—";
}
