using System.IO.Compression;
using System.Net.Http;

namespace Foundry.Core.Simulation;

/// <summary>
/// Locates a Renode executable on PATH or in the app-local tools folder, or downloads the pinned
/// portable build on demand to %LocalAppData%/Foundry/tools/renode/. Mirrors
/// <see cref="Cad.OpenScadInstaller"/>. Renode is shelled out to (long-lived headless process), never
/// vendored. Pinned to 1.16.1 — the .repl/.resc syntax and the community RP2040 model track this version.
/// </summary>
public static class RenodeInstaller
{
    /// <summary>Pinned Renode version. .repl/.resc generation and the RP2040 model are validated against it.</summary>
    public const string Version = "1.16.1";

    // NOTE: the 1.16.1 Windows asset is the .NET portable — "renode-1.16.1.windows-portable.zip" (the old mono
    // name) does NOT exist for this release and 404s; it must be the "-dotnet" asset.
    private const string PortableUrl =
        "https://github.com/renode/renode/releases/download/v1.16.1/renode-1.16.1.windows-portable-dotnet.zip";

    public static string ToolsDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry", "tools", "renode");

    /// <summary>The first Renode.exe found in PATH or the app-local tools folder (recursively), or null.</summary>
    public static string? Locate()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try { var p = System.IO.Path.Combine(dir.Trim(), "Renode.exe"); if (System.IO.File.Exists(p)) return p; }
            catch { }
        }
        if (!System.IO.Directory.Exists(ToolsDir)) return null;
        var direct = System.IO.Path.Combine(ToolsDir, "Renode.exe");
        if (System.IO.File.Exists(direct)) return direct;
        // The portable zip extracts into a versioned subfolder (e.g. renode_1.16.1).
        try
        {
            foreach (var p in System.IO.Directory.EnumerateFiles(ToolsDir, "Renode.exe", System.IO.SearchOption.AllDirectories))
                return p;
        }
        catch { }
        return null;
    }

    public static bool IsInstalled => Locate() is not null;

    /// <summary>Download the pinned portable Renode zip into <see cref="ToolsDir"/> and extract. Returns the exe path.</summary>
    public static async Task<string> DownloadAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(ToolsDir);
        var zip = System.IO.Path.Combine(ToolsDir, "renode.zip");
        Diagnostics.AppLog.Info("sim", $"downloading Renode {Version} (portable) — this is a large file…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, PortableUrl, zip, null, ct);
        // Zip-slip-safe extract (a malicious archive can't write outside ToolsDir).
        Provisioning.DownloadVerifier.ExtractZipSafe(zip, ToolsDir, overwrite: true);
        try { System.IO.File.Delete(zip); } catch { }
        var exe = Locate() ?? throw new InvalidOperationException("Renode.exe not found after download.");
        // Fail-closed: refuse to run a Renode.exe that isn't validly publisher-signed.
        Provisioning.DownloadVerifier.RequireAuthenticode(exe, "downloaded Renode.exe");
        Diagnostics.AppLog.Info("sim", $"Renode {Version} installed at {exe}");
        return exe;
    }
}
