using System.Text;

namespace Foundry.Core.Fabrication;

/// <summary>One pin sitting on a net, as KiCad reports it.</summary>
/// <param name="PinType">
/// KiCad's ELECTRICAL TYPE for the pin — the fact that makes a design checkable without a datasheet.
/// One of power_in, power_out, input, output, bidirectional, passive, open_collector, tri_state,
/// no_connect, unspecified — and KiCad may emit a COMPOUND value ("passive+no_connect") when the pin
/// also carries a no-connect flag, so always read it through <see cref="Types"/>.
/// </param>
public sealed record NetNode(string Ref, string Pin, string? PinFunction, string PinType)
{
    /// <summary>The pin's electrical types, splitting KiCad's compound "a+b" form.</summary>
    public IReadOnlyList<string> Types =>
        (PinType ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Is(string type) => Types.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when KiCad could not say what this pin is — nothing can be soundly concluded about it.</summary>
    public bool IsUnknown => PinType is null || Types.Count == 0 || Is("unspecified");

    public string Endpoint => $"{Ref}.{Pin}";
}

/// <summary>A computed net: KiCad has already flattened hierarchical sheets, buses and global labels.</summary>
public sealed record ImportedNet(int Code, string Name, IReadOnlyList<NetNode> Nodes);

/// <summary>A placed component as KiCad reports it (footprint present once assigned).</summary>
public sealed record ImportedComponent(string Ref, string Value, string? Footprint);

/// <summary>A whole schematic, imported.</summary>
public sealed record ImportedDesign(IReadOnlyList<ImportedComponent> Components, IReadOnlyList<ImportedNet> Nets);

/// <summary>
/// Reads a KiCad s-expression netlist (<c>kicad-cli sch export netlist --format kicadsexpr</c>) into a
/// typed net graph — the INVERSE of <see cref="KiCadNetlist"/>, which writes Foundry's own designs out.
///
/// <para>
/// This is what lets Foundry reason about a board it did NOT generate. KiCad has already done the hard
/// part: hierarchical sheets, buses and global labels are flattened, and every node carries the pin's
/// electrical type. That turns "is this net driven?" into arithmetic over data rather than an inference
/// about a picture — no schematic geometry parsing, no wire/junction tracing.
/// </para>
///
/// Pure: give it netlist text, get a design back. Never throws on malformed input — an unparseable
/// document yields an empty design, which the caller reports as unproven rather than as a pass.
/// </summary>
public static class KiCadNetlistReader
{
    public static ImportedDesign Parse(string netlistText)
    {
        var root = SExpr.Parse(netlistText);
        if (root is null) return new ImportedDesign(Array.Empty<ImportedComponent>(), Array.Empty<ImportedNet>());

        var components = new List<ImportedComponent>();
        foreach (var comp in root.Find("components").SelectMany(c => c.Children("comp")))
        {
            var r = comp.Value("ref");
            if (string.IsNullOrEmpty(r)) continue;
            components.Add(new ImportedComponent(r, comp.Value("value") ?? "", comp.Value("footprint")));
        }

        var nets = new List<ImportedNet>();
        foreach (var net in root.Find("nets").SelectMany(n => n.Children("net")))
        {
            var nodes = new List<NetNode>();
            foreach (var node in net.Children("node"))
            {
                var r = node.Value("ref");
                var pin = node.Value("pin");
                if (string.IsNullOrEmpty(r) || string.IsNullOrEmpty(pin)) continue;
                nodes.Add(new NetNode(r, pin, node.Value("pinfunction"), node.Value("pintype") ?? "unspecified"));
            }
            int.TryParse(net.Value("code"), out var code);
            nets.Add(new ImportedNet(code, net.Value("name") ?? "", nodes));
        }

        return new ImportedDesign(components, nets);
    }

    // ---- a minimal s-expression reader (quoted atoms with \" escapes; comments are not emitted by KiCad) ----
    internal sealed class SExpr
    {
        public string Head = "";
        public List<string> Atoms = new();
        public List<SExpr> Nodes = new();

        /// <summary>Direct children whose head is <paramref name="head"/>.</summary>
        public IEnumerable<SExpr> Children(string head) =>
            Nodes.Where(n => n.Head.Equals(head, StringComparison.OrdinalIgnoreCase));

        /// <summary>The first atom of the first child named <paramref name="head"/> — i.e. (head "value").</summary>
        public string? Value(string head) => Children(head).FirstOrDefault()?.Atoms.FirstOrDefault();

        /// <summary>Descendant lists with this head, at any depth.</summary>
        public IEnumerable<SExpr> Find(string head)
        {
            foreach (var n in Nodes)
            {
                if (n.Head.Equals(head, StringComparison.OrdinalIgnoreCase)) yield return n;
                foreach (var d in n.Find(head)) yield return d;
            }
        }

        public static SExpr? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int i = 0;
            try
            {
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != '(') return null;
                var root = new SExpr { Head = "<root>" };
                root.Nodes.Add(ParseList(text, ref i));
                return root;
            }
            catch { return null; }   // malformed → caller treats the design as unproven, never as clean
        }

        private static SExpr ParseList(string s, ref int i)
        {
            i++;                       // consume '('
            var node = new SExpr();
            SkipWs(s, ref i);
            node.Head = ReadAtom(s, ref i);
            while (true)
            {
                SkipWs(s, ref i);
                // Running out of input before the closing paren means the document is TRUNCATED. Returning
                // the partial tree would hand the caller a net with no nodes — indistinguishable from a real
                // empty net, and therefore a silent pass on a design nobody actually read. Fail instead.
                if (i >= s.Length) throw new FormatException("unterminated s-expression list");
                if (s[i] == ')') { i++; break; }
                if (s[i] == '(') node.Nodes.Add(ParseList(s, ref i));
                else node.Atoms.Add(ReadAtom(s, ref i));
            }
            return node;
        }

        private static string ReadAtom(string s, ref int i)
        {
            if (i < s.Length && s[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;   // \" and \\ inside quoted atoms
                    sb.Append(s[i++]);
                }
                i++;                   // closing quote
                return sb.ToString();
            }
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '(' && s[i] != ')') i++;
            return s[start..i];
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
