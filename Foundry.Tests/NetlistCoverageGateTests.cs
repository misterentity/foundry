using Foundry.Core.Fabrication;
using Xunit.Abstractions;

namespace Foundry.Tests;

/// <summary>
/// THE COVERAGE GATE. Before writing a single verification rule, measure what fraction of nets on REAL
/// boards a rule could reach a sound verdict on. An honest verifier that must answer "unproven" on most
/// of a board is worth less than a stranger guessing, and no amount of downstream engineering fixes thin
/// ground truth.
///
/// Corpus: every schematic bundled with KiCad, exported via
/// <c>kicad-cli sch export netlist --format kicadsexpr</c> into the dir named by FOUNDRY_NET_CORPUS.
/// Self-skips when the corpus is absent so CI stays green without KiCad.
/// </summary>
public class NetlistCoverageGateTests
{
    private readonly ITestOutputHelper _out;
    public NetlistCoverageGateTests(ITestOutputHelper o) => _out = o;

    // How a pin behaves on its net.
    private enum Role { Driver, WeakDriver, Consumer, Passive, NoConnect, Unknown }

    private static Role RoleOf(NetNode n)
    {
        if (n.IsUnknown) return Role.Unknown;
        if (n.Is("output") || n.Is("power_out") || n.Is("bidirectional") || n.Is("tri_state")) return Role.Driver;
        if (n.Is("open_collector")) return Role.WeakDriver;
        if (n.Is("input") || n.Is("power_in")) return Role.Consumer;
        if (n.Is("passive")) return Role.Passive;
        if (n.Is("no_connect")) return Role.NoConnect;
        return Role.Unknown;
    }

    /// <summary>
    /// Can the "is this net driven?" rule reach a SOUND verdict? Only when nothing on the net is unknown
    /// AND nothing is passive — a passive pin (pull-up resistor, 0R link, connector) may conduct a driver
    /// in from a different net, and per-net analysis cannot see that. This is the honest limit of a
    /// netlist-only verifier, and measuring it is the whole point of the gate.
    /// </summary>
    private static bool DriveDecidable(ImportedNet net) =>
        net.Nodes.Count > 0 && net.Nodes.All(n => RoleOf(n) is not (Role.Unknown or Role.Passive));

    /// <summary>
    /// Per-net analysis calls a net undecidable the moment it holds a passive pin — and passives are 54% of
    /// all pins, so that alone caps coverage near a third. But a passive is a CONDUCTOR: a pull-up resistor,
    /// a 0R link, a ferrite, a connector. Follow it through its component to that component's other pins and
    /// on to their nets, and the driver usually turns up one or two hops away. This is the same union-find
    /// shape Foundry already uses in KiCadNetlist, run over an imported design instead of a generated one.
    /// Returns (driverReachable, sound) — sound is false if an unspecified pin was met anywhere in the closure,
    /// because then absence-of-driver cannot be concluded.
    /// </summary>
    private static (bool Reachable, bool Sound) DriverReachable(
        ImportedNet start,
        IReadOnlyDictionary<int, ImportedNet> netsByCode,
        IReadOnlyDictionary<string, List<(string Pin, int NetCode)>> pinsByRef,
        int maxHops = 4)
    {
        var seen = new HashSet<int> { start.Code };
        var frontier = new List<ImportedNet> { start };
        bool sound = true;

        for (int hop = 0; hop <= maxHops && frontier.Count > 0; hop++)
        {
            var next = new List<ImportedNet>();
            foreach (var net in frontier)
            {
                foreach (var node in net.Nodes)
                {
                    var role = RoleOf(node);
                    if (role is Role.Driver or Role.WeakDriver) return (true, sound);
                    if (role == Role.Unknown) { sound = false; continue; }
                    if (role != Role.Passive) continue;

                    // Conduct through this passive component to the nets on its other pins.
                    if (!pinsByRef.TryGetValue(node.Ref, out var pins)) continue;

                    // A connector is a hole in the world: whatever drives this net may be off-board, and no
                    // netlist can see it. That is UNPROVABLE, not undriven — say so instead of failing the net.
                    if (node.Ref.Length > 0 && (node.Ref[0] is 'J' or 'P' or 'X') && pins.Count > 1)
                    {
                        sound = false;
                        continue;
                    }
                    // Only a genuine 2-terminal part conducts predictably (R, L, ferrite, 0R link). An IC whose
                    // pins are marked passive does not, and pretending otherwise manufactures false passes.
                    if (pins.Count != 2) continue;
                    foreach (var (pin, code) in pins)
                    {
                        if (pin == node.Pin || !seen.Add(code)) continue;
                        if (netsByCode.TryGetValue(code, out var n2)) next.Add(n2);
                    }
                }
            }
            frontier = next;
        }
        return (false, sound);
    }

