using Foundry.Core.Pcb;

namespace Foundry.Tests;

// ---- RouteOptions defaults -----------------------------------------------------------------------

public class RouteOptionsTests
{
    [Fact]
    public void Default_TenPasses_OneThread()
    {
        var o = RouteOptions.Default;
        Assert.Equal(10, o.Passes);
        Assert.Equal(1, o.Threads);
    }

    [Fact]
    public void Custom_OverridesPassesAndThreads()
    {
        var o = new RouteOptions(Passes: 50, Threads: 4);
        Assert.Equal(50, o.Passes);
        Assert.Equal(4, o.Threads);
    }
}

// ---- FreeRoutingInstaller locate / metadata ------------------------------------------------------

public class FreeRoutingInstallerTests
{
    [Fact]
    public void Version_MatchesPinnedRelease()
    {
        Assert.Equal("2.2.4", FreeRoutingInstaller.Version);
    }

    [Fact]
    public void JarUrl_PointsAtThePinnedReleaseAsset()
    {
        Assert.Contains("freerouting/freerouting", FreeRoutingInstaller.JarUrl);
        Assert.Contains(FreeRoutingInstaller.Version, FreeRoutingInstaller.JarUrl);
        Assert.EndsWith(".jar", FreeRoutingInstaller.JarUrl);
    }

    [Fact]
    public void JdkDownloadUrl_PointsAtTemurin25()
    {
        // FreeRouting 2.2.4's jar is compiled for Java 25 (class file 69) — the runtime hint must match.
        Assert.Contains("adoptium", FreeRoutingInstaller.JdkDownloadUrl);
        Assert.Contains("25", FreeRoutingInstaller.JdkDownloadUrl);
    }

    [Fact]
    public void ToolsDir_And_JarPath_LiveUnderAppLocalTools()
    {
        Assert.EndsWith(Path.Combine("Foundry", "tools", "freerouting"), FreeRoutingInstaller.ToolsDir);
        Assert.Equal(Path.Combine(FreeRoutingInstaller.ToolsDir, "freerouting-2.2.4.jar"), FreeRoutingInstaller.JarPath);
    }

    [Fact]
    public void JarPresent_TracksJarPathOnDisk()
    {
        // Jar isn't downloaded here, so JarPresent and File.Exists must agree.
        Assert.Equal(File.Exists(FreeRoutingInstaller.JarPath), FreeRoutingInstaller.JarPresent);
    }

    [Fact]
    public void Locate_NullWhenJavaMissing()
    {
        // No JRE on this machine — Locate must degrade to null (never throw), regardless of the jar.
        if (FreeRoutingInstaller.LocateJava() is not null) return;   // guard: real JRE present, skip
        Assert.Null(FreeRoutingInstaller.Locate());
        Assert.False(FreeRoutingInstaller.IsInstalled);
    }

    [Fact]
    public void LocateJava_PrefersJavaHome_WhenItHasABinJava()
    {
        var home = Path.Combine(Path.GetTempPath(), "foundry_java_home_" + Guid.NewGuid().ToString("N")[..8]);
        var bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);
        var fakeJava = Path.Combine(bin, "java.exe");
        File.WriteAllText(fakeJava, "");

        var prev = Environment.GetEnvironmentVariable("JAVA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", home);
            Assert.Equal(fakeJava, FreeRoutingInstaller.LocateJava());
        }
        finally
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", prev);
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void LocateJava_NullWhenJavaHomeHasNoLauncher_AndPathHasNone()
    {
        var home = Path.Combine(Path.GetTempPath(), "foundry_empty_jh_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);   // no bin/java.exe inside

        var prevHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var prevPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", home);
            Environment.SetEnvironmentVariable("PATH", "");   // and nothing on PATH
            Assert.Null(FreeRoutingInstaller.LocateJava());
        }
        finally
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", prevHome);
            Environment.SetEnvironmentVariable("PATH", prevPath);
            Directory.Delete(home, true);
        }
    }
}

