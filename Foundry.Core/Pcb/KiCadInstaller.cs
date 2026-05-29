namespace Foundry.Core.Pcb;

/// <summary>
/// Locates a KiCad install — its bundled Python interpreter (the only thing that can <c>import pcbnew</c>)
/// and <c>kicad-cli.exe</c> — on PATH or under <c>C:\Program Files\KiCad\&lt;ver&gt;\bin</c> (newest first).
/// Mirrors <see cref="Foundry.Core.Firmware.FirmwareBuilder.Locate"/> / <see cref="Foundry.Core.Cad.OpenScadInstaller"/>,
/// but does <b>not</b> auto-download: KiCad is a ~1 GB MSI, so we locate-only and surface install guidance.
/// </summary>
public static class KiCadInstaller
{
    public const string DownloadUrl = "https://www.kicad.org/download/windows/";

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
    public sealed record Install(string BinDir, string PythonPath, string KicadCliPath, string FootprintDir, string Version);

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
}
