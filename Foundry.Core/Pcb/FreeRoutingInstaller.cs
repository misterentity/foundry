using System.Net.Http;
using Foundry.Core.Diagnostics;

namespace Foundry.Core.Pcb;

/// <summary>
/// Locates the two things v2.4 routing needs: a Java runtime (JRE 25+ — the pinned FreeRouting 2.2.4 jar
/// is compiled for Java 25 / class file 69) and the FreeRouting jar.
/// Java is locate-only — found via <c>JAVA_HOME</c> then PATH, never auto-installed (clear guidance +
/// JDK download hint when absent). The FreeRouting jar is a single ~58 MB file, so — unlike KiCad's MSI —
/// it CAN be downloaded on demand to <c>%LocalAppData%/Foundry/tools/freerouting/</c>, mirroring
/// <see cref="Foundry.Core.Cad.OpenScadInstaller.DownloadAsync"/> / <see cref="Foundry.Core.Firmware.FirmwareBuilder.DownloadCliAsync"/>.
/// </summary>
public static class FreeRoutingInstaller
{
    public const string Version = "2.2.4";
    public const string JarUrl = "https://github.com/freerouting/freerouting/releases/download/v2.2.4/freerouting-2.2.4.jar";
    public const string JdkDownloadUrl = "https://adoptium.net/temurin/releases/?version=25";

    private const string JarFileName = "freerouting-2.2.4.jar";

    public static string ToolsDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry", "tools", "freerouting");

    /// <summary>The on-demand jar location under the app-local tools folder.</summary>
    public static string JarPath => System.IO.Path.Combine(ToolsDir, JarFileName);

    /// <summary>Resolved routing toolchain: the Java launcher and the FreeRouting jar.</summary>
    public sealed record Install(string JavaPath, string JarPath);

    /// <summary>Located Java + jar, or null when either is missing. Does not download — call <see cref="DownloadJarAsync"/> first.</summary>
    public static Install? Locate()
    {
        var java = LocateJava();
        if (java is null) return null;
        if (!System.IO.File.Exists(JarPath)) return null;
        return new Install(java, JarPath);
    }

    public static bool IsInstalled => Locate() is not null;

    /// <summary>The <c>java.exe</c> launcher from <c>JAVA_HOME</c> then PATH, or null when no JRE is present.</summary>
    public static string? LocateJava()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            try
            {
                var p = System.IO.Path.Combine(home.Trim(), "bin", "java.exe");
                if (System.IO.File.Exists(p)) return p;
            }
            catch { }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            try
            {
                var p = System.IO.Path.Combine(dir.Trim(), "java.exe");
                if (System.IO.File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>True when the jar is already present app-locally (independent of Java).</summary>
    public static bool JarPresent => System.IO.File.Exists(JarPath);

    /// <summary>Download the single FreeRouting jar into ToolsDir on demand. Returns the jar path.</summary>
    public static async Task<string> DownloadJarAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(ToolsDir);
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            var bytes = await http.GetByteArrayAsync(JarUrl, ct);
            await System.IO.File.WriteAllBytesAsync(JarPath, bytes, ct);
        }
        if (!System.IO.File.Exists(JarPath))
            throw new InvalidOperationException("freerouting jar not found after download.");
        AppLog.Info("pcb", $"FreeRouting {Version} jar downloaded to {JarPath}");
        return JarPath;
    }
}
