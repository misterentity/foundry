using System.Diagnostics;
using System.Net.Http;
using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb;

/// <summary>
/// Locates a KiCad install — its bundled Python interpreter (the only thing that can <c>import pcbnew</c>)
/// and <c>kicad-cli.exe</c> — on PATH or under <c>C:\Program Files\KiCad\&lt;ver&gt;\bin</c> (newest first).
/// Mirrors <see cref="Foundry.Core.Firmware.FirmwareBuilder.Locate"/> / <see cref="Foundry.Core.Cad.OpenScadInstaller"/>.
/// Auto-install via <see cref="InstallAsync"/>: winget per-user (no UAC, lands where <see cref="Locate"/>
/// expects), falling back to the official silent NSIS exe (one UAC prompt) only when winget is absent.
/// </summary>
public static class KiCadInstaller
{
    public const string DownloadUrl = "https://www.kicad.org/download/windows/";

    /// <summary>winget package id (confirmed: KiCad.KiCad, installer type nullsoft/NSIS).</summary>
    public const string WingetId = "KiCad.KiCad";

    /// <summary>Official silent NSIS installer (fallback when winget is absent; may prompt one UAC).</summary>
    public const string FallbackExeUrl =
        "https://github.com/KiCad/kicad-source-mirror/releases/download/10.0.3/kicad-10.0.3-x86_64.exe";

    /// <summary>Install roots, each holding per-version <c>&lt;root&gt;\&lt;ver&gt;\bin</c> dirs: machine-wide
    /// Program Files first, then the per-user location winget uses by default
    /// (<c>%LOCALAPPDATA%\Programs\KiCad</c>) — a winget install is per-user unless run elevated.</summary>
    private static IEnumerable<string> KiCadRoots()
    {
        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KiCad");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return System.IO.Path.Combine(local, "Programs", "KiCad");
    }

