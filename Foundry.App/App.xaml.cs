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

        // Global crash logging + user-facing surface (PRD §13 — no uncaught exception crashes silently).
        DispatcherUnhandledException += (_, ex) => { LogCrash("UI", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash("Domain", ex.ExceptionObject as Exception);

        Foundry.Core.Diagnostics.AppLog.Info("app", $"Foundry {AppInfo.Version} started · {Environment.OSVersion} · .NET {Environment.Version}");

        // App lives in the tray: closing the window hides it; Quit (tray) exits explicitly.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var main = new MainViewModel(new CredentialStore());
        switch (Environment.GetEnvironmentVariable("FOUNDRY_START"))
        {
            case "projects": main.ShowProjects(); break;
            case "newproject": main.ShowNewProject(); break;
            case "workspace": main.OpenSample(); break;
            case "settings": main.ShowSettings(); break;
            case "logs": main.ShowLogs(); break;
            case "gen": GenerateForDiag(main); break;
        }

        _window = new MainWindow { DataContext = main };
        _window.Show();

        SetupTray();
    }

    // Dev hook: FOUNDRY_GEN=<prompt> generates a real project then opens the workspace (for QA).
    private static void GenerateForDiag(MainViewModel main)
    {
        var prompt = Environment.GetEnvironmentVariable("FOUNDRY_GEN");
        if (string.IsNullOrWhiteSpace(prompt)) { main.OpenSample(); return; }
        var key = new CredentialStore().Read(CredentialStore.AnthropicTarget);
        if (string.IsNullOrWhiteSpace(key)) { main.OpenSample(); return; }
        var gen = new Foundry.Core.Generation.ProjectGenerator(new AnthropicClient(key), ConfigStore.Load().ModelId);
        // Off the UI thread to avoid a sync-over-async deadlock (diag hook only).
        var r = System.Threading.Tasks.Task.Run(() => gen.GenerateAsync(prompt)).GetAwaiter().GetResult();
        if (r.Ok && r.Project is not null) main.OpenGenerated(r.Project);
        else main.OpenSample();
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
            // Repo is pinned to the build-time constants (NOT read from config) so a writable
            // config.json can't repoint the updater at an attacker-controlled repo.
            var updater = new GitHubUpdater();
            var result = await updater.CheckAsync(AppInfo.DefaultUpdateOwner, AppInfo.DefaultUpdateRepo, AppInfo.Version);

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
            if (!InstallerTrusted(path))
            {
                MessageBox.Show("The downloaded update could not be verified as signed by Foundry's publisher, so it was not run. Download it manually from the releases page if you trust it.",
                    "Foundry — update blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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

    /// <summary>
    /// Only run a downloaded installer if it's Authenticode-signed by the SAME publisher as the
    /// running app (thumbprint pin). If the running app is itself unsigned (no cert to pin to),
    /// we can't verify the publisher — allow it but log, since the repo is already pinned.
    /// </summary>
    private static bool InstallerTrusted(string path)
    {
        var appCert = SignerCert(Process.GetCurrentProcess().MainModule?.FileName);
        if (appCert is null)
        {
            Foundry.Core.Diagnostics.AppLog.Warn("update", "running app is unsigned — cannot pin publisher; running update unverified");
            return true;
        }
        var fileCert = SignerCert(path);
        if (fileCert is null)
        {
            Foundry.Core.Diagnostics.AppLog.Error("update", "downloaded installer is not signed — refusing to run");
            return false;
        }
        var match = string.Equals(fileCert.Thumbprint, appCert.Thumbprint, StringComparison.OrdinalIgnoreCase);
        if (!match)
            Foundry.Core.Diagnostics.AppLog.Error("update", $"installer signer {fileCert.Thumbprint} ≠ app signer {appCert.Thumbprint} — refusing to run");
        return match;
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2? SignerCert(string? path)
    {
        try
        {
            return path is null ? null
                : new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
        }
        catch { return null; } // unsigned / unreadable
    }

    private static void OpenUrl(string url)
    {
        // only launch http(s) — never hand an arbitrary scheme to the shell
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    private void Quit()
    {
        Foundry.Core.Sidecar.SidecarHost.Shared.Dispose();
        _tray?.Dispose();
        _window?.ForceClose();
        Shutdown();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "crash.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:u}] {source}: {ex}\n\n");
            try { Foundry.Core.Diagnostics.AppLog.Error("crash", ex?.Message ?? source, ex?.ToString()); } catch { }
            if (Environment.GetEnvironmentVariable("FOUNDRY_NODIALOG") != "1")
                MessageBox.Show($"Foundry hit an error and logged it to:\n{path}\n\n{ex?.Message}", "Foundry", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* never throw from the crash handler */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Foundry.Core.Sidecar.SidecarHost.Shared.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
