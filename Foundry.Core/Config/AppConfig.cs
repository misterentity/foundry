using System.Text.Json;
using Foundry.Core.Ai;

namespace Foundry.Core.Config;

/// <summary>
/// Non-secret app settings (PRD §8.9). Secrets (API keys) never live here — they go to Windows
/// Credential Manager. Persisted as JSON under %AppData%/Foundry/config.json.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Model used for full project generation. Defaults to Opus 5 — the most capable, for complex
    /// designs + long structured JSON; users can switch to a faster model in Settings.</summary>
    public string ModelId { get; set; } = ModelCatalog.GenerationModelId;
    /// <summary>Model used for chat iteration + validation fixes (kept fast/cheap vs. full generation).</summary>
    public string ChatModelId { get; set; } = ModelCatalog.DefaultModelId;
    public int MaxOutputTokens { get; set; } = 16384;
    public double Temperature { get; set; } = 1.0;
    /// <summary>Arduino C++ | MicroPython</summary>
    public string FirmwarePlatform { get; set; } = "Arduino C++";
    public string OutputFolder { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Foundry");
    /// <summary>STL | 3MF | STEP</summary>
    public string EnclosureFormat { get; set; } = "STL";
    public string Units { get; set; } = "mm";

    // Note: the update repo is intentionally NOT configurable — it is pinned to
    // AppInfo.DefaultUpdateOwner/Repo so a writable config can't repoint the updater (security).
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Foundry", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            // Falls back to the .bak when the settings file is truncated — otherwise a crash mid-save
            // silently resets every preference to defaults, including the configured output folder.
            var json = Project.AtomicFile.ReadAllText(path, IsLoadable);
            if (json is not null && JsonSerializer.Deserialize<AppConfig>(json) is { } cfg)
            {
                // The chosen model is persisted, so shipping a newer catalog does nothing on its own for an
                // install that has already run: it stays pinned to whatever it saved, even after that model
                // is retired. Migrate on load, preserving the tier the user picked.
                cfg.ModelId = ModelCatalog.Migrate(cfg.ModelId);
                // Only when one was actually chosen: a null ChatModelId means "use the fast default", and
                // migrating that would silently promote every chat edit to the expensive generation model.
                if (!string.IsNullOrWhiteSpace(cfg.ChatModelId))
                    cfg.ChatModelId = ModelCatalog.Migrate(cfg.ChatModelId);
                return cfg;
            }
        }
        catch { /* fall through to defaults */ }
        return new AppConfig();
    }

    private static bool IsLoadable(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { return JsonSerializer.Deserialize<AppConfig>(json) is not null; }
        catch (JsonException) { return false; }
    }

    public static void Save(AppConfig config, string? path = null) =>
        Project.AtomicFile.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(config, Opts));
}
