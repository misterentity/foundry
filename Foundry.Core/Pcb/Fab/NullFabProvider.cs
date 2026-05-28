namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// No-key default fallback (mirrors <see cref="Foundry.Core.Sourcing.NullSourcingProvider"/>): always
/// available, never throws, degrades gracefully so the UI always has *something* — a local estimate and a
/// place to upload. <see cref="QuoteAsync"/> returns an estimate; <see cref="PrepareOrderAsync"/> defaults to
/// the JLCPCB instant-quote handoff. Used by <see cref="FabService.Shared"/> until App wires a keyed provider.
/// </summary>
public sealed class NullFabProvider : IFabProvider
{
    private readonly JlcpcbProvider _handoff = new();

    public string Name => "offline";
    public bool NeedsApiKey => false;
    public bool IsLive => false;

    public Task<FabQuote> QuoteAsync(FabOrderSpec spec, CancellationToken ct = default) =>
        Task.FromResult(FabEstimator.Estimate(Name, spec));

    public Task<FabOrderHandoff> PrepareOrderAsync(FabOrderSpec spec, CancellationToken ct = default) =>
        _handoff.PrepareOrderAsync(spec, ct);
}
