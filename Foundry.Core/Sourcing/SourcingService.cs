using System.Collections.Concurrent;

namespace Foundry.Core.Sourcing;

/// <summary>
/// Orchestrates sourcing lookups over a provider with an in-memory cache (PRD §8.7 "cache
/// results"). Selected once at app startup based on which keys are configured; exposed as a
/// process-wide <see cref="Shared"/> instance the BOM view model reads.
/// </summary>
public sealed class SourcingService
{
    private readonly ISourcingProvider _provider;
    private readonly ConcurrentDictionary<string, SourcingQuote?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SourcingService(ISourcingProvider provider) => _provider = provider;

    /// <summary>Process-wide instance; defaults to offline until App configures it.</summary>
    public static SourcingService Shared { get; set; } = new(new NullSourcingProvider());

    public bool IsLive => _provider.IsLive;
    public string ProviderName => _provider.Name;

    public async Task<SourcingQuote?> GetQuoteAsync(string mpn, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(mpn, out var cached)) return cached;
        var quote = await _provider.GetQuoteAsync(mpn, ct);
        _cache[mpn] = quote;
        return quote;
    }

    public async Task<IReadOnlyDictionary<string, SourcingQuote>> GetQuotesAsync(
        IEnumerable<string> mpns, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SourcingQuote>(StringComparer.OrdinalIgnoreCase);
        foreach (var mpn in mpns.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var q = await GetQuoteAsync(mpn, ct);
            if (q is not null) result[mpn] = q;
        }
        return result;
    }

    public void ClearCache() => _cache.Clear();
}
