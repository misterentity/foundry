using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// PCBWay board house (v2.7). PCBWay is partner-gated (key by approval) but publishes its contract openly:
/// base <c>https://api-partner.pcbway.com</c>, JSON, auth via an <c>api-key</c> header. With an approved key,
/// <see cref="QuoteAsync"/> calls <c>POST /api/Pcb/PcbQuotation</c> and returns a LIVE price/lead-time
/// (<see cref="FabQuoteSource.Live"/>); with no key it falls back to a local estimate. It NEVER places an
/// order: <see cref="PrepareOrderAsync"/> is an assisted handoff (open the quote page + params + ZIP), and the
/// live-money <c>ConfirmOrder</c> step is NEVER called from anywhere. Mirrors <c>AnthropicClient</c>'s static
/// shared <see cref="HttpClient"/> with an injectable <see cref="HttpMessageHandler"/> so tests use a fake
/// handler — no real calls. (See spec §A/§B.)
/// </summary>
public sealed class PcbWayProvider : IFabProvider, IDisposable
{
    public const string BaseUrl = "https://api-partner.pcbway.com";
    public const string QuoteEndpoint = "/api/Pcb/PcbQuotation";

    /// <summary>PCBWay's instant-quote page — where the user finishes an assisted handoff.</summary>
    public const string PortalUrl = "https://www.pcbway.com/orderonline.aspx";

    private const double HouseMultiplier = 1.05;

    // Shared, long-lived client (idiomatic, like AnthropicClient). The api-key is set per-request, so one
    // instance safely serves every provider. A caller-injected client (tests' fake handler) is owned + disposed.
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;

    /// <summary>
    /// <paramref name="apiKey"/> is the approved PCBWay key (from Credential Manager; may be empty/null →
    /// estimate-only). Pass <paramref name="handler"/> in tests to use a fake <see cref="HttpMessageHandler"/>
    /// so no real network call is made.
    /// </summary>
    public PcbWayProvider(string? apiKey = null, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey?.Trim() ?? "";
        if (handler is not null)
        {
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            _ownsHttp = true;
        }
        else
        {
            _http = SharedHttp;
            _ownsHttp = false;
        }
    }

    public string Name => "PCBWay";
    public bool NeedsApiKey => true;
    public bool IsLive => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<FabQuote> QuoteAsync(FabOrderSpec spec, CancellationToken ct = default)
    {
        if (!IsLive)
            return FabEstimator.Estimate(Name, spec, HouseMultiplier);

        try
        {
            var live = await LiveQuoteAsync(spec, ct);
            if (live is not null)
            {
                AppLog.Info("fab", $"{Name} live quote: {live.Summary}");
                return live;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("fab", $"{Name} live quote failed — falling back to estimate.", ex.Message);
        }

        // Degrade to estimate so the UI always has something (note that it's a fallback).
        var est = FabEstimator.Estimate(Name, spec, HouseMultiplier);
        return est with { Notes = est.Notes.Append("Live PCBWay quote unavailable — showing estimate.").ToList() };
    }

    private async Task<FabQuote?> LiveQuoteAsync(FabOrderSpec spec, CancellationToken ct)
    {
        // Body fields per the published PcbQuotation contract (size/layers/qty/thickness/material/finish).
        var body = new Dictionary<string, object?>
        {
            ["length"] = spec.HeightMm,
            ["width"] = spec.WidthMm,
            ["layers"] = spec.Layers,
            ["qty"] = spec.Quantity,
            ["thickness"] = spec.ThicknessMm,
            ["material"] = spec.Material,
            ["SolderMask"] = "Green",
            ["Silkscreen"] = "White",
            ["PcbFileName"] = System.IO.Path.GetFileName(spec.ZipPath),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + QuoteEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("api-key", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParseLive(doc.RootElement, spec);
    }

    /// <summary>
    /// Defensive parse of a PcbQuotation response into a live <see cref="FabQuote"/>. The published example is
    /// loosely typed, so we probe a few likely property names and never throw on a shape mismatch.
    /// </summary>
    private FabQuote ParseLive(JsonElement root, FabOrderSpec spec)
    {
        decimal? price = TryDecimal(root, "price") ?? TryDecimal(root, "Price")
            ?? TryDecimal(root, "totalPrice") ?? TryDecimal(root, "amount");
        string currency = TryString(root, "currency") ?? TryString(root, "Currency") ?? "USD";
        int? lead = TryInt(root, "buildDays") ?? TryInt(root, "BuildDays") ?? TryInt(root, "leadTime");

        var notes = new List<string> { "Live PCBWay quote via PcbQuotation. Not an order — finish on PCBWay's site." };
        var msg = TryString(root, "message") ?? TryString(root, "Message");
        if (!string.IsNullOrWhiteSpace(msg)) notes.Add(msg!);

        var priceText = price is null ? "see notes" : $"${price}";
        var leadText = lead is null ? "" : $", ~{lead} business days build";
        var summary = $"{priceText} (live){leadText}.";
        return new FabQuote(Name, price, currency, lead, FabQuoteSource.Live, summary, notes);
    }

    public Task<FabOrderHandoff> PrepareOrderAsync(FabOrderSpec spec, CancellationToken ct = default)
    {
        // Always an assisted handoff — even with a live key we stop at the user finishing on PCBWay's site.
        // The documented PlaceOrder (cart) / ConfirmOrder (payment) steps are NEVER called from here.
        var (parms, clip) = FabEstimator.HandoffParams(spec);
        var notes = new List<string>
        {
            "Assisted handoff — Foundry never submits or pays. Finish the order on PCBWay's site.",
            "Opens the PCBWay quote page; upload the fab ZIP, review price + place the order yourself.",
        };
        if (IsLive) notes.Add("A PCBWay key is configured for live quotes, but ordering still happens on PCBWay's site.");

        var handoff = new FabOrderHandoff(Name, PortalUrl, spec.ZipPath, parms, clip,
            $"Ready to upload to {Name} — opens {PortalUrl}; ZIP: {System.IO.Path.GetFileName(spec.ZipPath)}.", notes);
        AppLog.Info("fab", $"{Name} handoff prepared (assisted, not submitted).");
        return Task.FromResult(handoff);
    }

    private static decimal? TryDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static int? TryInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String &&
            int.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static string? TryString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
