using System.Text;
using Foundry.Core.Project;

namespace Foundry.Core.Sourcing;

/// <summary>
/// Builds distributor buy/cart links and BOM-upload artifacts (PRD §8.7). Per-line search URLs
/// are stable public endpoints; bulk checkout is handled by exporting a distributor-format BOM CSV
/// the user uploads to the distributor's BOM manager (DigiKey/Mouser both support BOM upload).
/// </summary>
public static class CartLinks
{
    public const string DigiKeyBomManager = "https://www.digikey.com/en/mylists/list/bom-manager";
    public const string MouserBom = "https://www.mouser.com/Bom/";

    public static string Search(string distributor, string mpn) =>
        distributor.ToLowerInvariant() switch
        {
            "mouser" => MouserSearch(mpn),
            "amazon" => AmazonSearch(mpn),
            _ => DigiKeySearch(mpn),
        };

    public static string DigiKeySearch(string mpn) =>
        $"https://www.digikey.com/en/products/result?keywords={Uri.EscapeDataString(mpn)}";

    public static string MouserSearch(string mpn) =>
        $"https://www.mouser.com/c/?q={Uri.EscapeDataString(mpn)}";

    public static string AmazonSearch(string mpn) =>
        $"https://www.amazon.com/s?k={Uri.EscapeDataString(mpn)}";

    /// <summary>DigiKey BOM-manager CSV: "Quantity,Part Number" — uploadable at the BOM manager.</summary>
    public static string DigiKeyBomCsv(IEnumerable<BomLine> bom)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Quantity,Part Number");
        foreach (var l in bom)
            sb.AppendLine($"{l.Qty},{Escape(l.Mpn)}");
        return sb.ToString();
    }

    private static string Escape(string field) =>
        field.Contains(',') || field.Contains('"')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
}