// ---- RouteResult.NotInstalled / Failed ----------------------------------------------------------

public class RouteResultFactoryTests
{
    [Fact]
    public void NotInstalled_SurfacesJreDownloadGuidance()
    {
        var r = RouteResult.NotInstalled();
        Assert.False(r.Installed);
        Assert.False(r.Ok);
        Assert.Null(r.RoutedPcbPath);
        Assert.Equal(0, r.TrackCount);
        Assert.False(r.FullyRouted);
        Assert.Contains(FreeRoutingInstaller.JdkDownloadUrl, r.Summary);
    }

    [Fact]
    public void Failed_IsInstalledButNotOk_CarriesNotes()
    {
        var r = RouteResult.Failed("Couldn't export Specctra DSN.", new[] { "boom" });
        Assert.True(r.Installed);
        Assert.False(r.Ok);
        Assert.Null(r.RoutedPcbPath);
        Assert.Equal("Couldn't export Specctra DSN.", r.Summary);
        Assert.Contains("boom", r.Notes);
    }

    [Fact]
    public void Failed_NullNotes_IsEmpty()
    {
        var r = RouteResult.Failed("nope");
        Assert.Empty(r.Notes);
    }
}

// ---- RouteResult.Parse — board-derived SES stats ------------------------------------------------

