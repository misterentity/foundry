using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Foundry.Core.Sourcing;

/// <summary>
/// Live sourcing via the Nexar / Octopart API (PRD §8.7 — aggregator first, covers DigiKey,
/// Mouser, etc.). OAuth2 client-credentials against identity.nexar.com, then a GraphQL part
/// search against api.nexar.com. Defensive throughout: any failure yields a null quote so the
/// caller degrades to cached estimates. Key is "client_id:client_secret" from Credential Manager.
/// </summary>
public sealed class NexarSourcingProvider : ISourcingProvider, IDisposable
{
    private const string TokenUrl = "https://identity.nexar.com/connect/token";
    private const string GraphQlUrl = "https://api.nexar.com/graphql";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;

    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public NexarSourcingProvider(string credential, HttpClient? http = null)
    {
        var parts = (credential ?? "").Split(':', 2);
        _clientId = parts.Length > 0 ? parts[0].Trim() : "";
        _clientSecret = parts.Length > 1 ? parts[1].Trim() : "";
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public string Name => "Nexar";
    public bool IsLive => !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret);

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["scope"] = "supply.domain",
            }),
        };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        _token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        var expires = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expires - 60);
        return _token;
    }

    private const string Query =
        "query P($mpn:String!){supSearchMpn(q:$mpn,limit:1){results{part{mpn bestDatasheet{url} " +
        "sellers{company{name} offers{inventoryLevel clickUrl prices{quantity price}}}}}}}";

    public async Task<SourcingQuote?> GetQuoteAsync(string mpn, CancellationToken ct = default)
    {
        if (!IsLive) return null;
        try
        {
            var token = await GetTokenAsync(ct);
            if (token is null) return null;

            var payload = JsonSerializer.Serialize(new { query = Query, variables = new { mpn } });
            using var req = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return Parse(doc.RootElement, mpn);
        }
        catch { return null; }
    }

    private static SourcingQuote? Parse(JsonElement root, string mpn)
    {
        if (!root.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("supSearchMpn", out var search)) return null;
        if (!search.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;

        var part = results[0].GetProperty("part");
        string? datasheet = part.TryGetProperty("bestDatasheet", out var ds) && ds.ValueKind == JsonValueKind.Object
            ? ds.GetProperty("url").GetString() : null;

        SourcingQuote? best = null;
        if (part.TryGetProperty("sellers", out var sellers))
        {
            foreach (var seller in sellers.EnumerateArray())
            {
                var dist = seller.GetProperty("company").GetProperty("name").GetString() ?? "?";
                if (!seller.TryGetProperty("offers", out var offers)) continue;
                foreach (var offer in offers.EnumerateArray())
                {
                    int stock = offer.TryGetProperty("inventoryLevel", out var inv) ? inv.GetInt32() : 0;
                    string? url = offer.TryGetProperty("clickUrl", out var cu) ? cu.GetString() : null;
                    double price = double.MaxValue;
                    if (offer.TryGetProperty("prices", out var prices))
                        foreach (var p in prices.EnumerateArray())
                            if (p.TryGetProperty("price", out var pv)) price = Math.Min(price, pv.GetDouble());
                    if (price == double.MaxValue) continue;

                    // prefer in-stock, then lowest unit price
                    bool better = best is null
                        || (stock > 0 && best.Stock == 0)
                        || (Math.Sign(stock) == Math.Sign(best.Stock) && price < best.UnitPrice);
                    if (better)
                        best = new SourcingQuote(mpn, dist, price, stock, stock > 0 ? "Stock" : "—", datasheet, url);
                }
            }
        }
        return best;
    }

    public void Dispose() => _http.Dispose();
}
