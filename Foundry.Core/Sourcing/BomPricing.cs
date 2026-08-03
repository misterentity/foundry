namespace Foundry.Core.Sourcing;

/// <summary>
/// Pure helpers for honest BOM sourcing status. Separated from the (untestable) view model so the
/// crucial "don't claim live pricing when no live quotes actually came back" rule is unit-tested.
/// </summary>
public static class BomPricing
{
    /// <summary>
    /// The status line to show after a "refresh prices" pass. <paramref name="applied"/> is how many rows
    /// actually received a live quote (GetQuoteAsync returns null on a bad/expired key, a provider outage, or
    /// schema drift). When NONE did, we must NOT claim "live pricing · updated" — the rows are still cached
    /// estimates and saying otherwise could send a user to order on stale prices. Partial coverage is reported
    /// honestly as applied/total.
    /// </summary>
    public static string RefreshStatus(string provider, int applied, int total, string timeHhmm)
    {
        if (applied <= 0)
            return $"no live prices returned — showing cached estimates (check your {provider} key / connection)";
        if (applied < total)
            return $"live pricing via {provider} · {applied}/{total} updated {timeHhmm} (the rest are estimates)";
        return $"live pricing via {provider} · updated {timeHhmm}";
    }

    /// <summary>Below this a live stock figure counts as low. Meaningless against an estimate.</summary>
    public const int LowStockThreshold = 100;

    /// <summary>Shown in place of a figure nobody fetched.</summary>
    public const string Unknown = "—";

    /// <summary>
    /// Stock as text. An estimate has no stock figure to report — the model invents a plausible integer
    /// and the UI used to render it beside a green dot, which is indistinguishable from real inventory.
    /// </summary>
    public static string StockText(Project.BomLine line) =>
        line.IsLive ? line.Stock.ToString("N0") : Unknown;

    /// <summary>Lead time as text; blank for an estimate for the same reason as <see cref="StockText"/>.</summary>
    public static string LeadText(Project.BomLine line) =>
        line.IsLive && !string.IsNullOrWhiteSpace(line.Lead) ? line.Lead : Unknown;

    /// <summary>"LIVE" once a provider answered, "EST" while the row is the model's guess.</summary>
    public static string SourceTag(Project.BomLine line) => line.IsLive ? "LIVE" : "EST";

    /// <summary>
    /// The Overview tab's stock headline. It used to read "All in stock" whenever every generated Stock
    /// happened to exceed 100 — a sourcing claim about parts nobody had looked up. A design with no live
    /// quotes has an UNKNOWN stock position, which is not the same as a good one.
    /// </summary>
    public static string StockSummary(IReadOnlyList<Project.BomLine> bom)
    {
        if (bom.Count == 0) return "no parts";

        var live = bom.Where(b => b.IsLive).ToList();
        if (live.Count == 0) return "stock not checked";

        var low = live.Count(b => b.Stock < LowStockThreshold);
        var coverage = live.Count < bom.Count ? $" ({live.Count}/{bom.Count} checked)" : "";
        return (low == 0 ? "All in stock" : $"{low} low-stock") + coverage;
    }

    /// <summary>
    /// Status for one distributor group on the Overview: "ok" / "warn" only where stock is known, and
    /// "unknown" otherwise, so an unchecked group cannot render as a healthy one.
    /// </summary>
    public static string GroupStatus(IEnumerable<Project.BomLine> group)
    {
        var lines = group as ICollection<Project.BomLine> ?? group.ToList();
        if (!lines.Any(b => b.IsLive)) return "unknown";
        return lines.Any(b => b.LowStock) ? "warn" : "ok";
    }
}
