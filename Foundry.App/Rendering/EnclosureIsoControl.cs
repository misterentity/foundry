using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Foundry.App.Rendering;

/// <summary>
/// Isometric "3D" enclosure preview — faithful port of enclosure-svg.jsx. Placeholder until
/// the Phase 4 build123d sidecar + HelixToolkit mesh viewer land. Fixed 720×460 canvas.
/// </summary>
public sealed class EnclosureIsoControl : FrameworkElement
{
    private const double W = 720, H = 460;
    private const double CX = 360, CY = 230;
    private const double ax = 1, ay = 0.55, bx = -1, by = 0.55, cz = -1, SCALE = 4;
    private const double L = 66, Wd = 52, Hd = 30;

    private static readonly Color CAccent = Color.FromRgb(0xFF, 0x5A, 0x1F);
    private static readonly Color CInfo   = Color.FromRgb(0x5D, 0xD2, 0xFF);
    private static readonly Color CWarn   = Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly Color COk     = Color.FromRgb(0x4A, 0xDE, 0x80);
    private static readonly Color CLine   = Color.FromRgb(0x3A, 0x3A, 0x46);
    private static readonly Color CInk    = Color.FromRgb(0xED, 0xED, 0xEE);
    private static readonly Color CInkSoft= Color.FromRgb(0xB6, 0xB6, 0xBB);
    private static readonly Color CInkMute= Color.FromRgb(0x6A, 0x6A, 0x72);
    private static readonly Color CBand   = Color.FromRgb(0x0C, 0x0C, 0x10);

    private static FontFamily Mono =>
        (FontFamily)(Application.Current?.TryFindResource("Font.Mono") ?? new FontFamily("Consolas"));

    protected override Size MeasureOverride(Size availableSize) => new(W, H);

    private static Point P(double x, double y, double z) =>
        new(CX + (x * ax + y * bx) * SCALE, CY + (x * ay + y * by + z * cz) * SCALE);

    protected override void OnRender(DrawingContext dc)
    {
        // verts
        Point A = P(0,0,0), B = P(L,0,0), C = P(L,Wd,0), D = P(0,Wd,0);
        Point E = P(0,0,Hd), F = P(L,0,Hd), G = P(L,Wd,Hd), Hh = P(0,Wd,Hd);

        // iso grid
        var gp = new Pen(new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), 1);
        for (double x = 0; x <= W; x += 32) dc.DrawLine(gp, new Point(x, 0), new Point(x, H));
        for (double y = 0; y <= H; y += 18) dc.DrawLine(gp, new Point(0, y), new Point(W, y));

        // length dimension line
        var dimPen = new Pen(new SolidColorBrush(CInfo), 0.8);
        dc.DrawLine(dimPen, new Point(D.X, D.Y + 30), new Point(C.X, C.Y + 30));
        dc.DrawLine(dimPen, new Point(D.X, D.Y + 24), new Point(D.X, D.Y + 36));
        dc.DrawLine(dimPen, new Point(C.X, C.Y + 24), new Point(C.X, C.Y + 36));
        TextCenter(dc, "L · 66.00 mm", (D.X + C.X) / 2, D.Y + 50, 11, CInfo);

