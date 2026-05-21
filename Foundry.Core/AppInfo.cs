namespace Foundry.Core;

/// <summary>App identity / version, single source of truth for the updater and UI.</summary>
public static class AppInfo
{
    public const string Version = "0.4.17";

    /// <summary>Default GitHub repo the updater checks for releases (overridable in Settings).</summary>
    public const string DefaultUpdateOwner = "misterentity";
    public const string DefaultUpdateRepo = "foundry";
}
