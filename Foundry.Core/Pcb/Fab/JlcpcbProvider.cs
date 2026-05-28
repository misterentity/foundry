using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// JLCPCB board house (v2.7). JLCPCB has a real, capable PCB API (Gerber upload + automated pricing + order
/// create + tracking) but it is APPROVAL-GATED and its auth contract is NOT published openly, so there is no
/// documented contract to code a live client against. Therefore this provider is ESTIMATE-QUOTE +
/// ASSISTED-HANDOFF only: <see cref="QuoteAsync"/> returns a local <see cref="FabEstimator"/> estimate, and
/// <see cref="PrepareOrderAsync"/> opens JLCPCB's instant-quote/upload page with the params + ZIP for the user
/// to finish themselves. It NEVER auto-submits, NEVER places an order. (See spec §A.)
/// </summary>
public sealed class JlcpcbProvider : IFabProvider
{
    /// <summary>JLCPCB's instant-quote / Gerber-upload page — where the user drag-drops the fab ZIP.</summary>
    public const string PortalUrl = "https://cart.jlcpcb.com/quote";

    /// <summary>Coarse house multiplier vs. the shared estimator baseline.</summary>
    private const double HouseMultiplier = 1.0;

    public string Name => "JLCPCB";

    // A key would unlock a live path, but no contract is published — so we never claim a live quote.
    public bool NeedsApiKey => true;
    public bool IsLive => false;

    public Task<FabQuote> QuoteAsync(FabOrderSpec spec, CancellationToken ct = default)
    {
        var quote = FabEstimator.Estimate(Name, spec, HouseMultiplier);
        AppLog.Info("fab", $"{Name} estimate: {quote.Summary}");
        return Task.FromResult(quote);
    }

    public Task<FabOrderHandoff> PrepareOrderAsync(FabOrderSpec spec, CancellationToken ct = default)
    {
        var (parms, clip) = FabEstimator.HandoffParams(spec);
        var notes = new List<string>
        {
            "Assisted handoff — Foundry never submits or pays. Finish the order on JLCPCB's site.",
            "Opens the JLCPCB instant-quote page; drag-drop the fab ZIP, then review price + place the order yourself.",
        };
        var handoff = new FabOrderHandoff(Name, PortalUrl, spec.ZipPath, parms, clip,
            $"Ready to upload to {Name} — opens {PortalUrl}; ZIP: {System.IO.Path.GetFileName(spec.ZipPath)}.", notes);
        AppLog.Info("fab", $"{Name} handoff prepared (assisted, not submitted).");
        return Task.FromResult(handoff);
    }
}
