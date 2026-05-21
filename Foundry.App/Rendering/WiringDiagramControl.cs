using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Foundry.Core.Kb;
using FProject = Foundry.Core.Project.Project;

namespace Foundry.App.Rendering;

/// <summary>
/// Generic netlist diagram. Auto-lays out the project's components into three columns
/// (power sources left · controller center · peripherals right), draws each as a pin-labelled
/// block, and routes every connection as a colored orthogonal net. Nothing is hard-coded — the
/// layout is derived from <see cref="FProject.Connections"/> and <see cref="FProject.Components"/>.
/// </summary>
public sealed class WiringDiagramControl : FrameworkElement
{
    public static readonly DependencyProperty ProjectProperty =
        DependencyProperty.Register(nameof(Project), typeof(FProject), typeof(WiringDiagramControl),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public FProject? Project
    {
        get => (FProject?)GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    // palette
    private static readonly Color CPower  = Color.FromRgb(0xFF, 0x40, 0x40);
    private static readonly Color CGround = Color.FromRgb(0x88, 0x88, 0x88);
    private static readonly Color CSignal = Color.FromRgb(0x5D, 0xD2, 0xFF);
    private static readonly Color CI2c    = Color.FromRgb(0xC0, 0x84, 0xFC);
    private static readonly Color CAccent = Color.FromRgb(0xFF, 0x5A, 0x1F);
    private static readonly Color CBody   = Color.FromRgb(0x16, 0x16, 0x1C);
    private static readonly Color CBand   = Color.FromRgb(0x0C, 0x0C, 0x10);
    private static readonly Color CLine   = Color.FromRgb(0x3A, 0x3A, 0x46);
    private static readonly Color CInk    = Color.FromRgb(0xED, 0xED, 0xEE);
    private static readonly Color CInkSoft= Color.FromRgb(0xB6, 0xB6, 0xBB);
    private static readonly Color CInkMute= Color.FromRgb(0x6A, 0x6A, 0x72);
    private static readonly Color CBg     = Color.FromRgb(0x07, 0x07, 0x0A);

    // layout constants (px)
    private const double ColW = 196, Gap = 150, MarginX = 64, MarginY = 76;
    private const double HeaderH = 26, FooterH = 18, PinPitch = 28, PinPad = 16, Stub = 16;

    private static FontFamily Mono =>
        (FontFamily)(Application.Current?.TryFindResource("Font.Mono") ?? new FontFamily("Consolas"));
    private static FontFamily Serif =>
        (FontFamily)(Application.Current?.TryFindResource("Font.Serif") ?? new FontFamily("Georgia"));

    private sealed class LPin { public required string Comp, Pin, Net; public double X, Y; public bool Right; }
    private sealed class LComp
    {
        public required string Alias, Title, Sub; public string Kind = "peripheral";
        public double X, Y, W, H;
        public readonly List<LPin> Left = new(); public readonly List<LPin> Right = new();
    }

    private FProject? _built;
    private readonly List<LComp> _comps = new();
    private readonly List<(string d, Color c)> _nets = new();
    private readonly Dictionary<string, LPin> _anchors = new();
    private double _w = 900, _h = 480;
    private int _netCount, _compCount;

    private static Color NetColor(string net) => net switch
    {
        "power" => CPower, "ground" => CGround, "i2c" => CI2c, _ => CSignal
    };

    private static string NetOfKind(PinKind k) => k switch
    {
        PinKind.Power => "power", PinKind.Ground => "ground", _ => "signal"
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        Build();
        return new Size(_w, _h);
    }

    // ----- layout -----
    private void Build()
    {
        if (ReferenceEquals(_built, Project) && _comps.Count > 0) return;
        _built = Project;
        _comps.Clear(); _nets.Clear(); _anchors.Clear();

        var p = Project;
        if (p is null) { _w = 900; _h = 480; return; }

        // 1. collect components keyed by alias, with the set of pins actually used + their net.
        var byAlias = new Dictionary<string, LComp>(StringComparer.OrdinalIgnoreCase);
        var pinNet = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase); // alias -> pin -> net
        var declared = new Dictionary<string, ComponentSpec>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in p.Components) declared[c.Alias] = c;

        void Touch(string alias) { if (!pinNet.ContainsKey(alias)) pinNet[alias] = new(StringComparer.OrdinalIgnoreCase); }

        // pins referenced by the netlist (authoritative)
        foreach (var conn in p.Connections)
        {
            foreach (var (ep, _) in new[] { (conn.From, 0), (conn.To, 1) })
            {
                var (a, pin) = SplitEndpoint(ep);
                if (a.Length == 0) continue;
                Touch(a);
                // a connection net wins; keep the strongest (power/ground over signal) if multiple
                if (!pinNet[a].TryGetValue(pin, out var existing) || Rank(conn.Net) > Rank(existing))
                    pinNet[a][pin] = conn.Net;
            }
        }
        // components with no connections still get a block (show a few declared pins)
        foreach (var c in p.Components)
        {
            Touch(c.Alias);
            if (pinNet[c.Alias].Count == 0)
                foreach (var pin in c.Pins.Take(6)) pinNet[c.Alias][pin.Name] = NetOfKind(pin.Kind);
        }

        foreach (var alias in pinNet.Keys)
        {
            declared.TryGetValue(alias, out var spec);
            var title = spec?.Name is { Length: > 0 } n ? $"{alias} · {n}" : alias;
            var comp = new LComp { Alias = alias, Title = title, Sub = spec?.Ref ?? "" };
            byAlias[alias] = comp;
        }

        // 2. classify role
        LComp? mcu = null; int bestSignal = -1;
        foreach (var comp in byAlias.Values)
        {
            var pins = pinNet[comp.Alias];
            bool allPower = pins.Count > 0 && pins.Values.All(v => v is "power" or "ground");
            int sigCount = pins.Values.Count(v => v is "signal" or "i2c");
            if (IsPowerName(comp.Alias) || (allPower && !IsMcuName(comp.Alias))) comp.Kind = "power";
            else comp.Kind = "peripheral";
            // best MCU candidate = explicit name, else most signal pins
            int score = (IsMcuName(comp.Alias) ? 1000 : 0) + sigCount;
            if (score > bestSignal) { bestSignal = score; mcu = comp; }
        }
        if (mcu is not null && !IsPowerName(mcu.Alias)) mcu.Kind = "mcu";

        // 3. assign pins to sides
        foreach (var comp in byAlias.Values)
        {
            foreach (var kv in pinNet[comp.Alias].OrderBy(k => SideOrder(comp.Kind, k.Value)).ThenBy(k => k.Key))
            {
                bool right = comp.Kind switch
                {
                    "power" => true,                                   // power: pins face center (right)
                    "peripheral" => false,                             // peripheral: pins face center (left)
                    _ => kv.Value is not ("power" or "ground"),        // mcu: power/ground left, signals right
                };
                var lp = new LPin { Comp = comp.Alias, Pin = kv.Key, Net = kv.Value, Right = right };
                (right ? comp.Right : comp.Left).Add(lp);
            }
        }

        // 4. size blocks
        foreach (var comp in byAlias.Values)
        {
            comp.W = ColW;
            int rows = Math.Max(comp.Left.Count, comp.Right.Count);
            comp.H = HeaderH + PinPad * 2 + Math.Max(1, rows) * PinPitch + FooterH;
        }

        // 5. columns
        var colLeft = byAlias.Values.Where(c => c.Kind == "power").ToList();
        var colCenter = byAlias.Values.Where(c => c.Kind == "mcu").ToList();
        var colRight = byAlias.Values.Where(c => c.Kind == "peripheral").ToList();
        if (colCenter.Count == 0 && colRight.Count > 0) { colCenter.Add(colRight[0]); colRight.RemoveAt(0); } // ensure a center

        var columns = new List<List<LComp>> { colLeft, colCenter, colRight };
        double x = MarginX;
        double maxColH = 0;
        foreach (var col in columns)
        {
            if (col.Count == 0) continue;
            double y = MarginY;
            foreach (var comp in col)
            {
                comp.X = x; comp.Y = y;
                LayoutPins(comp);
                y += comp.H + 44;
            }
            maxColH = Math.Max(maxColH, y - 44);
            x += ColW + Gap;
        }
        _w = Math.Max(900, x - Gap + MarginX);
        _h = Math.Max(360, maxColH + MarginY + 96); // room for title block

        _comps.AddRange(byAlias.Values);
        foreach (var comp in _comps)
            foreach (var lp in comp.Left.Concat(comp.Right))
                _anchors[$"{comp.Alias}.{lp.Pin}"] = lp;

        // 6. route nets
        _compCount = _comps.Count;
        _netCount = p.Connections.Count;
        int idx = 0;
        foreach (var conn in p.Connections)
        {
            var (fa, fp) = SplitEndpoint(conn.From);
            var (ta, tp) = SplitEndpoint(conn.To);
            if (!_anchors.TryGetValue($"{fa}.{fp}", out var a1) || !_anchors.TryGetValue($"{ta}.{tp}", out var a2)) { idx++; continue; }
            _nets.Add((RoutePath(a1, a2, idx++), NetColor(conn.Net)));
        }
    }

