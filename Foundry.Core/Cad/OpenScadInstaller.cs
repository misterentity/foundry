using System.IO.Compression;
using System.Net.Http;

namespace Foundry.Core.Cad;

/// <summary>
/// Locates an OpenSCAD CLI for the SCAD render path (PRD v2 Phase A), or downloads the portable
/// build on demand to %LocalAppData%/Foundry/tools/openscad/. Subprocess invocation only — Foundry
/// does not vendor OpenSCAD sources (it's GPL; we shell out to it like we do trimesh / arduino-cli).
/// </summary>
public static class OpenScadInstaller
{
    private const string PortableUrl = "https://files.openscad.org/OpenSCAD-2021.01-x86-64.zip";

    public static string ToolsDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry", "tools", "openscad");

    /// <summary>The first openscad.exe found in PATH or the app-local tools folder, or null.</summary>
    public static string? Locate()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try { var p = System.IO.Path.Combine(dir.Trim(), "openscad.exe"); if (System.IO.File.Exists(p)) return p; }
            catch { }
        }
        if (!System.IO.Directory.Exists(ToolsDir)) return null;
        var direct = System.IO.Path.Combine(ToolsDir, "openscad.exe");
        if (System.IO.File.Exists(direct)) return direct;
        foreach (var sub in System.IO.Directory.EnumerateDirectories(ToolsDir))
        {
            var p = System.IO.Path.Combine(sub, "openscad.exe");
            if (System.IO.File.Exists(p)) return p;
        }
        return null;
    }

    public static bool IsInstalled => Locate() is not null;

    /// <summary>Download the portable OpenSCAD zip into ToolsDir and extract. Returns the exe path.</summary>
    public static async Task<string> DownloadAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(ToolsDir);
        var zip = System.IO.Path.Combine(ToolsDir, "openscad.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, PortableUrl, zip, null, ct);
        Provisioning.DownloadVerifier.ExtractZipSafe(zip, ToolsDir, overwrite: true);
        try { System.IO.File.Delete(zip); } catch { }
        var exe = Locate() ?? throw new InvalidOperationException("openscad.exe not found after download.");
        // Fail-closed: refuse to run an openscad.exe that isn't validly publisher-signed.
        Provisioning.DownloadVerifier.RequireAuthenticode(exe, "downloaded openscad.exe");
        Diagnostics.AppLog.Info("cad", $"OpenSCAD installed at {exe}");
        return exe;
    }
}
