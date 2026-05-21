using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Foundry.App.Rendering;

/// <summary>
/// Blueprint-style netlist diagram — a faithful port of wiring-svg.jsx. Drawn entirely in
/// code on a fixed 1100×600 canvas (wrap in a Viewbox to scale). Component blocks carry pin
/// headers; nets are colored orthogonal paths (power=red, ground=gray, signal=cyan).
/// </summary>
public sealed class WiringDiagramControl : FrameworkElement
{
    private const double W = 1100, H = 600;

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

    private static FontFamily Mono =>
        (FontFamily)(Application.Current?.TryFindResource("Font.Mono") ?? new FontFamily("Consolas"));
    private static FontFamily Serif =>
        (FontFamily)(Application.Current?.TryFindResource("Font.Serif") ?? new FontFamily("Georgia"));

    protected override Size MeasureOverride(Size availableSize) => new(W, H);

    private static Color NetColor(string net) => net switch
    {
        "power" => CPower, "ground" => CGround, "i2c" => CI2c, _ => CSignal
    };

    private sealed record Pin(string Side, double At, string Label, string Net);
    private sealed record Net(string D, Color Color, double? Lx = null, double? Ly = null, string? Lt = null);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x07, 0x07, 0x0A)), null, new Rect(0, 0, W, H));

        // ---- grid ----
        var fine = new Pen(new SolidColorBrush(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF)), 1);
        for (double x = 0; x <= W; x += 20) dc.DrawLine(fine, new Point(x, 0), new Point(x, H));
        for (double y = 0; y <= H; y += 20) dc.DrawLine(fine, new Point(0, y), new Point(W, y));
        var coarse = new Pen(new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)), 1);
        for (double x = 0; x <= W; x += 100) dc.DrawLine(coarse, new Point(x, 0), new Point(x, H));
        for (double y = 0; y <= H; y += 100) dc.DrawLine(coarse, new Point(0, y), new Point(W, y));

        // ---- margin marks ----
        var markPen = new Pen(new SolidColorBrush(CLine), 1);
        foreach (var x in new[] { 0, 200, 400, 600, 800, 1000 })
        {
            dc.DrawLine(markPen, new Point(x, 0), new Point(x, 8));
            Text(dc, x.ToString(), x + 4, 14, 9, CLine, Mono);
        }
        foreach (var y in new[] { 0, 150, 300, 450, 600 })
        {
            dc.DrawLine(markPen, new Point(0, y), new Point(8, y));
            Text(dc, y.ToString(), 12, y + 8, 9, CLine, Mono);
        }

        // ---- title block ----
        DrawTitleBlock(dc);

        // ---- component blocks ----
        DrawBlock(dc, 60, 420, 180, 120, "BAT · 18650 Li-ion", "3.7V · 3000mAh", "HLD-18650-1S", CAccent, new[]
        {
            new Pin("R", 40, "BAT+", "power"), new Pin("R", 80, "BAT−", "ground"),
        });
        DrawBlock(dc, 60, 250, 180, 120, "CHG · TP4056", "USB-C 1A", "TP4056-USB-C", CAccent, new[]
        {
            new Pin("T", 40, "VBUS", "power"), new Pin("B", 90, "BAT+", "power"), new Pin("B", 140, "GND", "ground"),
        });
        DrawBlock(dc, 310, 350, 170, 110, "REG · MCP1700", "LDO · 3.3V", "TO-92", CAccent, new[]
        {
            new Pin("L", 30, "VIN", "power"), new Pin("L", 60, "GND", "ground"),
            new Pin("L", 90, "VOUT", "power"), new Pin("R", 55, "→3V3", "power"),
        });
        DrawBlock(dc, 540, 140, 310, 330, "MCU · ESP32 DevKit v1", "240MHz · WiFi+BLE", "ESP32-DEVKITC-32E · 30 pins", CAccent, new[]
        {
            new Pin("L", 50, "3V3", "power"), new Pin("L", 90, "GND", "ground"), new Pin("L", 130, "5V", "power"),
            new Pin("L", 200, "GPIO34", "signal"), new Pin("L", 240, "GPIO0", "signal"), new Pin("L", 280, "GPIO13", "signal"),
            new Pin("R", 70, "WIFI/ANT", "signal"), new Pin("R", 130, "TX0", "signal"), new Pin("R", 170, "RX0", "signal"),
            new Pin("R", 230, "EN", "signal"), new Pin("R", 280, "USB", "power"),
        });
        DrawBlock(dc, 920, 260, 150, 130, "SEN · CAP v1.2", "0–3V analog", "SEN-CAP-01", CAccent, new[]
        {
            new Pin("L", 40, "VCC", "power"), new Pin("L", 70, "GND", "ground"), new Pin("L", 100, "AOUT", "signal"),
        });
        DrawBlock(dc, 310, 100, 150, 80, "BTN1 · TACT", "6×6mm", "TL3301AF260QG", CAccent, new[]
        {
            new Pin("R", 30, "A", "signal"), new Pin("R", 55, "B", "ground"),
        });

        // ---- nets ----
        var nets = new[]
        {
            new Net("M 240 460 L 270 460 L 270 388 L 200 388 L 200 370", CPower, 260, 430, "BAT+"),
            new Net("M 240 500 L 285 500 L 285 392 L 250 392 L 250 370", CGround),
            new Net("M 200 370 L 200 405 L 310 405 L 310 380", CPower, 250, 396, "VBAT"),
            new Net("M 250 370 L 250 410 L 310 410 L 310 410", CGround),
            new Net("M 480 405 L 510 405 L 510 270 L 540 270", CPower, 510, 332, "3V3"),
            new Net("M 540 190 L 510 190 L 510 60 L 900 60 L 900 300 L 920 300", CPower, 712, 56, "3V3"),
            new Net("M 540 230 L 500 230 L 500 80 L 880 80 L 880 330 L 920 330", CGround),
            new Net("M 540 340 L 870 340 L 870 360 L 920 360", CSignal, 720, 336, "SIG · GPIO34"),
            new Net("M 540 380 L 480 380 L 480 130 L 460 130", CSignal, 490, 256, "GPIO0"),
            new Net("M 460 155 L 475 155 L 475 90 L 700 90 L 700 140", CGround),
            new Net("M 100 250 L 100 220 L 145 220", CPower, 70, 230, "USB-C IN"),
        };
        foreach (var n in nets) DrawNet(dc, n);

        // ---- USB-C call-out box ----
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CLine), 1), new Rect(30, 200, 60, 40));
        TextCenter(dc, "USB-C", 60, 215, 8, CInkMute, Mono);
        TextCenter(dc, "⟳ 5V", 60, 232, 11, CInk, Mono);

        // ---- antenna squiggle ----
        var antPen = new Pen(new SolidColorBrush(CSignal), 1.6);
        dc.DrawLine(new Pen(new SolidColorBrush(CSignal), 2), new Point(870, 200), new Point(890, 200));
        var ant = Geometry.Parse("M 890 200 q 4 -8 8 0 q 4 -8 8 0 q 4 -8 8 0");
        dc.DrawGeometry(null, antPen, ant);
        Text(dc, "WIFI", 928, 203, 9, CSignal, Mono);

        // ---- signal cluster annotation ----
        Text(dc, "SIGNAL CLUSTER · A", W - 260, 30, 9, CAccent, Mono);
        dc.DrawLine(new Pen(new SolidColorBrush(CAccent), 0.8), new Point(W - 260, 36), new Point(W - 20, 36));
        Text(dc, "3 nets · soil → MCU", W - 260, 52, 8.5, CInkSoft, Mono);
        Text(dc, "power · ground · signal", W - 260, 66, 8.5, CInkMute, Mono);
    }

    private void DrawBlock(DrawingContext dc, double x, double y, double w, double h,
        string label, string sub, string footprint, Color accent, Pin[] pins)
    {
        // shadow + body
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), null, new Rect(x + 3, y + 3, w, h));
        dc.DrawRectangle(new SolidColorBrush(CBody), new Pen(new SolidColorBrush(CLine), 1), new Rect(x, y, w, h));
        // corner cut
        dc.DrawLine(new Pen(new SolidColorBrush(accent), 1.5), new Point(x, y + 8), new Point(x + 8, y));
        // label band
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CLine), 1), new Rect(x, y, w, 22));
        Text(dc, label, x + 10, y + 15, 10, CInk, Mono);
        TextRight(dc, sub, x + w - 10, y + 15, 9, CInkMute, Mono);
        Text(dc, footprint, x + 10, y + h - 8, 8.5, CInkMute, Mono);

        foreach (var p in pins)
        {
            double cx = 0, cy = 0, tx = 0, ty = 0; var anchor = "start";
            switch (p.Side)
            {
                case "L": cx = x; cy = y + p.At; tx = x + 8; ty = y + p.At + 3; anchor = "start"; break;
                case "R": cx = x + w; cy = y + p.At; tx = x + w - 8; ty = y + p.At + 3; anchor = "end"; break;
                case "T": cx = x + p.At; cy = y; tx = x + p.At; ty = y + 14; anchor = "middle"; break;
                case "B": cx = x + p.At; cy = y + h; tx = x + p.At; ty = y + h - 8; anchor = "middle"; break;
            }
            var c = NetColor(p.Net);
            dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(c), 1), new Rect(cx - 5, cy - 3, 10, 6));
            switch (anchor)
            {
                case "end": TextRight(dc, p.Label, tx, ty, 9, CInkSoft, Mono); break;
                case "middle": TextCenter(dc, p.Label, tx, ty, 9, CInkSoft, Mono); break;
                default: Text(dc, p.Label, tx, ty, 9, CInkSoft, Mono); break;
            }
        }
    }

    private void DrawNet(DrawingContext dc, Net n)
    {
        var geo = Geometry.Parse(n.D);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x1F, n.Color.R, n.Color.G, n.Color.B)), 6), geo);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(n.Color), 2) { LineJoin = PenLineJoin.Miter }, geo);
        if (n.Lt != null && n.Lx is double lx && n.Ly is double ly)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x07, 0x07, 0x0A)), new Pen(new SolidColorBrush(n.Color), 0.8),
                new Rect(lx - 32, ly - 8, 64, 16));
            TextCenter(dc, n.Lt, lx, ly + 3, 8.5, n.Color, Mono);
        }
    }

    private void DrawTitleBlock(DrawingContext dc)
    {
        double tx = W - 260, ty = H - 90;
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CLine), 1), new Rect(tx, ty, 252, 82));
        var p = new Pen(new SolidColorBrush(CLine), 1);
        dc.DrawLine(p, new Point(tx, ty + 18), new Point(tx + 252, ty + 18));
        dc.DrawLine(p, new Point(tx, ty + 48), new Point(tx + 252, ty + 48));
        dc.DrawLine(p, new Point(tx + 170, ty + 18), new Point(tx + 170, ty + 82));
        Text(dc, "FOUNDRY · NETLIST", tx + 8, ty + 13, 9, CInk, Mono);
        Text(dc, "Cap. Soil Moisture Sentinel", tx + 8, ty + 32, 14, CInk, Serif);
        Text(dc, "rev 03 · 9 nets · 8 components", tx + 8, ty + 44, 8, CInkMute, Mono);
        Text(dc, "SHEET", tx + 8, ty + 63, 8, CInkMute, Mono);
        Text(dc, "01 / 01", tx + 8, ty + 76, 11, CAccent, Mono);
        Text(dc, "SCALE", tx + 178, ty + 63, 8, CInkMute, Mono);
        Text(dc, "1:1", tx + 178, ty + 76, 11, CInk, Mono);
    }

    // ---- text helpers (y is baseline, like SVG) ----
    private static void Text(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    {
        var ft = Format(s, size, color, fam);
        dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
    }
    private static void TextRight(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    {
        var ft = Format(s, size, color, fam);
        dc.DrawText(ft, new Point(x - ft.Width, baseline - ft.Baseline));
    }
    private static void TextCenter(DrawingContext dc, string s, double x, double baseline, double size, Color color, FontFamily fam)
    {
        var ft = Format(s, size, color, fam);
        dc.DrawText(ft, new Point(x - ft.Width / 2, baseline - ft.Baseline));
    }
    private static FormattedText Format(string s, double size, Color color, FontFamily fam) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.25);
}
