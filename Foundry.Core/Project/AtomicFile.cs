using System.Text;

namespace Foundry.Core.Project;

/// <summary>
/// Crash-safe file replacement for the stores that hold data the user cannot regenerate — the project
/// library, settings, and revision history.
///
/// <para>
/// Those all wrote in place with <c>File.WriteAllText</c>, which truncates the destination BEFORE writing
/// the new bytes. Lose power, hit a full disk, or get killed by the updater in that window and the file is
/// left empty or half-written: the project is gone, and for the library that also means the revision
/// history that could have restored it is orphaned.
/// </para>
///
/// <para>
/// Writes go to a temp file in the SAME directory (rename is only atomic within a volume), which is then
/// swapped in with <see cref="File.Replace(string,string,string)"/> so the previous contents survive as a
/// <c>.bak</c>. A reader that finds the main file missing or unusable can fall back to it.
/// </para>
/// </summary>
public static class AtomicFile
{
    public const string BackupSuffix = ".bak";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Replace <paramref name="path"/>'s contents atomically, keeping the old copy as .bak.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Same directory: File.Replace/Move are only atomic within one volume, and %TEMP% may be elsewhere.
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents, Utf8NoBom);

            if (File.Exists(path))
            {
                // Replace needs the backup on the same volume too. ignoreMetadataErrors keeps this working
                // on volumes that don't carry ACLs across (a network share or a FAT USB stick).
                File.Replace(tmp, path, path + BackupSuffix, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            // Never leave a stray .tmp behind to be mistaken for real data.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Read a file written by <see cref="WriteAllText"/>, falling back to its <c>.bak</c> when the main
    /// copy is missing or fails <paramref name="isValid"/>. Returns null when neither can be used.
    ///
    /// <para>
    /// The validity check is the caller's, because "not corrupt" means "parses as the thing I expect" —
    /// a truncated JSON file is perfectly readable text.
    /// </para>
    /// </summary>
    public static string? ReadAllText(string path, Func<string, bool>? isValid = null)
    {
        isValid ??= s => !string.IsNullOrWhiteSpace(s);

        foreach (var candidate in new[] { path, path + BackupSuffix })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate);
                if (!isValid(text))
                {
                    Diagnostics.AppLog.Warn("store", $"{Path.GetFileName(candidate)} is unusable — trying the backup.");
                    continue;
                }
                if (candidate != path)
                    Diagnostics.AppLog.Warn("store", $"recovered {Path.GetFileName(path)} from its backup.");
                return text;
            }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Warn("store", $"could not read {Path.GetFileName(candidate)}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Delete a file and the backup that shadows it, so a delete cannot be silently undone.</summary>
    public static void Delete(string path)
    {
        foreach (var p in new[] { path, path + BackupSuffix, path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }
}
