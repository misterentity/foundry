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

    /// <summary>
    /// Pinned SHA-256 of the portable zip, computed from the official files.openscad.org asset over TLS.
    /// OpenSCAD does NOT Authenticode-sign openscad.exe (verified: Get-AuthenticodeSignature reports
    /// NotSigned), so a signature gate here can never pass — the archive hash is the only real anchor.
    /// </summary>
    public const string PortableSha256 = "FB0CAABF5BBC89F8F2F80C10B79AE64D697AAFF6EFD58B2756F5D6270EDB7BA7";

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
        var parent = System.IO.Path.GetDirectoryName(ToolsDir)!;
        System.IO.Directory.CreateDirectory(parent);
        var zip = System.IO.Path.Combine(parent, "openscad.zip");
        // Fail-closed on the ARCHIVE hash (the publisher ships no signature), then quarantine-then-promote so
        // a rejected payload never lands where Locate() would later report it as installed.
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, PortableUrl, zip, PortableSha256, ct);
        try
        {
            Provisioning.DownloadVerifier.ExtractVerifiedZip(zip, ToolsDir);
        }
        finally { try { System.IO.File.Delete(zip); } catch { } }

        var exe = Locate() ?? throw new InvalidOperationException("openscad.exe not found after download.");
        Diagnostics.AppLog.Info("cad", $"OpenSCAD installed at {exe}");
        return exe;
    }
}
