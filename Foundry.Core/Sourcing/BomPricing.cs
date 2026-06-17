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
}
