namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Pure, no-network ballpark pricing used when no live API is configured (every provider's estimate path).
/// A rough function of board area × layers × quantity against a small table of published price tiers, so the
/// UI always has *something* to show. Always labelled <see cref="FabQuoteSource.Estimate"/> and clearly
/// non-binding — it is NOT a live quote. Constants are coarse public-tier ballparks (USD), not a contract.
/// </summary>
public static class FabEstimator
{
    /// <summary>
    /// Build an estimate <see cref="FabQuote"/> for a board. Pure — no I/O, never throws. Both houses share
    /// the same coarse model with a small per-house multiplier; the point is order-of-magnitude, not precision.
    /// </summary>
    public static FabQuote Estimate(string provider, FabOrderSpec spec, double houseMultiplier = 1.0)
    {
        var areaCm2 = Math.Max(1.0, (spec.WidthMm / 10.0) * (spec.HeightMm / 10.0));
        var qty = Math.Max(1, spec.Quantity);
        var layers = Math.Max(1, spec.Layers);

        // Many houses sell a small-batch (≤5 pcs, ≤100×100 mm) 2-layer board at a flat promo (~$2–5);
        // beyond that, scale by area, layer count, and quantity. Coarse on purpose.
        decimal basePrice = areaCm2 <= 100 && qty <= 5 && layers <= 2 ? 4m : 0m;
        var areaCost = (decimal)(areaCm2 * 0.06) * (decimal)(layers / 2.0);
        var qtyCost = (decimal)(qty * 0.30);
        var price = decimal.Round((basePrice + areaCost + qtyCost) * (decimal)houseMultiplier, 2);

        // Lead time: typical published build (~2 business days) plus a little for larger/many boards.
        var lead = 2 + (qty > 10 ? 2 : 0) + (areaCm2 > 200 ? 1 : 0);

        var notes = new List<string>
        {
            "Rough estimate from board size, layer count and quantity — NOT a live quote or binding price.",
            $"Board ≈ {spec.WidthMm:0.#}×{spec.HeightMm:0.#} mm, {layers}-layer, ×{qty}, {spec.ThicknessMm:0.#} mm {spec.Material}.",
        };

        var summary = $"≈ ${price} (estimate), ~{lead} business days build.";
        return new FabQuote(provider, price, "USD", lead, FabQuoteSource.Estimate, summary, notes);
    }

    /// <summary>
    /// The order params surfaced to the user for an assisted handoff (clipboard + the params dict). Shared so
    /// JLCPCB/PCBWay handoffs read identically. Pure.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Params, string Clipboard) HandoffParams(FabOrderSpec spec)
    {
        var p = new Dictionary<string, string>
        {
            ["Board size (mm)"] = $"{spec.WidthMm:0.#} × {spec.HeightMm:0.#}",
            ["Layers"] = spec.Layers.ToString(),
            ["Quantity"] = spec.Quantity.ToString(),
            ["Thickness (mm)"] = spec.ThicknessMm.ToString("0.#"),
            ["Material"] = spec.Material,
        };
        if (!string.IsNullOrWhiteSpace(spec.BoardName)) p["Board name"] = spec.BoardName!;
        p["Fab ZIP"] = spec.ZipPath;

        var clip = string.Join(Environment.NewLine, p.Select(kv => $"{kv.Key}: {kv.Value}"));
        return (p, clip);
    }
}