    [Fact]
    public void CoverageGate_OverRealKiCadBoards()
    {
        var dir = Environment.GetEnvironmentVariable("FOUNDRY_NET_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _out.WriteLine("FOUNDRY_NET_CORPUS not set / missing — skipping coverage gate.");
            return;
        }

        var files = Directory.GetFiles(dir, "*.net").OrderBy(f => f).ToList();
        Assert.NotEmpty(files);

        int totNets = 0, totNodes = 0, totTyped = 0, totUnknownNodes = 0;
        int fullyTyped = 0, driveDecidable = 0, consumerPassiveOnly = 0;
        int fireUndriven = 0, fireMultiDriver = 0, fireNcShared = 0, fireDangling = 0;
        int applicableUndriven = 0, decidableAnyRule = 0;
        var typeHist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        _out.WriteLine($"{"board",-32} {"nets",6} {"nodes",7} {"typed%",7} {"drive-decidable%",17}");
        _out.WriteLine(new string('-', 74));

        int travApplicable = 0, travFires = 0, travUnsound = 0;
        int pwrApplicable = 0, pwrFires = 0, logicApplicable = 0, logicFires = 0;

        foreach (var f in files)
        {
            var design = KiCadNetlistReader.Parse(File.ReadAllText(f));
            int bNets = design.Nets.Count, bNodes = 0, bTyped = 0, bDecide = 0;

            // indexes for traversal-through-passives
            var netsByCode = design.Nets.GroupBy(n => n.Code).ToDictionary(g => g.Key, g => g.First());
            var pinsByRef = new Dictionary<string, List<(string, int)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in design.Nets)
                foreach (var nd in n.Nodes)
                {
                    if (!pinsByRef.TryGetValue(nd.Ref, out var l)) pinsByRef[nd.Ref] = l = new();
                    l.Add((nd.Pin, n.Code));
                }

            foreach (var net in design.Nets)
            {
                bNodes += net.Nodes.Count;
                foreach (var n in net.Nodes)
                {
                    foreach (var t in n.Types) typeHist[t] = typeHist.GetValueOrDefault(t) + 1;
                    if (n.IsUnknown) totUnknownNodes++; else bTyped++;
                }

                bool allTyped = net.Nodes.Count > 0 && net.Nodes.All(n => !n.IsUnknown);
                if (allTyped) fullyTyped++;

                var roles = net.Nodes.Select(RoleOf).ToList();
                bool decidable = DriveDecidable(net);
                if (decidable) { bDecide++; driveDecidable++; }

                // Rule applicability + firing (these are SHIPPING boards, so fires are precision suspects).
                bool hasConsumer = roles.Contains(Role.Consumer);
                bool hasDriver = roles.Contains(Role.Driver) || roles.Contains(Role.WeakDriver);
                if (hasConsumer && decidable) { applicableUndriven++; if (!hasDriver) fireUndriven++; }
                if (decidable && roles.Count(r => r == Role.Driver) > 1) fireMultiDriver++;
                if (net.Nodes.Count > 1 && roles.Contains(Role.NoConnect)) fireNcShared++;
                if (net.Nodes.Count == 1) fireDangling++;
                if (hasConsumer && !decidable && roles.Contains(Role.Passive)) consumerPassiveOnly++;

                // The same rule, but conducting through passives instead of giving up at them.
                if (hasConsumer)
                {
                    var (reach, sound) = DriverReachable(net, netsByCode, pinsByRef);
                    if (!sound) travUnsound++;
                    else
                    {
                        travApplicable++;
                        if (!reach) travFires++;
                        // Split by consumer kind: a logic input that nothing drives is a real defect; a
                        // power_in rail with no reachable power_out is usually just KiCad's convention
                        // (rails are fed by power symbols/PWR_FLAG, not by a pin that declares power_out).
                        bool isPower = net.Nodes.Any(n => n.Is("power_in"));
                        if (isPower) { pwrApplicable++; if (!reach) pwrFires++; }
                        else { logicApplicable++; if (!reach) logicFires++; }
                    }
                }

                // "any rule can reach a verdict": driver analysis, or the two structural rules that need no types.
                if (decidable || net.Nodes.Count == 1 || (net.Nodes.Count > 1 && roles.Contains(Role.NoConnect)))
                    decidableAnyRule++;
            }

            totNets += bNets; totNodes += bNodes; totTyped += bTyped;
            _out.WriteLine($"{Path.GetFileNameWithoutExtension(f),-32} {bNets,6} {bNodes,7} " +
                           $"{Pct(bTyped, bNodes),6:N1}% {Pct(bDecide, bNets),16:N1}%");
        }

