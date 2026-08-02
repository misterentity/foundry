using System.Text.Json;

namespace Foundry.Core.Project;

/// <summary>
/// Per-project version history. Each generate / chat-edit / validation-fix / rebuild snapshots the
/// full Project JSON under %AppData%/Foundry/projects/{id}.rev/, so any version can be restored.
/// </summary>
public static class RevisionStore
{
    private const int MaxRevisions = 40;

    private static string RevDir(string id) =>
        System.IO.Path.Combine(ProjectStore.LibraryDir, ProjectStore.SafeId(id) + ".rev");

    /// <summary>Snapshot the current project state with a human label. No-op for an unsaved (id-less) project.</summary>
    public static void Capture(Project project, string label)
    {
        if (string.IsNullOrWhiteSpace(project.Id)) return;
        try
        {
            var dir = RevDir(project.Id);
            System.IO.Directory.CreateDirectory(dir);
            var rev = new ProjectRevision
            {
                RevId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"),
                Label = string.IsNullOrWhiteSpace(label) ? "edit" : label.Trim(),
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Json = ProjectStore.Serialize(project),
            };
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, rev.RevId + ".json"),
                JsonSerializer.Serialize(rev, ProjectStore.Options));
            Prune(dir);
            Diagnostics.AppLog.Info("revision", $"captured “{rev.Label}” for {project.Id}");
        }
        catch (Exception ex) { Diagnostics.AppLog.Warn("revision", $"capture failed: {ex.Message}"); }
    }

    /// <summary>Revisions for a project, newest first.</summary>
    public static List<RevisionSummary> List(string id)
    {
        var dir = RevDir(id);
        var list = new List<RevisionSummary>();
        if (!System.IO.Directory.Exists(dir)) return list;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rev = JsonSerializer.Deserialize<ProjectRevision>(System.IO.File.ReadAllText(file), ProjectStore.Options);
                if (rev is not null) list.Add(new RevisionSummary { RevId = rev.RevId, Label = rev.Label, At = rev.At });
            }
            catch { /* skip corrupt */ }
        }
        return list.OrderByDescending(r => r.RevId).ToList();
    }

    /// <summary>
    /// Remove every revision snapshot for a project. Called when the project is deleted so its history
    /// doesn't outlive it: an orphaned <c>&lt;id&gt;.rev</c> folder is silently adopted by the next project
    /// that happens to be created with the same id, presenting another design's history as your own.
    /// </summary>
    public static void DeleteAll(string id)
    {
        try
        {
            var dir = RevDir(id);
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { Diagnostics.AppLog.Warn("revision", $"couldn't remove revisions for {id}: {ex.Message}"); }
    }

    /// <summary>Load a snapshot back into a Project (for restore).</summary>
    public static Project? Load(string id, string revId)
    {
        try
        {
            var path = System.IO.Path.Combine(RevDir(id), revId + ".json");
            if (!System.IO.File.Exists(path)) return null;
            var rev = JsonSerializer.Deserialize<ProjectRevision>(System.IO.File.ReadAllText(path), ProjectStore.Options);
            return rev is null ? null : ProjectStore.Deserialize(rev.Json);
        }
        catch { return null; }
    }

    private static void Prune(string dir)
    {
        var files = System.IO.Directory.EnumerateFiles(dir, "*.json").OrderByDescending(f => f).Skip(MaxRevisions);
        foreach (var f in files) try { System.IO.File.Delete(f); } catch { }
    }
}

public sealed class ProjectRevision
{
    public string RevId { get; set; } = "";
    public string Label { get; set; } = "";
    public string At { get; set; } = "";
    public string Json { get; set; } = "";
}

public sealed class RevisionSummary
{
    public string RevId { get; set; } = "";
    public string Label { get; set; } = "";
    public string At { get; set; } = "";
}