    private static void LayoutPins(LComp comp)
    {
        double top = comp.Y + HeaderH + PinPad + PinPitch / 2;
        for (int i = 0; i < comp.Left.Count; i++) { comp.Left[i].X = comp.X; comp.Left[i].Y = top + i * PinPitch; }
        for (int i = 0; i < comp.Right.Count; i++) { comp.Right[i].X = comp.X + comp.W; comp.Right[i].Y = top + i * PinPitch; }
    }

    private static string RoutePath(LPin a, LPin b, int idx)
    {
        double d1 = a.Right ? Stub : -Stub, d2 = b.Right ? Stub : -Stub;
        double ex1 = a.X + d1, ex2 = b.X + d2;
        double jitter = ((idx % 9) - 4) * 6.0;
        double cx = (ex1 + ex2) / 2 + jitter;
        return string.Format(CultureInfo.InvariantCulture,
            "M {0} {1} L {2} {1} L {3} {1} L {3} {4} L {5} {4} L {6} {4}",
            a.X, a.Y, ex1, cx, b.Y, ex2, b.X);
    }

    private static int Rank(string net) => net switch { "power" => 3, "ground" => 3, "i2c" => 2, _ => 1 };
    private static int SideOrder(string kind, string net) => kind == "mcu" ? (net is "power" or "ground" ? 0 : 1) : 0;

