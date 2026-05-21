using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Media.Imaging;
using Foundry.App.ViewModels;
using Foundry.Core;
using Foundry.Core.Ai;
using Foundry.Core.Config;
using Foundry.Core.Security;
using Foundry.Core.Update;
using Forms = System.Windows.Forms;

namespace Foundry.App;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private bool _updateInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // App lives in the tray: closing the window hides it; Quit (tray) exits explicitly.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var credentials = new CredentialStore();
        var anthropicKey = credentials.Read(CredentialStore.AnthropicTarget);
        IAnthropicClient ai = string.IsNullOrWhiteSpace(anthropicKey)
            ? new StubAnthropicClient()
            : new AnthropicClient(anthropicKey);
        IPipeline pipeline = new ChatPipeline(ai);

        var nexarKey = credentials.Read(CredentialStore.NexarTarget);
        Foundry.Core.Sourcing.SourcingService.Shared = new Foundry.Core.Sourcing.SourcingService(
            string.IsNullOrWhiteSpace(nexarKey)
                ? new Foundry.Core.Sourcing.NullSourcingProvider()
                : new Foundry.Core.Sourcing.NexarSourcingProvider(nexarKey));

        var main = new MainViewModel(credentials, ai, pipeline);
        switch (Environment.GetEnvironmentVariable("FOUNDRY_START"))
        {
            case "projects": main.ShowProjects(); break;
            case "newproject": main.ShowNewProject(); break;
            case "workspace": main.OpenSample(); break;
            case "settings": main.ShowSettings(); break;
        }

        _window = new MainWindow { DataContext = main };
        _window.Show();

        SetupTray();
    }

    private void SetupTray()
    {
        _window!.Icon = new BitmapImage(new Uri("pack://application:,,,/Themes/foundry.png"));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(MenuItem("Open Foundry", ShowMainWindow));
        menu.Items.Add(MenuItem("Check for updates…", () => _ = CheckForUpdatesAsync(interactive: true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(MenuItem("Quit", Quit));

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = $"Foundry {AppInfo.Version}",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null)
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    private static Forms.ToolStripMenuItem MenuItem(string header, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>Check GitHub Releases and, if a newer installer exists, download + run it (then exit).</summary>
    public async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updateInProgress) return;
        _updateInProgress = true;
        try
        {
            var cfg = ConfigStore.Load();
            var updater = new GitHubUpdater();
            var result = await updater.CheckAsync(cfg.UpdateOwner, cfg.UpdateRepo, AppInfo.Version);

            if (!result.Ok)
            {
                if (interactive) MessageBox.Show($"Update check failed: {result.Message}", "Foundry", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!result.UpdateAvailable)
            {
                if (interactive) MessageBox.Show(result.Message, "Foundry — up to date", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var info = result.Info!;
            if (info.InstallerUrl is null)
            {
                if (MessageBox.Show($"Update {info.TagName} is available, but no installer asset was found.\n\nOpen the release page?",
                        "Foundry — update available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenUrl(info.ReleaseUrl);
                return;
            }

            var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : $"\n\n{Trim(info.Notes, 600)}";
            if (MessageBox.Show($"Update {info.TagName} is available (you have {AppInfo.Version}).{notes}\n\nDownload and install now? Foundry will close to finish updating.",
                    "Foundry — update available", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var path = await updater.DownloadAsync(info.InstallerUrl, info.InstallerName!);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            Quit();
        }
        catch (Exception ex)
        {
            if (interactive) MessageBox.Show($"Update failed: {ex.Message}", "Foundry", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _updateInProgress = false; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    private void Quit()
    {
        Foundry.Core.Sidecar.SidecarHost.Shared.Dispose();
        _tray?.Dispose();
        _window?.ForceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Foundry.Core.Sidecar.SidecarHost.Shared.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
