using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Foundry.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Dev affordance: FOUNDRY_SHOT=<path> renders the window to a PNG once it has
        // settled, then exits. Reliable self-capture for verification (no screen race).
        var shot = Environment.GetEnvironmentVariable("FOUNDRY_SHOT");
        if (!string.IsNullOrEmpty(shot))
        {
            ContentRendered += (_, _) =>
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
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
