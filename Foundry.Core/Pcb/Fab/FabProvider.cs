namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Board + run parameters fed to a fab provider (v2.7). Derived from the fab ZIP / .kicad_pcb via
/// <see cref="BoardDimensions"/>; <see cref="ZipPath"/> is the v2.6 <c>&lt;name&gt;-fab.zip</c> a house
/// expects (review before ordering). <see cref="Layers"/> is known from the pipeline (the 2-layer set), not derived; the rest
/// is user-chosen (quantity) or fixed for v2.7 (thickness 1.6 mm, FR-4).
/// </summary>
public sealed record FabOrderSpec(
    string ZipPath,
    double WidthMm,
    double HeightMm,
    int Layers = 2,
    int Quantity = 5,
    double ThicknessMm = 1.6,
    string Material = "FR-4",
    string? BoardName = null);

/// <summary>Where a quote came from — a rough local estimate vs. a live API price.</summary>
public enum FabQuoteSource { Estimate, Live }

/// <summary>
/// A price / lead-time quote for a board from a house. A null <see cref="Price"/> or <see cref="LeadTimeDays"/>
/// means "couldn't price it" — the caller still shows the <see cref="Summary"/>/notes. An estimate is a rough
/// ballpark and is always labelled as such (never presented as a binding price). Mirrors
/// <see cref="Foundry.Core.Sourcing.SourcingQuote"/>'s plain-record shape.
/// </summary>
public sealed record FabQuote(
    string Provider,
    decimal? Price,
    string Currency,
    int? LeadTimeDays,
    FabQuoteSource Source,
    string Summary,
    IReadOnlyList<string> Notes);

/// <summary>
/// A prepared, NOT-submitted order — the explicit-confirm boundary. The UI opens <see cref="PortalUrl"/> in
/// the browser, copies <see cref="ClipboardParams"/>, and reveals <see cref="ZipPath"/> so the user drag-drops
/// the ZIP and finishes ON THE FAB'S SITE. Foundry never auto-submits and never pays.
/// </summary>
public sealed record FabOrderHandoff(
    string Provider,
    string PortalUrl,
    string ZipPath,
    IReadOnlyDictionary<string, string> Params,
    string ClipboardParams,
    string Summary,
    IReadOnlyList<string> Notes);

/// <summary>
/// A board house that can quote a board and PREPARE (never submit) an order. Mirrors
/// <see cref="Foundry.Core.Sourcing.ISourcingProvider"/>. <see cref="PrepareOrderAsync"/> always returns an
/// assisted handoff — it never auto-submits, never pays. Providers degrade gracefully (no throws on the
/// normal paths) so the UI always has at least an estimate + a place to upload.
/// </summary>
public interface IFabProvider
{
    string Name { get; }

    /// <summary>True if a live API path needs a user key; estimate + handoff work regardless.</summary>
    bool NeedsApiKey { get; }

    /// <summary>True when configured with a key AND able to return a LIVE quote.</summary>
    bool IsLive { get; }

    Task<FabQuote> QuoteAsync(FabOrderSpec spec, CancellationToken ct = default);

    /// <summary>Prepare an assisted handoff. NEVER auto-submits, NEVER pays. Always returns a handoff.</summary>
    Task<FabOrderHandoff> PrepareOrderAsync(FabOrderSpec spec, CancellationToken ct = default);
}