    private static (string alias, string pin) SplitEndpoint(string ep)
    {
        var dot = ep.IndexOf('.');
        return dot < 0 ? (ep.Trim(), "") : (ep[..dot].Trim(), ep[(dot + 1)..].Trim());
    }

    private static readonly string[] PowerWords = { "bat", "batt", "cell", "reg", "regul", "ldo", "charg", "chg", "boost", "buck", "pmic", "solar", "power", "vreg", "psu", "supply" };
    private static readonly string[] McuWords = { "mcu", "esp", "pico", "rp2040", "stm", "atmega", "avr", "nrf", "samd", "teensy", "controller", "soc", "cpu" };
    private static bool IsPowerName(string a) => PowerWords.Any(w => a.ToLowerInvariant().Contains(w));
    private static bool IsMcuName(string a) => McuWords.Any(w => a.ToLowerInvariant().Contains(w));

    // ----- render -----
    protected override void OnRender(DrawingContext dc)
    {
        Build();
        dc.DrawRectangle(new SolidColorBrush(CBg), null, new Rect(0, 0, _w, _h));

        var fine = new Pen(new SolidColorBrush(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF)), 1);
        for (double gx = 0; gx <= _w; gx += 20) dc.DrawLine(fine, new Point(gx, 0), new Point(gx, _h));
        for (double gy = 0; gy <= _h; gy += 20) dc.DrawLine(fine, new Point(0, gy), new Point(_w, gy));