        _out.WriteLine("");
        _out.WriteLine($"TOTAL nets {totNets:N0} · nodes {totNodes:N0}");
        _out.WriteLine($"  nodes carrying a usable pintype : {Pct(totTyped, totNodes):N1}%  ({totUnknownNodes:N0} unspecified)");
        _out.WriteLine($"  nets fully typed                : {Pct(fullyTyped, totNets):N1}%");
        _out.WriteLine($"  nets DRIVE-DECIDABLE            : {Pct(driveDecidable, totNets):N1}%   <-- the gate number");
        _out.WriteLine($"  nets decidable by ANY rule      : {Pct(decidableAnyRule, totNets):N1}%");
        _out.WriteLine($"  consumer+passive (needs part traversal) : {Pct(consumerPassiveOnly, totNets):N1}%");
        _out.WriteLine("");
        _out.WriteLine("RULE FIRES on shipping boards (precision proxy — these should be RARE):");
        _out.WriteLine($"  undriven consumer net : {fireUndriven,6}  of {applicableUndriven,6} applicable ({Pct(fireUndriven, applicableUndriven):N1}%)");
        _out.WriteLine($"  ...WITH passive traversal: {travFires,6}  of {travApplicable,6} sound ({Pct(travFires, travApplicable):N1}%)   [{travUnsound} unsound]");
        _out.WriteLine($"  ...consumer-net coverage : {Pct(travApplicable, travApplicable + travUnsound):N1}% sound");
        _out.WriteLine($"     split — LOGIC inputs  : {logicFires,6}  of {logicApplicable,6} ({Pct(logicFires, logicApplicable):N1}% fire)");
        _out.WriteLine($"     split — POWER rails   : {pwrFires,6}  of {pwrApplicable,6} ({Pct(pwrFires, pwrApplicable):N1}% fire)");
        _out.WriteLine($"  multiple hard drivers : {fireMultiDriver,6}");
        _out.WriteLine($"  no_connect on shared  : {fireNcShared,6}");
        _out.WriteLine($"  single-node (dangling): {fireDangling,6}");
        _out.WriteLine("");
        _out.WriteLine("pintype histogram:");
        foreach (var kv in typeHist.OrderByDescending(k => k.Value))
            _out.WriteLine($"  {kv.Key,-18} {kv.Value,7:N0}");

        Assert.True(totNets > 0, "corpus produced no nets — the reader or the export is broken");
    }

    private static double Pct(int n, int d) => d == 0 ? 0 : 100.0 * n / d;

    // ---- reader unit tests (no KiCad needed) ------------------------------------------------------

    private const string Sample = """
    (export (version "E")
      (components
        (comp (ref "R103") (value "10k") (footprint "Resistor_SMD:R_0805_2012Metric"))
        (comp (ref "U1") (value "TPS63031")))
      (nets
        (net (code "1") (name "+1V8")
          (node (ref "Module301") (pin "88") (pinfunction "+1.8v_(Output)_88") (pintype "power_out"))
          (node (ref "R103") (pin "1") (pintype "passive")))
        (net (code "2") (name "/EN")
          (node (ref "U1") (pin "5") (pinfunction "EN") (pintype "input"))
          (node (ref "C4") (pin "1") (pintype "passive+no_connect")))))
    """;

    [Fact]
    public void Reader_ParsesNetsNodesAndComponents()
    {
        var d = KiCadNetlistReader.Parse(Sample);

        Assert.Equal(2, d.Components.Count);
        Assert.Equal("Resistor_SMD:R_0805_2012Metric", d.Components[0].Footprint);
        Assert.Null(d.Components[1].Footprint);          // unassigned footprint stays null, never guessed

        Assert.Equal(2, d.Nets.Count);
        var en = d.Nets.Single(n => n.Name == "/EN");
        Assert.Equal(2, en.Code);
        Assert.Equal("U1.5", en.Nodes[0].Endpoint);
        Assert.Equal("EN", en.Nodes[0].PinFunction);
        Assert.True(en.Nodes[0].Is("input"));
    }

    [Fact]
    public void Reader_SplitsCompoundPinTypes()
    {
        var d = KiCadNetlistReader.Parse(Sample);
        var c4 = d.Nets.Single(n => n.Name == "/EN").Nodes[1];

        // KiCad emits "passive+no_connect" — reading it as one opaque string loses BOTH facts.
        Assert.Equal(new[] { "passive", "no_connect" }, c4.Types);
        Assert.True(c4.Is("passive"));
        Assert.True(c4.Is("no_connect"));
        Assert.False(c4.IsUnknown);
    }

    [Theory]
    [InlineData("unspecified")]
    [InlineData("")]
    public void Reader_TreatsUntypedPinsAsUnknown(string pintype)
    {
        var d = KiCadNetlistReader.Parse(
            $"""(export (nets (net (code "1") (name "N") (node (ref "U1") (pin "1") (pintype "{pintype}")))))""");
        Assert.True(d.Nets[0].Nodes[0].IsUnknown);
    }

    // Malformed input must yield an EMPTY design, which the caller reports as unproven — never as a clean pass.
    [Theory]
    [InlineData("")]
    [InlineData("not an s-expression")]
    [InlineData("(export (nets (net (code \"1\")")]
    public void Reader_NeverThrowsAndNeverInventsNets(string text)
    {
        var d = KiCadNetlistReader.Parse(text);
        Assert.Empty(d.Nets);
    }

    [Fact]
    public void Reader_HandlesQuotedAtomsWithEscapes()
    {
        var d = KiCadNetlistReader.Parse(
            """(export (nets (net (code "3") (name "/A \"B\" C") (node (ref "U1") (pin "1") (pintype "input")))))""");
        Assert.Equal("/A \"B\" C", d.Nets[0].Name);
    }
}
