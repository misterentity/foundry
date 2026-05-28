namespace Foundry.Core.Simulation;

/// <summary>
/// The live level of one MCU GPIO line during simulation. <see cref="Net"/> and <see cref="Endpoint"/>
/// carry the netlist identity (resolved from <see cref="GpioPinMap"/>) so the breadboard can colour the
/// right wire/pin dot without knowing anything about the emulator. <see cref="Endpoint"/> is the peripheral
/// side ("alias.pin", e.g. "LED1.A") the GPIO drives.
/// </summary>
public sealed record PinLevel(int Gpio, bool High, string? Net, string? Endpoint);

/// <summary>
/// An immutable snapshot of all known pin levels at a point in time. Indexed both by GPIO number and by
/// peripheral endpoint so <c>BreadboardControl</c> can look up either way. Snapshots are produced by
/// <see cref="SimSession"/> and handed to the UI on the dispatcher thread; never mutated in place.
/// </summary>
public sealed class PinStateSnapshot
{
    private readonly Dictionary<int, PinLevel> _byGpio;
    private readonly Dictionary<string, PinLevel> _byEndpoint;

    public static readonly PinStateSnapshot Empty = new();

    public PinStateSnapshot() : this(Array.Empty<PinLevel>()) { }

    public PinStateSnapshot(IEnumerable<PinLevel> levels)
    {
        _byGpio = new Dictionary<int, PinLevel>();
        _byEndpoint = new Dictionary<string, PinLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in levels) Index(l);
    }

    private PinStateSnapshot(Dictionary<int, PinLevel> byGpio, Dictionary<string, PinLevel> byEndpoint)
    {
        _byGpio = byGpio;
        _byEndpoint = byEndpoint;
    }

    private void Index(PinLevel l)
    {
        _byGpio[l.Gpio] = l;
        if (!string.IsNullOrEmpty(l.Endpoint)) _byEndpoint[l.Endpoint!] = l;
    }

    public IReadOnlyDictionary<int, PinLevel> ByGpio => _byGpio;
    public IReadOnlyDictionary<string, PinLevel> ByEndpoint => _byEndpoint;

    public bool TryGetGpio(int gpio, out PinLevel level) => _byGpio.TryGetValue(gpio, out level!);
    public bool TryGetEndpoint(string endpoint, out PinLevel level) => _byEndpoint.TryGetValue(endpoint, out level!);

    /// <summary>Returns a new snapshot with <paramref name="level"/> applied over this one (copy-on-write).</summary>
    public PinStateSnapshot With(PinLevel level)
    {
        var g = new Dictionary<int, PinLevel>(_byGpio);
        var e = new Dictionary<string, PinLevel>(_byEndpoint, StringComparer.OrdinalIgnoreCase);
        g[level.Gpio] = level;
        if (!string.IsNullOrEmpty(level.Endpoint)) e[level.Endpoint!] = level;
        return new PinStateSnapshot(g, e);
    }
}