public class RouteResultParseTests
{
    private static string TempPcb()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "foundry_routed_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        return tmp;
    }

    [Fact]
    public void Parse_FullyRouted_SummarizesTracksAndVias()
    {
        var tmp = TempPcb();
        try
        {
            var json = "{\"ok\":true,\"out\":\"" + tmp.Replace("\\", "\\\\") +
                       "\",\"tracks\":42,\"vias\":3,\"unconnected\":0}";
            var r = RouteResult.Parse(json, "", 0, null, tmp);

            Assert.True(r.Installed);
            Assert.True(r.Ok);
            Assert.Equal(tmp, r.RoutedPcbPath);
            Assert.Equal(42, r.TrackCount);
            Assert.Equal(3, r.ViaCount);
            Assert.Equal(0, r.UnroutedCount);
            Assert.True(r.FullyRouted);
            Assert.Contains("42 tracks", r.Summary);
            Assert.Contains("3 vias", r.Summary);
            Assert.Contains("fully connected", r.Summary);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_PartiallyRouted_ReportsUnroutedCount_NotFullyRouted()
    {
        var tmp = TempPcb();
        try
        {
            var json = "{\"ok\":true,\"out\":\"" + tmp.Replace("\\", "\\\\") +
                       "\",\"tracks\":30,\"vias\":1,\"unconnected\":4}";
            var r = RouteResult.Parse(json, "", 0, null, tmp);

            Assert.True(r.Ok);
            Assert.Equal(4, r.UnroutedCount);
            Assert.False(r.FullyRouted);
            Assert.Contains("4 net(s) unrouted", r.Summary);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_FallsBackToExpectedOut_WhenJsonOmitsOut()
    {
        var tmp = TempPcb();
        try
        {
            var json = "{\"ok\":true,\"tracks\":10,\"vias\":0,\"unconnected\":0}";
            var r = RouteResult.Parse(json, "", 0, null, tmp);
            Assert.True(r.Ok);
            Assert.Equal(tmp, r.RoutedPcbPath);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_Ok_ButMissingFile_BecomesFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no_such_routed_" + Guid.NewGuid().ToString("N") + ".kicad_pcb");
        var json = "{\"ok\":true,\"out\":\"" + missing.Replace("\\", "\\\\") + "\",\"tracks\":5,\"vias\":0,\"unconnected\":0}";
        var r = RouteResult.Parse(json, "", 0, null, missing);

        Assert.False(r.Ok);
        Assert.Null(r.RoutedPcbPath);
        Assert.Contains(r.Notes, n => n.Contains("no routed .kicad_pcb"));
    }

    [Fact]
    public void Parse_ErrorJson_IsFailureWithNote()
    {
        var r = RouteResult.Parse("{\"ok\":false,\"error\":\"pcbnew has no ImportSpecctraSES binding\"}", "", 1, null, "out.kicad_pcb");
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("ImportSpecctraSES"));
    }

    [Fact]
    public void Parse_NonZeroExit_NoJson_FallsBackToStderr()
    {
        var r = RouteResult.Parse("", "ImportError: No module named pcbnew", 1, null, "out.kicad_pcb");
        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("pcbnew"));
    }

    [Fact]
    public void Parse_ToleratesLeadingLogLines_BeforeJson()
    {
        var tmp = TempPcb();
        try
        {
            var stdout = "loading pcbnew...\nimporting SES\n{\"ok\":true,\"out\":\"" +
                         tmp.Replace("\\", "\\\\") + "\",\"tracks\":7,\"vias\":0,\"unconnected\":0}";
            var r = RouteResult.Parse(stdout, "", 0, null, tmp);
            Assert.True(r.Ok);
            Assert.Equal(tmp, r.RoutedPcbPath);
            Assert.Equal(7, r.TrackCount);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Parse_FoldsRouterLog_IntoNotes()
    {
        var tmp = TempPcb();
        try
        {
            var json = "{\"ok\":true,\"out\":\"" + tmp.Replace("\\", "\\\\") +
                       "\",\"tracks\":12,\"vias\":2,\"unconnected\":0}";
            var routerLog = "INFO starting autoroute\nINFO routing completed, 0 incomplete";
            var r = RouteResult.Parse(json, "", 0, routerLog, tmp);

            Assert.True(r.Ok);
            Assert.Contains(r.Notes, n => n.StartsWith("FreeRouting:") && n.Contains("completed"));
        }
        finally { File.Delete(tmp); }
    }
}

// ---- PcbRouter.RouteAsync degradation + embedded scripts -----------------------------------------

public class PcbRouterTests
{
    private static bool FullyAvailable() =>
        Foundry.Core.Pcb.KiCadInstaller.Locate() is not null
        && FreeRoutingInstaller.LocateJava() is not null
        && FreeRoutingInstaller.JarPresent;

    [Fact]
    public async Task RouteAsync_ReturnsNotInstalled_WhenToolchainAbsent()
    {
        // KiCad / Java / jar are all absent here — assert graceful degradation, never a throw.
        if (FullyAvailable()) return;   // guard: real toolchain present, skip

        var tmp = Path.Combine(Path.GetTempPath(), "foundry_route_in_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(tmp, "(kicad_pcb)");
        try
        {
            var r = await PcbRouter.RouteAsync(tmp);
            Assert.False(r.Installed);
            Assert.False(r.Ok);
            Assert.Null(r.RoutedPcbPath);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task RouteAsync_NotInstalled_TakesPrecedenceOverMissingInput()
    {
        // Even with a bogus input path, the not-installed gate short-circuits first (no throw).
        if (FullyAvailable()) return;
        var r = await PcbRouter.RouteAsync("Z:/does/not/exist.kicad_pcb");
        Assert.False(r.Installed);
        Assert.False(r.Ok);
    }

    [Fact]
    public void ReadScript_ReturnsEmbeddedExportDsnPython()
    {
        var script = PcbRouter.ReadScript("Foundry.Core.Pcb.KiCadScripts.export_dsn.py");
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("pcbnew", script);
    }

    [Fact]
    public void ReadScript_ReturnsEmbeddedImportSesPython()
    {
        var script = PcbRouter.ReadScript("Foundry.Core.Pcb.KiCadScripts.import_ses.py");
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("ImportSpecctraSES", script);
    }

    [Fact]
    public void ReadScript_MissingResource_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => PcbRouter.ReadScript("Foundry.Core.Pcb.KiCadScripts.nope.py"));
    }
}
