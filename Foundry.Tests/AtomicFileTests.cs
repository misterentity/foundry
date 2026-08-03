using Foundry.Core.Project;

namespace Foundry.Tests;

// ProjectStore, AppConfig and RevisionStore all wrote in place. File.WriteAllText TRUNCATES the destination
// before writing the new bytes, so losing power, filling the disk, or being killed by the updater inside
// that window left the file empty or half-written. For the library that means the project is gone -- and
// DeleteById takes the .rev history with a project, so nothing was left to restore from either.
public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foundry-atomic-" + Guid.NewGuid().ToString("N")[..8]);

    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string P(string name = "f.json") => Path.Combine(_dir, name);

    [Fact]
    public void WritingANewFile_JustWritesIt()
    {
        AtomicFile.WriteAllText(P(), "hello");
        Assert.Equal("hello", File.ReadAllText(P()));
        Assert.False(File.Exists(P() + ".bak"));      // nothing to back up yet
    }

    [Fact]
    public void OverwritingKeepsThePreviousContentsAsABackup()
    {
        AtomicFile.WriteAllText(P(), "v1");
        AtomicFile.WriteAllText(P(), "v2");

        Assert.Equal("v2", File.ReadAllText(P()));
        Assert.Equal("v1", File.ReadAllText(P() + AtomicFile.BackupSuffix));
    }

    [Fact]
    public void NoTempFileIsLeftBehind()
    {
        AtomicFile.WriteAllText(P(), "v1");
        AtomicFile.WriteAllText(P(), "v2");
        Assert.False(File.Exists(P() + ".tmp"));
    }

    [Fact]
    public void ItCreatesMissingDirectories()
    {
        var nested = Path.Combine(_dir, "a", "b", "f.json");
        AtomicFile.WriteAllText(nested, "x");
        Assert.Equal("x", File.ReadAllText(nested));
    }

    // ---- recovery ----

    [Fact]
    public void ReadPrefersTheMainFile()
    {
        AtomicFile.WriteAllText(P(), "v1");
        AtomicFile.WriteAllText(P(), "v2");
        Assert.Equal("v2", AtomicFile.ReadAllText(P()));
    }

    [Fact]
    public void AHalfWrittenFile_FallsBackToTheBackup()
    {
        AtomicFile.WriteAllText(P(), "{\"ok\":1}");
        AtomicFile.WriteAllText(P(), "{\"ok\":2}");
        File.WriteAllText(P(), "{\"ok\":");                    // simulate the truncated write

        Assert.Equal("{\"ok\":1}", AtomicFile.ReadAllText(P(), IsJson));
    }

    [Fact]
    public void AnEmptyFile_FallsBackToTheBackup()
    {
        AtomicFile.WriteAllText(P(), "v1");
        AtomicFile.WriteAllText(P(), "v2");
        File.WriteAllText(P(), "");

        Assert.Equal("v1", AtomicFile.ReadAllText(P()));
    }

    [Fact]
    public void AMissingFileWithABackup_StillReads()
    {
        AtomicFile.WriteAllText(P(), "v1");
        AtomicFile.WriteAllText(P(), "v2");
        File.Delete(P());

        Assert.Equal("v1", AtomicFile.ReadAllText(P()));
    }

    [Fact]
    public void NothingReadableAtAll_ReturnsNull()
    {
        Assert.Null(AtomicFile.ReadAllText(P()));

        AtomicFile.WriteAllText(P(), "garbage");
        Assert.Null(AtomicFile.ReadAllText(P(), IsJson));      // main invalid, no backup exists
    }

    // A delete that leaves the .bak means the project quietly returns on the next load.
    [Fact]
    public void DeleteRemovesTheBackupToo()
    {
        AtomicFile.WriteAllText(P(), "v1");
        AtomicFile.WriteAllText(P(), "v2");
        AtomicFile.Delete(P());

        Assert.False(File.Exists(P()));
        Assert.False(File.Exists(P() + AtomicFile.BackupSuffix));
        Assert.Null(AtomicFile.ReadAllText(P()));
    }

    private static bool IsJson(string s)
    {
        try { System.Text.Json.JsonDocument.Parse(s); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}

// The store-level behaviour: a project must survive a torn write, and a deleted one must stay deleted.
public class ProjectStoreDurabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foundry-store-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectStoreDurabilityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static Project Proj(string title) => new() { Id = "p_test", Title = title };

    // Two distinct guarantees, and it matters which is which.
    //
    // 1. A write torn INSIDE AtomicFile never touches the live file: the bytes go to a temp file and are
    //    swapped in only once complete, so the current version survives intact.
    [Fact]
    public void AFailedWriteLeavesTheLiveFileIntact()
    {
        var path = Path.Combine(_dir, "p.json");
        ProjectStore.Save(Proj("good"), path);

        // A directory where the temp file must go cannot itself be written as a file.
        Directory.CreateDirectory(path + ".tmp");
        Assert.ThrowsAny<Exception>(() => ProjectStore.Save(Proj("doomed"), path));

        Assert.Equal("good", ProjectStore.Load(path).Title);
        Directory.Delete(path + ".tmp");
    }

    // 2. If the live file is destroyed by something OUTSIDE this code (disk fault, a stray editor, the
    //    old in-place writer), the .bak recovers the version before it -- not the one just lost. That is
    //    what one generation of backup can honestly offer, and it beats an empty file.
    [Fact]
    public void ExternallyCorruptedFile_RecoversThePreviousVersion()
    {
        var path = Path.Combine(_dir, "p.json");
        ProjectStore.Save(Proj("first"), path);
        ProjectStore.Save(Proj("second"), path);

        File.WriteAllText(path, "{\"title\":\"thi");     // truncated on disk

        Assert.Equal("first", ProjectStore.Load(path).Title);
    }

    [Fact]
    public void ARoundTripIsUnchanged()
    {
        var path = Path.Combine(_dir, "p.json");
        ProjectStore.Save(Proj("Cap. Soil Moisture Sentinel"), path);
        Assert.Equal("Cap. Soil Moisture Sentinel", ProjectStore.Load(path).Title);
    }

    [Fact]
    public void NothingReadable_ThrowsRatherThanReturningAnEmptyProject()
    {
        var path = Path.Combine(_dir, "missing.json");
        Assert.Throws<FileNotFoundException>(() => ProjectStore.Load(path));
    }
}
