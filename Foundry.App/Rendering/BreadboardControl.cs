using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FProject = Foundry.Core.Project.Project;

namespace Foundry.App.Rendering;

/// <summary>
/// Breadboard-style view of the netlist (PRD v2 G5): a classic solderless breadboard with power rails,
/// a hole grid, the project's components placed as chips, and colored jumper wires for every connection
/// (power=red, ground=blue, i2c=purple, signal=amber). Illustrative — derived from the netlist, not
/// hole-exact. Renders to PNG/SVG like the schematic.
/// </summary>
public sealed class BreadboardControl : FrameworkElement
{
    public static readonly DependencyProperty ProjectProperty =
        DependencyProperty.Register(nameof(Project), typeof(FProject), typeof(BreadboardControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public FProject? Project { get => (FProject?)GetValue(ProjectProperty); set => SetValue(ProjectProperty, value); }

    // palette
    private static readonly Color CBg = Color.FromRgb(0x07, 0x07, 0x0A);
    private static readonly Color CBoard = Color.FromRgb(0xE8, 0xE2, 0xD0);   // breadboard tan
    private static readonly Color CBoardEdge = Color.FromRgb(0xC9, 0xC2, 0xAE);
    private static readonly Color CChannel = Color.FromRgb(0xD5, 0xCE, 0xBB);
    private static readonly Color CHole = Color.FromRgb(0xB7, 0xAF, 0x99);
    private static readonly Color CRailRed = Color.FromRgb(0xD8, 0x3A, 0x3A);
    private static readonly Color CRailBlue = Color.FromRgb(0x2E, 0x6F, 0xD8);
    private static readonly Color CChip = Color.FromRgb(0x1A, 0x1A, 0x20);
    private static readonly Color CChipEdge = Color.FromRgb(0x3A, 0x3A, 0x46);
    private static readonly Color CInk = Color.FromRgb(0xED, 0xED, 0xEE);
    private static readonly Color CInkMute = Color.FromRgb(0x6A, 0x6A, 0x72);
    private static readonly Color CPower = Color.FromRgb(0xFF, 0x40, 0x40);
    private static readonly Color CGround = Color.FromRgb(0x4F, 0x86, 0xFF);
    private static readonly Color CSignal = Color.FromRgb(0xF2, 0xB1, 0x3C);
    private static readonly Color CI2c = Color.FromRgb(0xC0, 0x84, 0xFC);

    private static FontFamily Mono => (FontFamily)(Application.Current?.TryFindResource("Font.Mono") ?? new FontFamily("Consolas"));
    private static FontFamily Serif => (FontFamily)(Application.Current?.TryFindResource("Font.Serif") ?? new FontFamily("Georgia"));

    private sealed class Chip { public required string Title; public double X, Y, W, H; public readonly List<(string pin, double x, double y, string net)> Pins = new(); }

    private const double Margin = 50, ChipW = 150, ChipGap = 56, ChipH = 96, BoardTop = 96;
    private double _w = 1000, _h = 520;
    private readonly List<Chip> _chips = new();
    private readonly List<(Point a, Point b, Color c)> _jumpers = new();
    private FProject? _built;

    protected override Size MeasureOverride(Size available) { Build(); return new Size(_w, _h); }

    private void Build()
    {
        if (ReferenceEquals(_built, Project) && _chips.Count > 0) return;
        _built = Project; _chips.Clear(); _jumpers.Clear();
        if (Project is null) { _w = 1000; _h = 520; return; }

        // components that appear in the netlist (preserve order of first appearance)
        var aliases = new List<string>();
        foreach (var c in Project.Connections)
            foreach (var ep in new[] { c.From, c.To })
            {
                var a = Alias(ep);
                if (a.Length > 0 && !aliases.Contains(a, StringComparer.OrdinalIgnoreCase)) aliases.Add(a);
            }
        if (aliases.Count == 0) { _w = 1000; _h = 520; return; }

        int n = aliases.Count;
        _w = Math.Max(960, Margin * 2 + n * ChipW + (n - 1) * ChipGap);
        _h = 560;
        double chipY = BoardTop + 150;

        // place chips left→right; pins along the bottom edge
        var pinAnchor = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            var alias = aliases[i];
            var spec = Project.Components.FirstOrDefault(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
            var pins = Project.Connections
                .SelectMany(c => new[] { (ep: c.From, c.Net), (ep: c.To, c.Net) })
                .Where(t => Alias(t.ep).Equals(alias, StringComparison.OrdinalIgnoreCase))
                .Select(t => (pin: Pin(t.ep), t.Net))
                .Where(t => t.pin.Length > 0)
                .GroupBy(t => t.pin, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .ToList();

            var chip = new Chip { Title = spec?.Name ?? alias, X = Margin + i * (ChipW + ChipGap), Y = chipY, W = ChipW, H = ChipH };
            int pc = Math.Max(1, pins.Count);
            for (int p = 0; p < pins.Count; p++)
            {
                double px = chip.X + 14 + (chip.W - 28) * (pins.Count == 1 ? 0.5 : (double)p / (pins.Count - 1));
                double py = chip.Y + chip.H;
                chip.Pins.Add((pins[p].pin, px, py, pins[p].Net));
                pinAnchor[$"{alias}.{pins[p].pin}"] = new Point(px, py + 20);   // a hole just below the pin
            }
            _chips.Add(chip);
        }

        foreach (var c in Project.Connections)
        {
            if (!pinAnchor.TryGetValue(c.From, out var a) || !pinAnchor.TryGetValue(c.To, out var b)) continue;
            _jumpers.Add((a, b, NetColor(c.Net)));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        Build();
        dc.DrawRectangle(new SolidColorBrush(CBg), null, new Rect(0, 0, _w, _h));
        if (Project is null || _chips.Count == 0)
        { TextCenter(dc, "no netlist", _w / 2, _h / 2, 14, CInkMute, Mono); return; }

        // breadboard body
        double bx = 24, bw = _w - 48, by = BoardTop, bh = _h - by - 24;
        dc.DrawRoundedRectangle(new SolidColorBrush(CBoard), new Pen(new SolidColorBrush(CBoardEdge), 1.5), new Rect(bx, by, bw, bh), 8, 8);

        // power rails (top + / bottom -)
        DrawRail(dc, bx + 16, by + 16, bw - 32, CRailRed, "+");
        DrawRail(dc, bx + 16, by + bh - 30, bw - 32, CRailBlue, "−");

        // hole grid (two banks split by the center channel)
        double gridTop = by + 52, gridBot = by + bh - 52, midY = (gridTop + gridBot) / 2;
        dc.DrawRectangle(new SolidColorBrush(CChannel), null, new Rect(bx + 12, midY - 9, bw - 24, 18));   // center channel
        for (double hx = bx + 26; hx < bx + bw - 18; hx += 22)
            for (double hy = gridTop; hy <= gridBot; hy += 20)
            {
                if (Math.Abs(hy - midY) < 14) continue;
                dc.DrawEllipse(new SolidColorBrush(CHole), null, new Point(hx, hy), 2.4, 2.4);
            }

        // jumper wires (under the chips) — colored by net, gentle bezier
        foreach (var (a, b, c) in _jumpers)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(a, false, false);
                double lift = 26 + Math.Min(70, Math.Abs(a.X - b.X) * 0.18);
                g.BezierTo(new Point(a.X, a.Y + lift), new Point(b.X, b.Y + lift), b, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)), 5) { LineJoin = PenLineJoin.Round }, geo);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
            dc.DrawEllipse(new SolidColorBrush(c), null, a, 3, 3);
            dc.DrawEllipse(new SolidColorBrush(c), null, b, 3, 3);
        }

        // chips
        foreach (var chip in _chips) DrawChip(dc, chip);

        // title
        Text(dc, "FOUNDRY · BREADBOARD", bx + 4, by - 30, 9, CInkMute, Mono);
        Text(dc, Project!.Title, bx + 4, by - 10, 15, CInk, Serif);
    }

