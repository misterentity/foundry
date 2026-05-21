using System.Windows;
using System.Windows.Media;

namespace Foundry.App.Controls;

/// <summary>
/// Tiny per-card netlist preview — port of projects.jsx MiniDiagram. Deterministic node
/// layout seeded from the project id; first node/edge tinted by status.
/// </summary>
public sealed class MiniDiagram : FrameworkElement
{
    public static readonly DependencyProperty ProjectIdProperty = DependencyProperty.Register(
        nameof(ProjectId), typeof(string), typeof(MiniDiagram),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(MiniDiagram),
        new FrameworkPropertyMetadata("ok", FrameworkPropertyMetadataOptions.AffectsRender));

    public string ProjectId { get => (string)GetValue(ProjectIdProperty); set => SetValue(ProjectIdProperty, value); }
    public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public MiniDiagram() => Height = 80;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : 320;
        double h = ActualHeight > 0 ? ActualHeight : 80;
        double sx = w / 320.0, sy = h / 80.0;

        int seed = 0;
        foreach (var ch in ProjectId ?? "") if (char.IsDigit(ch)) seed = seed * 10 + (ch - '0');

        // JS-style remainder: C# % keeps the dividend's sign, matching Math.sin(...)%1.
        double Rand(int n) => (Math.Sin(seed * (n + 1)) * 10000.0) % 1.0;

        var color = Status switch
        {
            "fail" => Color.FromRgb(0xEF, 0x44, 0x44),
            "warn" => Color.FromRgb(0xFB, 0xBF, 0x24),
            _ => (Color)(Application.Current?.TryFindResource("Color.Accent") ?? Color.FromRgb(0xFF, 0x5A, 0x1F)),
        };
        var colorBrush = new SolidColorBrush(color);
        var faint = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x4C));
        var surface2 = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1C));
        var hair3 = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46)), 1);

        // grid
        var gp = new Pen(new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), 1);
        for (double x = 0; x <= w; x += 16 * sx) dc.DrawLine(gp, new Point(x, 0), new Point(x, h));
        for (double y = 0; y <= h; y += 16 * sy) dc.DrawLine(gp, new Point(0, y), new Point(w, y));

        var nodes = new Point[5];
        for (int i = 0; i < 5; i++)
        {
            double nx = (20 + (Rand(i * 2) + 1) * 0.5 * 280) * sx;
            double ny = (14 + (Rand(i * 2 + 1) + 1) * 0.5 * 50) * sy;
            nodes[i] = new Point(nx, ny);
        }

        for (int i = 1; i < 5; i++)
            dc.DrawLine(new Pen(i == 1 ? colorBrush : faint, 1.2), nodes[i - 1], nodes[i]);

        for (int i = 0; i < 5; i++)
        {
            var fill = i == 0 ? colorBrush : (Brush)surface2;
            var pen = i == 0 ? new Pen(colorBrush, 1) : hair3;
            dc.DrawRectangle(fill, pen, new Rect(nodes[i].X - 4, nodes[i].Y - 4, 8, 8));
        }
    }
}
