using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FProject = Foundry.Core.Project.Project;

namespace Foundry.App.Rendering;

/// <summary>Renders the auto-laid-out wiring diagram to a PNG (for the PDF + the Wiring tab export).
/// Must be called on the UI thread.</summary>
public static class WiringImage
{
    public static byte[]? Render(FProject project, double dpiScale = 2.0)
    {
        try
        {
            var ctrl = new WiringDiagramControl { Project = project };
            ctrl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = ctrl.DesiredSize;
            if (size.Width < 10 || size.Height < 10) size = new Size(1100, 720);
            ctrl.Arrange(new Rect(size));
            ctrl.UpdateLayout();

            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling(size.Width * dpiScale), (int)Math.Ceiling(size.Height * dpiScale),
                96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
            rtb.Render(ctrl);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Foundry.Core.Diagnostics.AppLog.Warn("export", $"wiring image render failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Render the wiring diagram to SVG (vector). Must be called on the UI thread.</summary>
    public static string? RenderSvg(FProject project)
    {
        try { return new WiringDiagramControl { Project = project }.ToSvg(); }
        catch (Exception ex)
        {
            Foundry.Core.Diagnostics.AppLog.Warn("export", $"wiring SVG render failed: {ex.Message}");
            return null;
        }
    }
}
