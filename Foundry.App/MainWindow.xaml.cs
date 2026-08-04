using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Foundry.App;

public partial class MainWindow : Window
{
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();

        // Dev affordance: FOUNDRY_SHOT=<path> renders the window to a PNG once it has
        // settled, then exits. Reliable self-capture for verification (no screen race).
        var shot = Environment.GetEnvironmentVariable("FOUNDRY_SHOT");
        if (!string.IsNullOrEmpty(shot))
        {
            // FOUNDRY_SHOT_SIZE=WxH renders at a specific window size. Layout defects are size-dependent —
            // a cap or a fixed column only shows up past a certain width — so verifying them needs the
            // size to be a parameter of the capture, not whatever the default happens to be.
            var size = Environment.GetEnvironmentVariable("FOUNDRY_SHOT_SIZE");
            if (!string.IsNullOrEmpty(size))
            {
                var wh = size.Split('x', 'X');
                if (wh.Length == 2 && double.TryParse(wh[0], out var sw) && double.TryParse(wh[1], out var sh))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = 0; Top = 0;
                    MinWidth = Math.Min(MinWidth, sw);
                    MinHeight = Math.Min(MinHeight, sh);
                    Width = sw; Height = sh;
                }
            }
            var delayMs = int.TryParse(Environment.GetEnvironmentVariable("FOUNDRY_SHOT_DELAY_MS"), out var d) ? d : 1200;
            ContentRendered += (_, _) =>
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
                t.Tick += (_, _) => { t.Stop(); RenderToFile(shot); Application.Current.Shutdown(); };
                t.Start();
            };
        }
    }

    private void RenderToFile(string path)
    {
        var target = (FrameworkElement)Content;
        int w = (int)Math.Ceiling(target.ActualWidth);
        int h = (int)Math.Ceiling(target.ActualHeight);
        if (w <= 0 || h <= 0) return;

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Measure(new Size(w, h));
        target.Arrange(new Rect(new Size(w, h)));
        rtb.Render(target);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Really close the window (used by tray Quit) instead of hiding to tray.</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // App lives in the tray: the X / close button hides the window instead of exiting.
        if (!_forceClose && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOUNDRY_SHOT")))
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void Titlebar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
