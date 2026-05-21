using Foundry.Core.Project;

namespace Foundry.Core.Wiring;

/// <summary>
/// Derives diagram data from the authoritative netlist (PRD §6, §8.3). The wiring view is
/// a pure render of this — power=red, ground=gray, signal=cyan, i2c=purple. Block placement
/// for the demo project is hand-tuned in the renderer; this layer owns the net→class
/// mapping and netlist summaries shared by the diagram, ledger, and future exporters.
/// </summary>
public static class NetlistLayout
{
    public enum NetKind { Power, Ground, Signal, I2c }

    public static NetKind Classify(string net) => net.ToLowerInvariant() switch
    {
        "power"  => NetKind.Power,
        "ground" => NetKind.Ground,
        "i2c"    => NetKind.I2c,
        _        => NetKind.Signal,
    };

    /// <summary>One routed edge per connection (invariant: |edges| == |connections|).</summary>
    public static IReadOnlyList<NetEdge> BuildEdges(IEnumerable<Connection> connections) =>
        connections.Select(c => new NetEdge(c.From, c.To, Classify(c.Net), c.Net)).ToList();

    /// <summary>Distinct endpoint components referenced by the netlist (e.g. MCU, SENSOR).</summary>
    public static IReadOnlyList<string> Components(IEnumerable<Connection> connections) =>
        connections
            .SelectMany(c => new[] { c.From, c.To })
            .Select(ep => ep.Split('.')[0])
            .Distinct()
            .ToList();
}

public sealed record NetEdge(string From, string To, NetlistLayout.NetKind Kind, string Net);
