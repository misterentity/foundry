using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Orchestrates fab quoting + order-prep over a selected <see cref="IFabProvider"/>, mirroring
/// <see cref="Foundry.Core.Sourcing.SourcingService"/>. Selected once at startup based on which keys are
/// configured; exposed as a process-wide <see cref="Shared"/> instance the fab-order view model reads.
/// Defaults to <see cref="NullFabProvider"/> (estimate + handoff) until App wires keys. Never auto-submits.
/// </summary>
public sealed class FabService
{
    private readonly IFabProvider _provider;

    public FabService(IFabProvider provider) => _provider = provider;

    /// <summary>Process-wide instance; defaults to offline (estimate + handoff) until App configures it.</summary>
    public static FabService Shared { get; set; } = new(new NullFabProvider());

    public bool IsLive => _provider.IsLive;
    public bool NeedsApiKey => _provider.NeedsApiKey;
    public string ProviderName => _provider.Name;

    public Task<FabQuote> QuoteAsync(FabOrderSpec spec, CancellationToken ct = default) =>
        _provider.QuoteAsync(spec, ct);

    /// <summary>
    /// Prepare an assisted handoff. NEVER auto-submits, NEVER pays — the user finishes on the fab's site.
    /// </summary>
    public Task<FabOrderHandoff> PrepareOrderAsync(FabOrderSpec spec, CancellationToken ct = default) =>
        _provider.PrepareOrderAsync(spec, ct);

    /// <summary>
    /// Build a <see cref="FabOrderSpec"/> from the v2.6 fab artifacts: the ZIP path plus board size derived
    /// from the source <c>.kicad_pcb</c> (best) via <see cref="BoardDimensions.FromKicadPcb"/>. Reads the board
    /// text from <paramref name="kicadPcbPath"/> when given; otherwise falls back to the default footprint.
    /// Pure-ish (one file read, guarded) — never throws.
    /// </summary>
    public static FabOrderSpec BuildSpec(
        string zipPath, string? kicadPcbPath = null, int quantity = 5, string? boardName = null)
    {
        double w = BoardDimensions.Default.WidthMm, h = BoardDimensions.Default.HeightMm;
        try
        {
            if (!string.IsNullOrWhiteSpace(kicadPcbPath) && System.IO.File.Exists(kicadPcbPath))
                (w, h) = BoardDimensions.FromKicadPcb(System.IO.File.ReadAllText(kicadPcbPath));
        }
        catch (Exception ex)
        {
            AppLog.Warn("fab", "Couldn't read board outline — using default size.", ex.Message);
        }

        return new FabOrderSpec(zipPath, w, h, Layers: FabLayers, Quantity: Math.Max(1, quantity),
            BoardName: boardName ?? (string.IsNullOrWhiteSpace(zipPath) ? null
                : System.IO.Path.GetFileNameWithoutExtension(zipPath)));
    }

    /// <summary>v2.7 is the standard 2-layer set (matches <see cref="FabOptions.DefaultLayers"/>).</summary>
    public const int FabLayers = 2;

    /// <summary>
    /// Select the provider from configured keys (mirrors how App picks the sourcing provider). Prefers a keyed
    /// PCBWay (live quotes), then keyed JLCPCB (estimate + handoff), else the no-key fallback. Pass the keys
    /// read from Credential Manager (<c>Foundry:PcbWay</c> / <c>Foundry:Jlcpcb</c>); empty/null = not configured.
    /// </summary>
    public static IFabProvider Select(string? pcbWayKey = null, bool jlcpcbConfigured = false)
    {
        if (!string.IsNullOrWhiteSpace(pcbWayKey))
            return new PcbWayProvider(pcbWayKey);
        if (jlcpcbConfigured)
            return new JlcpcbProvider();
        return new NullFabProvider();
    }
}