        // floor shadow
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), null, new Point(CX, CY + 100), 180, 22);

        // faces
        var stroke = new Pen(new SolidColorBrush(CLine), 1);
        dc.DrawGeometry(VFill(B, C, G, F, Color.FromRgb(0x1D,0x1D,0x24), Color.FromRgb(0x06,0x06,0x0A), true), stroke, Poly(B, C, G, F));
        dc.DrawGeometry(VFill(E, F, G, Hh, Color.FromRgb(0x3A,0x3A,0x46), Color.FromRgb(0x1D,0x1D,0x24), false), stroke, Poly(E, F, G, Hh));
        dc.DrawGeometry(VFill(A, B, F, E, Color.FromRgb(0x26,0x26,0x2E), Color.FromRgb(0x0E,0x0E,0x12), false), stroke, Poly(A, B, F, E));

        // lid seam (dashed orange)
        var seam = new Pen(new SolidColorBrush(CAccent), 0.8) { DashStyle = new DashStyle(new double[]{2,3}, 0) };
        dc.DrawLine(seam, new Point(E.X, E.Y - 6), new Point(F.X, F.Y - 6));
        dc.DrawLine(seam, new Point(F.X, F.Y - 6), new Point(G.X, G.Y - 6));
        Text(dc, "SNAP LID · −2mm", F.X + 18, F.Y - 8, 9, CAccent);

        // USB-C cutout (front)
        var c1 = P(12,0,18); var c2 = P(12+9.5,0,18); var c3 = P(12+9.5,0,18+6.5); var c4 = P(12,0,18+6.5);
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x04,0x04,0x0A)), new Pen(new SolidColorBrush(CInfo),1.2), Poly(c1,c2,c3,c4));
        double midY = (c1.Y + c4.Y)/2;
        dc.DrawLine(new Pen(new SolidColorBrush(CInfo),0.6), new Point(c2.X+8, midY), new Point(c2.X+60, midY+30));
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CInfo),0.8), new Rect(c2.X+60, midY+18, 74, 28));
        Text(dc, "USB-C", c2.X+68, midY+30, 9, CInfo);
        Text(dc, "9.5 × 6.5", c2.X+68, midY+41, 8, CInkSoft);

        // M12 gland (front circle)
        var gc = P(50,0,13); var ge = P(56,0,13); double ru = System.Math.Abs(ge.X - gc.X);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x04,0x04,0x0A)), new Pen(new SolidColorBrush(CAccent),1.2), gc, ru, ru*0.6);
        dc.DrawLine(new Pen(new SolidColorBrush(CAccent),0.6), new Point(gc.X, gc.Y+12), new Point(gc.X-80, gc.Y+80));
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CAccent),0.8), new Rect(gc.X-168, gc.Y+70, 90, 28));
        Text(dc, "M12 GLAND", gc.X-160, gc.Y+82, 9, CAccent);
        Text(dc, "⌀ 12.00 · IP65", gc.X-160, gc.Y+93, 8, CInkSoft);

        // reset (top circle)
        var rc = P(40,10,Hd);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x04,0x04,0x0A)), new Pen(new SolidColorBrush(CWarn),1.2), rc, 6, 3.5);
        dc.DrawLine(new Pen(new SolidColorBrush(CWarn),0.6), new Point(rc.X+6, rc.Y-1), new Point(rc.X+70, rc.Y-50));
        dc.DrawRectangle(new SolidColorBrush(CBand), new Pen(new SolidColorBrush(CWarn),0.8), new Rect(rc.X+70, rc.Y-70, 74, 28));
        Text(dc, "RESET", rc.X+78, rc.Y-58, 9, CWarn);
        Text(dc, "⌀ 6.00", rc.X+78, rc.Y-47, 8, CInkSoft);

        // standoffs
        foreach (var (sx, sy) in new[]{(8.0,8.0),(L-8,8.0),(8.0,Wd-8),(L-8,Wd-8)})
        {
            var c = P(sx, sy, Hd);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2A,0x2A,0x36)), new Pen(new SolidColorBrush(CLine),0.8), c, 4, 2.4);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x04,0x04,0x0A)), null, c, 1.4, 0.9);
        }

        // axes
        DrawAxes(dc);

        // HUD
        Text(dc, "VIEWPORT · ISO NE · ORTHOGRAPHIC", 20, 24, 9, CInkMute);
        Text(dc, "build123d · OpenCASCADE 7.7.2", 20, 38, 9, CInkMute);
        Text(dc, "/sidecar/enclosure.stl", 20, 52, 9, CInk);
        TextRight(dc, "● LIVE PREVIEW", 700, 24, 9, CAccent);
        TextRight(dc, "238k tris · 1.42 MB", 700, 38, 9, CInkMute);
        TextRight(dc, "17 ms regen", 700, 52, 9, CInkMute);
    }

    private void DrawAxes(DrawingContext dc)
    {
        double ox = 70, oy = 380;
        dc.DrawLine(new Pen(new SolidColorBrush(CAccent),1.2), new Point(ox,oy), new Point(ox+40,oy+22));
        Text(dc, "X", ox+46, oy+26, 10, CAccent);
        dc.DrawLine(new Pen(new SolidColorBrush(COk),1.2), new Point(ox,oy), new Point(ox-40,oy+22));
        Text(dc, "Y", ox-50, oy+26, 10, COk);
        dc.DrawLine(new Pen(new SolidColorBrush(CInfo),1.2), new Point(ox,oy), new Point(ox,oy-40));
        Text(dc, "Z", ox-4, oy-44, 10, CInfo);
    }

    private static StreamGeometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }

    private static Brush VFill(Point a, Point b, Point c, Point d, Color top, Color bottom, bool horizontal)
    {
        var br = new LinearGradientBrush(top, bottom, horizontal ? 0 : 90);
        br.Freeze();
        return br;
    }

    private static void Text(DrawingContext dc, string s, double x, double baseline, double size, Color color)
    {
        var ft = Fmt(s, size, color);
        dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
    }
    private static void TextRight(DrawingContext dc, string s, double x, double baseline, double size, Color color)
    {
        var ft = Fmt(s, size, color);
        dc.DrawText(ft, new Point(x - ft.Width, baseline - ft.Baseline));
    }
    private static void TextCenter(DrawingContext dc, string s, double x, double baseline, double size, Color color)
    {
        var ft = Fmt(s, size, color);
        dc.DrawText(ft, new Point(x - ft.Width / 2, baseline - ft.Baseline));
    }
    private static FormattedText Fmt(string s, double size, Color color) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.25);
}