    /// <summary>Resolved KiCad install: the bin dir, python interpreter, kicad-cli, and footprint lib dir.</summary>
    public sealed record Install(string BinDir, string PythonPath, string KicadCliPath, string FootprintDir, string Version)
    {
        /// <summary>The symbol-library dir (sibling of <see cref="FootprintDir"/>) — source of the authoritative
        /// pin name→number tables used by <see cref="SymbolPinMap"/> to resolve logical MCU pins to real pads.</summary>
        public string SymbolDir => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(FootprintDir.TrimEnd('/', '\\')) ?? FootprintDir, "symbols");
    }

    /// <summary>The located KiCad install (newest version found), or null when KiCad isn't installed.</summary>
    public static Install? Locate()
    {
        var bin = LocateBinDir();
        if (bin is null) return null;

        var python = System.IO.Path.Combine(bin, "python.exe");
        var cli = System.IO.Path.Combine(bin, "kicad-cli.exe");
        if (!System.IO.File.Exists(python)) return null;   // no interpreter ⇒ can't run pcbnew

        var root = System.IO.Path.GetDirectoryName(bin) ?? bin;   // <root>\<ver>
        var version = System.IO.Path.GetFileName(root) ?? "";
        var footprints = FootprintDirFor(root, version);
        return new Install(bin, python, cli, footprints, version);
    }

    public static bool IsInstalled => Locate() is not null;

    /// <summary>Find a KiCad <c>bin</c> dir: PATH (via kicad-cli) first, then Program Files versions newest-first.</summary>
    private static string? LocateBinDir()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try
            {
                var p = System.IO.Path.Combine(dir.Trim(), "kicad-cli.exe");
                if (System.IO.File.Exists(p)) return System.IO.Path.GetDirectoryName(p);
            }
            catch { }
        }

        foreach (var root in KiCadRoots())
        {
            try
            {
                if (!System.IO.Directory.Exists(root)) continue;
                foreach (var ver in System.IO.Directory.EnumerateDirectories(root)
                             .OrderByDescending(d => ParseVersion(System.IO.Path.GetFileName(d))))   // numeric: 10.0 before 9.0
                {
                    var bin = System.IO.Path.Combine(ver, "bin");
                    if (System.IO.File.Exists(System.IO.Path.Combine(bin, "kicad-cli.exe"))) return bin;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>Parse a KiCad version dir name ("10.0", "9.0", "8") to a comparable <see cref="Version"/>; 0.0 on failure.</summary>
    private static Version ParseVersion(string? name)
    {
        if (Version.TryParse(name, out var v)) return v;
        if (int.TryParse(name, out var major)) return new Version(major, 0);
        return new Version(0, 0);
    }

    /// <summary>
    /// Footprint library dir for a located install: the per-version <c>KICAD&lt;major&gt;_FOOTPRINT_DIR</c>
    /// override if set, else <c>&lt;root&gt;\share\kicad\footprints</c>. Each library is a <c>&lt;Lib&gt;.pretty</c> subdir.
    /// </summary>
    private static string FootprintDirFor(string root, string version)
    {
        var major = version.Split('.').FirstOrDefault();
        if (!string.IsNullOrEmpty(major))
        {
            var env = Environment.GetEnvironmentVariable($"KICAD{major}_FOOTPRINT_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
        }
        return System.IO.Path.Combine(root, "share", "kicad", "footprints");
    }

    /// <summary>The <c>winget.exe</c> path (App Installer, per-user) on PATH, or null when absent.</summary>
    private static string? LocateWinget()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try { var p = System.IO.Path.Combine(dir.Trim(), "winget.exe"); if (System.IO.File.Exists(p)) return p; }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Auto-install KiCad. Primary path is winget per-user (no UAC, installs to
    /// <c>%LOCALAPPDATA%\Programs\KiCad\&lt;ver&gt;\bin</c> where <see cref="Locate"/> finds it). When winget
    /// is absent it downloads the official NSIS exe and runs it silently (<c>/S</c>) — this may prompt one
    /// unavoidable UAC. Idempotent: returns immediately if already installed. Re-runs <see cref="Locate"/>
    /// after either path and throws when KiCad still isn't found.
    /// </summary>
    public static async Task<Install> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var existing = Locate();
        if (existing is not null) return existing;

        var winget = LocateWinget();
        if (winget is not null)
        {
            progress?.Report("Installing via winget…");
            AppLog.Info("pcb", "installing KiCad via winget (per-user)…");
            var exit = await RunAsync(winget,
                $"install -e --id {WingetId} --scope user --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                ct);
            var located = Locate();
            if (located is not null)
            {
                AppLog.Info("pcb", $"KiCad installed via winget at {located.BinDir}");
                progress?.Report("Installed");
                return located;
            }
            AppLog.Warn("pcb", $"winget KiCad install did not land where expected (exit {exit}); trying NSIS exe fallback…");
        }

        // Fallback: official silent NSIS exe (may prompt one UAC).
        progress?.Report("Downloading installer…");
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry", "tools", "kicad");
        System.IO.Directory.CreateDirectory(dir);
        var exe = System.IO.Path.Combine(dir, "kicad-installer.exe");
        AppLog.Info("pcb", "downloading official KiCad NSIS installer…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, FallbackExeUrl, exe, null, ct);
        // Fail-closed BEFORE running an installer elevated/silent: refuse a KiCad exe that isn't validly
        // publisher-signed (the highest-stakes path — silent elevated execution). KiCad embed-signs its exe.
        Provisioning.DownloadVerifier.RequireAuthenticode(exe, "downloaded KiCad installer");
        progress?.Report("Running installer…");
        AppLog.Info("pcb", "running KiCad NSIS installer silently (/S) — may prompt UAC…");
        await RunAsync(exe, "/S", ct);
        try { System.IO.File.Delete(exe); } catch { }

        var result = Locate() ?? throw new InvalidOperationException("KiCad not found after install.");
        AppLog.Info("pcb", $"KiCad installed at {result.BinDir}");
        progress?.Report("Installed");
        return result;
    }

    /// <summary>Run a console process to completion (no window, output captured to AppLog). Returns exit code.</summary>
    private static async Task<int> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        // Bound the installer: a wedged winget/NSIS run (stalled download, unexpected prompt) must not hang
        // the install forever, and must not leave the installer process running when we give up.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (!ct.IsCancellationRequested)   // our timeout fired (not the caller's cancellation)
                throw new TimeoutException($"{System.IO.Path.GetFileName(fileName)} did not finish within 20 minutes.");
            throw;
        }
        var err = (await stderr).Trim();
        if (!string.IsNullOrEmpty(err)) AppLog.Warn("pcb", $"{System.IO.Path.GetFileName(fileName)}: {err}");
        return proc.ExitCode;
    }
}
