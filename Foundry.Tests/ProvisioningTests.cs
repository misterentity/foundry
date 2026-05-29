using Foundry.Core.Pcb;
using Foundry.Core.Toolchain;

namespace Foundry.Tests;

public class ToolchainProvisionerTests
{
    [Fact]
    public void Tools_ListsAllSixOptionalTools()
    {
        var ids = ToolchainProvisioner.Tools.Select(t => t.Id).ToArray();
        Assert.Equal(6, ids.Length);
        Assert.Contains(ToolId.ArduinoCli, ids);
        Assert.Contains(ToolId.OpenScad, ids);
        Assert.Contains(ToolId.Renode, ids);
        Assert.Contains(ToolId.FreeRouting, ids);
        Assert.Contains(ToolId.JavaJre, ids);
        Assert.Contains(ToolId.KiCad, ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Tools_AllHaveNonEmptyNameAndPurpose()
    {
        Assert.All(ToolchainProvisioner.Tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Purpose));
        });
    }

    [Theory]
    [InlineData(ToolId.ArduinoCli)]
    [InlineData(ToolId.OpenScad)]
    [InlineData(ToolId.Renode)]
    [InlineData(ToolId.FreeRouting)]
    [InlineData(ToolId.JavaJre)]
    [InlineData(ToolId.KiCad)]
    public void IsInstalled_ReturnsBool_NeverThrows(ToolId id)
    {
        // Pure-locate, no I/O side effects: must return a value either way.
        var ex = Record.Exception(() => ToolchainProvisioner.IsInstalled(id));
        Assert.Null(ex);
    }

    [Fact]
    public void GetStatus_MatchesDescriptor_AndIsInstalled()
    {
        foreach (var d in ToolchainProvisioner.Tools)
        {
            var s = ToolchainProvisioner.GetStatus(d.Id);
            Assert.Equal(d.Id, s.Id);
            Assert.Equal(d.Name, s.Name);
            Assert.Equal(d.Purpose, s.Purpose);
            Assert.Equal(ToolchainProvisioner.IsInstalled(d.Id), s.Installed);
            // Installed ⇔ a non-null resolved location.
            Assert.Equal(s.Installed, s.Location is not null);
        }
    }

    [Fact]
    public void Snapshot_CoversEveryTool_OncePerId()
    {
        var snap = ToolchainProvisioner.Snapshot();
        Assert.Equal(ToolchainProvisioner.Tools.Count, snap.Count);
        var snapIds = snap.Select(s => s.Id).OrderBy(x => x).ToArray();
        var toolIds = ToolchainProvisioner.Tools.Select(t => t.Id).OrderBy(x => x).ToArray();
        Assert.Equal(toolIds, snapIds);
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalled_IsNoOp_AndReportsInstalled()
    {
        // Find a tool that's present on this machine (KiCad/Java are installed here) so the idempotent
        // path runs with no network call. Skip if nothing is installed (clean CI box).
        var present = ToolchainProvisioner.Tools.FirstOrDefault(t => ToolchainProvisioner.IsInstalled(t.Id));
        if (present is null) return;

        var stages = new List<string>();
        var progress = new Progress<ToolProgress>(p => stages.Add(p.Stage));
        var status = await ToolchainProvisioner.InstallAsync(present.Id, progress);

        Assert.True(status.Installed);
        Assert.Equal(present.Id, status.Id);
        Assert.NotNull(status.Location);
    }
}

public class FreeRoutingInstallerProvisioningTests
{
    [Fact]
    public void JavaToolsDir_IsUnderLocalAppDataFoundryTools()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expected = Path.Combine(local, "Foundry", "tools", "java");
        Assert.Equal(expected, FreeRoutingInstaller.JavaToolsDir);
    }

    [Fact]
    public void JarPath_IsUnderFreeRoutingToolsDir()
    {
        Assert.Equal(FreeRoutingInstaller.ToolsDir, Path.GetDirectoryName(FreeRoutingInstaller.JarPath));
        Assert.EndsWith(".jar", FreeRoutingInstaller.JarPath);
    }

    [Fact]
    public void JreUrl_IsTemurin25_Windows_X64_Jre()
    {
        var url = FreeRoutingInstaller.JreUrl;
        Assert.Contains("api.adoptium.net", url);
        Assert.Contains("/25/", url);        // pinned Java 25 (FreeRouting 2.2.4 jar = class file 69)
        Assert.Contains("windows", url);
        Assert.Contains("x64", url);
        Assert.Contains("jre", url);
    }

    [Fact]
    public void JavaPresent_AgreesWithLocateJava()
    {
        Assert.Equal(FreeRoutingInstaller.LocateJava() is not null, FreeRoutingInstaller.JavaPresent);
    }

    [Fact]
    public void JarPresent_AgreesWithJarPathExistence()
    {
        Assert.Equal(File.Exists(FreeRoutingInstaller.JarPath), FreeRoutingInstaller.JarPresent);
    }

    [Fact]
    public void LocateJava_PrefersAppLocalJre_OverSystemJava()
    {
        // App-local JRE must win over JAVA_HOME/PATH. Drop a fake java.exe into a temp tools dir and
        // assert LocateJava returns it. We can't repoint JavaToolsDir (static), so we exercise the same
        // app-local-first ordering by planting a java.exe under the real JavaToolsDir and cleaning up.
        var planted = Path.Combine(FreeRoutingInstaller.JavaToolsDir,
            "test_jre_" + Guid.NewGuid().ToString("N")[..8], "bin");
        var fakeJava = Path.Combine(planted, "java.exe");
        var preexisting = Directory.Exists(FreeRoutingInstaller.JavaToolsDir);
        try
        {
            Directory.CreateDirectory(planted);
            File.WriteAllBytes(fakeJava, new byte[] { 0x4D, 0x5A });   // "MZ" header, harmless stub

            var located = FreeRoutingInstaller.LocateJava();
            Assert.NotNull(located);
            // The resolved java.exe must come from the app-local tools dir, not system Java.
            Assert.StartsWith(FreeRoutingInstaller.JavaToolsDir, located!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(fakeJava)!)!, recursive: true); } catch { }
            // If we created the tools dir solely for this test, remove it to avoid masking real state.
            if (!preexisting) { try { Directory.Delete(FreeRoutingInstaller.JavaToolsDir, recursive: true); } catch { } }
        }
    }
}

public class KiCadInstallerProvisioningTests
{
    [Fact]
    public void WingetId_IsKiCadPackage()
    {
        Assert.Equal("KiCad.KiCad", KiCadInstaller.WingetId);
    }

    [Fact]
    public void FallbackExeUrl_IsWindowsNsisInstaller()
    {
        Assert.StartsWith("https://", KiCadInstaller.FallbackExeUrl);
        Assert.EndsWith(".exe", KiCadInstaller.FallbackExeUrl);
        Assert.Contains("x86_64", KiCadInstaller.FallbackExeUrl);
    }

    [Fact]
    public void Locate_NeverThrows_AndIsInstalledAgrees()
    {
        KiCadInstaller.Install? install = null;
        var ex = Record.Exception(() => install = KiCadInstaller.Locate());
        Assert.Null(ex);
        Assert.Equal(install is not null, KiCadInstaller.IsInstalled);
    }
}
