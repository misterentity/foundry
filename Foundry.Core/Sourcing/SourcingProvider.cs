namespace Foundry.Core.Sourcing;

/// <summary>A price/availability quote for one MPN from a distributor (PRD §8.7).</summary>
public sealed record SourcingQuote(
    string Mpn,
    string Distributor,
    double UnitPrice,
    int Stock,
    string Lead,
    string? DatasheetUrl = null,
    string? ProductUrl = null);

/// <summary>A source of live price/availability data (Nexar aggregator, DigiKey, Mouser, …).</summary>
public interface ISourcingProvider
{
    string Name { get; }
    /// <summary>True when configured with a key and able to return live quotes.</summary>
    bool IsLive { get; }
    Task<SourcingQuote?> GetQuoteAsync(string mpn, CancellationToken ct = default);
}

/// <summary>
/// No-key fallback (PRD §8.7 graceful degradation): returns no live quotes, so the UI keeps
/// showing the cached estimates already on the BOM. Used until a sourcing key is configured.
/// </summary>
public sealed class NullSourcingProvider : ISourcingProvider
{
    public string Name => "offline";
    public bool IsLive => false;
    public Task<SourcingQuote?> GetQuoteAsync(string mpn, CancellationToken ct = default) =>
        Task.FromResult<SourcingQuote?>(null);
}
