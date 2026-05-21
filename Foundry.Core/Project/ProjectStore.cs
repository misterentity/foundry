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

    public static void Save(Project project, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(project));
    }

    public static Project Load(string path) => Deserialize(File.ReadAllText(path));

    public static string Serialize(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project Deserialize(string json) =>
        JsonSerializer.Deserialize<Project>(json, Options)
            ?? throw new InvalidDataException("Project JSON deserialized to null.");
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
}
