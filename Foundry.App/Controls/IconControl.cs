using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Foundry.App.Controls;

/// <summary>
/// Sharp 1.4px stroke icon on a 16×16 grid — port of shared.jsx's Icon set. Rect/circle
/// primitives are expressed as path data so a single <see cref="Geometry"/> renders each.
/// Usage: &lt;c:Icon Glyph="cart" IconSize="12" Stroke="{...}"/&gt;
/// </summary>
public sealed class IconControl : Shape
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(IconControl),
        new FrameworkPropertyMetadata("spark", FrameworkPropertyMetadataOptions.AffectsRender, OnGlyphChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(IconControl),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnSizeChanged));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public IconControl()
    {
        Stretch = Stretch.Uniform;
        StrokeThickness = 1.4;
        StrokeLineJoin = PenLineJoin.Miter;
        StrokeStartLineCap = PenLineCap.Square;
        StrokeEndLineCap = PenLineCap.Square;
        Fill = null;
        Width = 14; Height = 14;
        SetResourceReference(StrokeProperty, "Brush.InkSoft");
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((IconControl)d).InvalidateVisual();

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (IconControl)d;
        c.Width = c.IconSize;
        c.Height = c.IconSize;
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            var data = Map.TryGetValue(Glyph, out var d) ? d : Map["spark"];
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }
    }

    // path data on a 16×16 viewbox; circles/rects converted to arcs/closed paths.
    private static readonly Dictionary<string, string> Map = new()
    {
        ["spark"]   = "M8 1 L9.5 6.5 L15 8 L9.5 9.5 L8 15 L6.5 9.5 L1 8 L6.5 6.5 Z",
        ["chip"]    = "M3 3 H13 V13 H3 Z M3 6 H1 M3 10 H1 M15 6 H13 M15 10 H13 M6 3 V1 M10 3 V1 M6 15 V13 M10 15 V13 M6 6 H10 V10 H6 Z",
        ["cart"]    = "M1 2 H3 L5 11 H13 L15 5 H4 M5 14 A1 1 0 1 0 7 14 A1 1 0 1 0 5 14 M11 14 A1 1 0 1 0 13 14 A1 1 0 1 0 11 14",
        ["wire"]    = "M1.5 3 A1.5 1.5 0 1 0 4.5 3 A1.5 1.5 0 1 0 1.5 3 M11.5 13 A1.5 1.5 0 1 0 14.5 13 A1.5 1.5 0 1 0 11.5 13 M3 4.5 V8 H8 V13",
        ["cube"]    = "M8 1 L14 4.5 V11.5 L8 15 L2 11.5 V4.5 Z M2 4.5 L8 8 L14 4.5 M8 8 V15",
        ["code"]    = "M5 4 L1 8 L5 12 M11 4 L15 8 L11 12 M9 3 L7 13",
        ["shield"]  = "M8 1 L14 3 V8 C14 11 11 14 8 15 C5 14 2 11 2 8 V3 Z M5.5 8 L7.5 10 L11 6.5",
        ["book"]    = "M2 2 H7 C8 2 8 3 8 3 V14 C8 14 8 13 7 13 H2 Z M14 2 H9 C8 2 8 3 8 3 V14 C8 14 8 13 9 13 H14 Z",
        ["bolt"]    = "M9 1 L3 9 H8 L7 15 L13 7 H8 Z",
        ["play"]    = "M4 2 L13 8 L4 14 Z",
        ["plus"]    = "M8 2 V14 M2 8 H14",
        ["search"]  = "M2 7 A5 5 0 1 0 12 7 A5 5 0 1 0 2 7 M11 11 L15 15",
        ["minimize"]= "M3 8 H13",
        ["maximize"]= "M3 3 H13 V13 H3 Z",
        ["close"]   = "M3 3 L13 13 M13 3 L3 13",
        ["send"]    = "M1 8 L15 1 L11 15 L8 9 Z",
        ["download"]= "M8 1 V11 M3 7 L8 12 L13 7 M2 14 H14",
        ["refresh"] = "M14 4 V8 H10 M2 12 V8 H6 M3 6 A6 6 0 0 1 13 6 M13 10 A6 6 0 0 1 3 10",
        ["settings"]= "M6 8 A2 2 0 1 0 10 8 A2 2 0 1 0 6 8 M8 1 V3 M8 13 V15 M1 8 H3 M13 8 H15 M3 3 L4.5 4.5 M11.5 11.5 L13 13 M3 13 L4.5 11.5 M11.5 4.5 L13 3",
        ["grid"]    = "M2 2 H7 V7 H2 Z M9 2 H14 V7 H9 Z M2 9 H7 V14 H2 Z M9 9 H14 V14 H9 Z",
        ["chev"]    = "M5 3 L11 8 L5 13",
        ["chevD"]   = "M3 5 L8 11 L13 5",
        ["folder"]  = "M1 4 H6 L8 6 H15 V13 H1 Z",
        ["eye"]     = "M1 8 C3 4 5 3 8 3 C11 3 13 4 15 8 C13 12 11 13 8 13 C5 13 3 12 1 8 Z M6 8 A2 2 0 1 0 10 8 A2 2 0 1 0 6 8",
        ["cpu"]     = "M4 4 H12 V12 H4 Z M2 6 H4 M2 10 H4 M12 6 H14 M12 10 H14 M6 2 V4 M10 2 V4 M6 12 V14 M10 12 V14",
    };
}