        if (Project is null || _comps.Count == 0)
        {
            TextCenter(dc, "no netlist", _w / 2, _h / 2, 14, CInkMute, Mono);
            return;
        }

        // nets first (under blocks)
        foreach (var (d, c) in _nets)
        {
            var geo = Geometry.Parse(d);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x1F, c.R, c.G, c.B)), 6), geo);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 2) { LineJoin = PenLineJoin.Round }, geo);
        }

        foreach (var comp in _comps) DrawBlock(dc, comp);
        DrawTitleBlock(dc);
    }

    private void DrawBlock(DrawingContext dc, LComp comp)
    {
        double x = comp.X, y = comp.Y, w = comp.W, h = comp.H;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), null, new Rect(x + 3, y + 3, w, h));
        dc.DrawRectangle(new SolidColorBrush(CBody), new Pen(new SolidColorBrush(CLine), 1), new Rect(x, y, w, h));
        dc.DrawLine(new Pen(new SolidColorBrush(CAccent), 1.5), new Point(x, y + 8), new Point(x + 8, y));
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CLine), 1), new Rect(x, y, w, HeaderH));
        Text(dc, Truncate(comp.Title, 26), x + 10, y + 17, 10.5, CInk, Mono);
        var kindLabel = comp.Kind == "mcu" ? "CONTROLLER" : comp.Kind == "power" ? "POWER" : "PERIPHERAL";
        TextRight(dc, kindLabel, x + w - 10, y + h - 7, 8, CInkMute, Mono);
        if (comp.Sub.Length > 0) Text(dc, Truncate(comp.Sub, 24), x + 10, y + h - 7, 8.5, CInkMute, Mono);

        foreach (var lp in comp.Left)
        {
            var c = NetColor(lp.Net);
            dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(c), 1), new Rect(lp.X - 5, lp.Y - 3, 10, 6));
            Text(dc, lp.Pin, lp.X + 10, lp.Y + 3, 9, CInkSoft, Mono);
        }
        foreach (var lp in comp.Right)
        {
            var c = NetColor(lp.Net);
            dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(c), 1), new Rect(lp.X - 5, lp.Y - 3, 10, 6));
            TextRight(dc, lp.Pin, lp.X - 10, lp.Y + 3, 9, CInkSoft, Mono);
        }
    }

    private void DrawTitleBlock(DrawingContext dc)
    {
        double tw = 268, th = 70, tx = _w - tw - 16, ty = _h - th - 16;
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CLine), 1), new Rect(tx, ty, tw, th));
        dc.DrawLine(new Pen(new SolidColorBrush(CLine), 1), new Point(tx, ty + 18), new Point(tx + tw, ty + 18));
        Text(dc, "FOUNDRY · NETLIST", tx + 8, ty + 13, 9, CInk, Mono);
        Text(dc, Truncate(Project?.Title ?? "Project", 30), tx + 8, ty + 38, 14, CInk, Serif);
        Text(dc, $"{_netCount} nets · {_compCount} components · orthogonal", tx + 8, ty + 58, 8.5, CInkMute, Mono);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static void Text(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    { var ft = Format(s, size, color, fam); dc.DrawText(ft, new Point(x, baseline - ft.Baseline)); }
    private static void TextRight(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    { var ft = Format(s, size, color, fam); dc.DrawText(ft, new Point(x - ft.Width, baseline - ft.Baseline)); }
    private static void TextCenter(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    { var ft = Format(s, size, color, fam); dc.DrawText(ft, new Point(x - ft.Width / 2, baseline - ft.Baseline)); }
    private static FormattedText Format(string s, double size, Color color, FontFamily fam) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.25);
}
