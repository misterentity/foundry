using System.IO.Compression;
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
    /// <summary>Pinned SHA-256 of the v2.2.4 jar (a .jar isn't Authenticode-signable). Verified fail-closed on download.
    /// Computed from the official GitHub release asset (github.com/freerouting/freerouting v2.2.4).</summary>
    public const string JarSha256 = "06E2E89CB1AE7FE74FB37176C67A083BBB8A250AB72006BCCCC4DB18ACA91ED7";
    public const string JdkDownloadUrl = "https://adoptium.net/temurin/releases/?version=25";

    /// <summary>Adoptium API binary endpoint — 307-redirects to the latest Temurin 25 GA portable JRE
    /// .zip for Windows x64. HttpClient follows the redirect, so we download this URL directly. JRE 25
    /// because the pinned FreeRouting 2.2.4 jar is class file 69 = Java 25.</summary>
    public const string JreUrl = "https://api.adoptium.net/v3/binary/latest/25/ga/windows/x64/jre/hotspot/normal/eclipse";

    private const string JarFileName = "freerouting-2.2.4.jar";

    public static string ToolsDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry", "tools", "freerouting");

    /// <summary>App-local portable JRE location: %LocalAppData%/Foundry/tools/java/ (the Temurin zip
    /// extracts into a nested versioned <c>jdk-25.*-jre</c> folder beneath this).</summary>
    public static string JavaToolsDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foundry", "tools", "java");

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

    /// <summary>The <c>java.exe</c> launcher: the app-local portable JRE FIRST (so a Foundry-downloaded
    /// JRE is authoritative), then <c>JAVA_HOME</c>, then PATH. Null when no JRE is present.</summary>
    public static string? LocateJava()
    {
        // App-local portable JRE wins — the Temurin zip nests java.exe under a versioned subfolder.
        if (System.IO.Directory.Exists(JavaToolsDir))
        {
            try
            {
                foreach (var p in System.IO.Directory.EnumerateFiles(JavaToolsDir, "java.exe", System.IO.SearchOption.AllDirectories))
                    return p;
            }
            catch { }
        }

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

    /// <summary>True when a Java launcher is resolvable (app-local JRE, JAVA_HOME, or PATH).</summary>
    public static bool JavaPresent => LocateJava() is not null;

    /// <summary>
    /// Download the portable Temurin 25 JRE zip into <see cref="JavaToolsDir"/> and extract it, so
    /// FreeRouting can run with zero system Java. Mirrors <see cref="Cad.OpenScadInstaller.DownloadAsync"/>:
    /// download → extract (nested versioned folder) → delete zip. Returns the resolved <c>java.exe</c> path.
    /// </summary>
    public static async Task<string> DownloadJreAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(JavaToolsDir);
        var zip = System.IO.Path.Combine(JavaToolsDir, "jre.zip");
        AppLog.Info("pcb", "downloading Temurin 25 JRE (portable)…");
        // The Adoptium 'latest' endpoint has no stable hash, so verify integrity via Authenticode on the
        // extracted java.exe (Eclipse Adoptium embed-signs Windows binaries) rather than a pinned SHA.
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, JreUrl, zip, null, ct);
        Provisioning.DownloadVerifier.ExtractZipSafe(zip, JavaToolsDir, overwrite: true);
        try { System.IO.File.Delete(zip); } catch { }
        var java = LocateJava() ?? throw new InvalidOperationException("java.exe not found after JRE download.");
        // Fail-closed: an unsigned/tampered JRE is deleted and the install throws.
        if (!Provisioning.DownloadVerifier.VerifyAuthenticode(java))
        {
            try { System.IO.Directory.Delete(JavaToolsDir, recursive: true); } catch { }
            throw new Provisioning.IntegrityException("downloaded JRE java.exe failed Authenticode verification — refusing to use it.");
        }
        AppLog.Info("pcb", $"Java (JRE 25) installed at {java}");
        return java;
    }

    /// <summary>Download the single FreeRouting jar into ToolsDir on demand. Returns the jar path.</summary>
    public static async Task<string> DownloadJarAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(ToolsDir);
        // A .jar isn't a PE so it can't be Authenticode-verified — pin its published SHA-256 (fail-closed).
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, JarUrl, JarPath, JarSha256, ct);
        AppLog.Info("pcb", $"FreeRouting {Version} jar downloaded to {JarPath}");
        return JarPath;
    }
}
