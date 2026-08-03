using System.Text.Json;

namespace Foundry.Core.Project;

/// <summary>
/// Loads/saves a Project as a single JSON document (PRD §6, F8). Contains no
/// secrets — API keys live in Windows Credential Manager, never in the file.
/// </summary>
public static class ProjectStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Persist a project. Atomic: this wrote in place, and WriteAllText truncates the destination before
    /// writing, so a crash in that window left an empty or half-written file — and DeleteById takes the
    /// revision history with the project, so there was nothing to restore from either.
    /// </summary>
    public static void Save(Project project, string path) =>
        AtomicFile.WriteAllText(path, Serialize(project));

    /// <summary>Load a project, falling back to the .bak if the main file is missing or not valid JSON.</summary>
    public static Project Load(string path)
    {
        var json = AtomicFile.ReadAllText(path, IsLoadable)
            ?? throw new FileNotFoundException($"No readable project at {path} (and no usable backup).", path);
        return Deserialize(json);
    }

    /// <summary>Cheap "is this a whole project file" test for the backup fallback — a truncated write is still text.</summary>
    private static bool IsLoadable(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { return JsonSerializer.Deserialize<Project>(json, Options) is not null; }
        catch (JsonException) { return false; }
    }

    public static string Serialize(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project Deserialize(string json) =>
        JsonSerializer.Deserialize<Project>(json, Options)
            ?? throw new InvalidDataException("Project JSON deserialized to null.");

    // ---- local project library (%AppData%/Foundry/projects) ----

    public static string LibraryDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Foundry", "projects");

    private static string PathFor(string id) =>
        System.IO.Path.Combine(LibraryDir, Sanitize(id) + ".json");

    private static string Sanitize(string id)
    {
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) id = id.Replace(ch, '_');
        id = id.Replace("..", "_");                    // defense-in-depth: no path traversal
        return string.IsNullOrWhiteSpace(id) ? "project" : id;
    }

    /// <summary>Filesystem-safe form of a project id (for revision/sidecar folders).</summary>
    public static string SafeId(string id) => Sanitize(id);

    /// <summary>Persist a project to the library, stamping an id/timestamp if missing.</summary>
    public static void SaveToLibrary(Project project)
    {
        if (string.IsNullOrWhiteSpace(project.Id)) project.Id = "p_" + Guid.NewGuid().ToString("N")[..8];
        project.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        Save(project, PathFor(project.Id));
        Diagnostics.AppLog.Info("project", $"saved “{project.Title}” to library ({project.Id})");
    }

    public static Project? LoadById(string id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? Load(path) : null;
    }

    public static void DeleteById(string id)
    {
        try
        {
            // Takes the .bak too — otherwise a deleted project quietly comes back on the next load.
            AtomicFile.Delete(PathFor(id));
            RevisionStore.DeleteAll(id);   // don't leave history for the next project with this id to inherit
            Diagnostics.AppLog.Info("project", $"deleted {id} from library");
        }
        catch { /* best effort */ }
    }

    /// <summary>Library rows, newest first. Skips any unreadable files.</summary>
    public static List<ProjectSummary> ListSummaries()
    {
        var list = new List<ProjectSummary>();
        if (!Directory.Exists(LibraryDir)) return list;
        // Explicit extension check: the shell's wildcard matching can also hit .json.bak / .json.tmp, which
        // would list a project twice — once from its backup.
        foreach (var file in Directory.EnumerateFiles(LibraryDir, "*.json")
                     .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var p = Load(file);
                list.Add(new ProjectSummary
                {
                    Id = p.Id, Title = p.Title, Prompt = p.Prompt, Updated = p.Updated,
                    Parts = p.Kpis.Parts, Status = p.Validation, Cost = p.Kpis.Cost,
                });
            }
            catch { /* skip corrupt */ }
        }
        return list.OrderByDescending(s => s.Updated).ToList();
    }
}

/// <summary>Lightweight row for the project-library screen (RECENT_PROJECTS).</summary>
public sealed class ProjectSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Updated { get; set; } = "";
    public int Parts { get; set; }
    /// <summary>ok | warn | fail</summary>
    public string Status { get; set; } = "ok";
    public double Cost { get; set; }
    public bool Current { get; set; }

    public string CostText => $"${Cost:0.00}";
    public string PartsText => $"{Parts} parts";
    public string StatusText => Status?.ToUpperInvariant() switch { "FAIL" => "FAIL", "WARN" => "WARN", _ => "PASS" };
}