    private void DrawChip(DrawingContext dc, Chip chip)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, new Rect(chip.X + 3, chip.Y + 3, chip.W, chip.H), 6, 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(CChip), new Pen(new SolidColorBrush(CChipEdge), 1), new Rect(chip.X, chip.Y, chip.W, chip.H), 6, 6);
        Text(dc, Truncate(chip.Title, 20), chip.X + 12, chip.Y + 22, 11, CInk, Mono);
        // pin legs + labels
        foreach (var (pin, x, y, net) in chip.Pins)
        {
            var c = NetColor(net);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xC9)), 2), new Point(x, y), new Point(x, y + 20));
            dc.DrawEllipse(new SolidColorBrush(c), null, new Point(x, y + 20), 3, 3);
            var ft = Format(pin, 7.5, CInkMute, Mono);
            dc.PushTransform(new RotateTransform(-90, x, y - 4));
            dc.DrawText(ft, new Point(x - ft.Width, y - 4 - ft.Baseline));
            dc.Pop();
        }
    }

    private void DrawRail(DrawingContext dc, double x, double y, double w, Color c, string sign)
    {
        dc.DrawLine(new Pen(new SolidColorBrush(c), 2), new Point(x + 18, y + 7), new Point(x + w - 6, y + 7));
        Text(dc, sign, x, y + 12, 12, c, Mono);
        for (double hx = x + 30; hx < x + w - 6; hx += 22) dc.DrawEllipse(new SolidColorBrush(CHole), null, new Point(hx, y + 7), 2.2, 2.2);
    }

    private static Color NetColor(string net) => net switch
    { "power" => CPower, "ground" => CGround, "i2c" => CI2c, _ => CSignal };

    private static string Alias(string ep) { var d = ep.IndexOf('.'); return (d < 0 ? ep : ep[..d]).Trim(); }
    private static string Pin(string ep) { var d = ep.IndexOf('.'); return d < 0 ? "" : ep[(d + 1)..].Trim(); }
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static void Text(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    { var ft = Format(s, size, color, fam); dc.DrawText(ft, new Point(x, baseline - ft.Baseline)); }
    private static void TextCenter(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    { var ft = Format(s, size, color, fam); dc.DrawText(ft, new Point(x - ft.Width / 2, baseline - ft.Baseline)); }
    private static FormattedText Format(string s, double size, Color color, FontFamily fam) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, new SolidColorBrush(color), 1.25);
}
