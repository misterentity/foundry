namespace Foundry.Core.Project;

/// <summary>
/// Reusable full-project templates (PRD v2 G13): save a finished design as a template, then start a new
/// project from it instantly (no AI call). Stored as Project JSON under %AppData%/Foundry/templates/.
/// </summary>
public static class TemplateStore
{
    public static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Foundry", "templates");

    private static string PathFor(string id) =>
        System.IO.Path.Combine(Dir, ProjectStore.SafeId(id) + ".json");

    /// <summary>Snapshot the current project as a named template.</summary>
    public static string Save(Project project, string name)
    {
        System.IO.Directory.CreateDirectory(Dir);
        var copy = ProjectStore.Deserialize(ProjectStore.Serialize(project)); // deep copy
        copy.Id = "t_" + Guid.NewGuid().ToString("N")[..8];
        copy.Title = string.IsNullOrWhiteSpace(name) ? project.Title : name.Trim();
        copy.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        copy.Chat = new();                  // templates start a fresh conversation
        ProjectStore.Save(copy, PathFor(copy.Id));
        Diagnostics.AppLog.Info("template", $"saved template “{copy.Title}” ({copy.Id})");
        return copy.Id;
    }

    public static List<ProjectSummary> List()
    {
        var list = new List<ProjectSummary>();
        if (!System.IO.Directory.Exists(Dir)) return list;
        foreach (var file in System.IO.Directory.EnumerateFiles(Dir, "*.json"))
        {
            try
            {
                var p = ProjectStore.Load(file);
                list.Add(new ProjectSummary { Id = p.Id, Title = p.Title, Prompt = p.Prompt, Updated = p.Updated, Parts = p.Kpis.Parts, Status = p.Validation, Cost = p.Kpis.Cost });
            }
            catch { /* skip corrupt */ }
        }
        return list.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Load a template as a NEW project (fresh id), ready to open + save to the library.</summary>
    public static Project? Load(string id)
    {
        var path = PathFor(id);
        if (!System.IO.File.Exists(path)) return null;
        var p = ProjectStore.Load(path);
        p.Id = "p_" + Guid.NewGuid().ToString("N")[..8];   // a template instantiates into a fresh project
        return p;
    }

    public static void Delete(string id)
    {
        try { var p = PathFor(id); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
    }
}
