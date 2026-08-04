using System.Reflection;

namespace Foundry.Core;

/// <summary>App identity / version, single source of truth for the updater and UI.</summary>
public static class AppInfo
{
    /// <summary>
    /// The running application version, READ from the assembly rather than declared a second time.
    ///
    /// <para>
    /// This was <c>const string Version = "2.6.0"</c> while Foundry.App.csproj stamped the exe 2.7.1 — the
    /// "single source of truth" in the comment above was, in fact, the drifted copy. That is not cosmetic:
    /// <c>App.xaml.cs</c> passes this value to the update check, so a frozen constant makes the updater
    /// compare the wrong version against the latest release and offer an "update" the user already has,
    /// every launch, forever. The version now comes from <c>Directory.Build.props</c> via the compiler, so
    /// the exe metadata and this value cannot disagree again.
    /// </para>
    /// </summary>
    public static string Version { get; } = Resolve();

    /// <summary>Default GitHub repo the updater checks for releases (overridable in Settings).</summary>
    public const string DefaultUpdateOwner = "misterentity";
    public const string DefaultUpdateRepo = "foundry";

    private static string Resolve()
    {
        var asm = typeof(AppInfo).Assembly;

        // InformationalVersion carries the full "2.7.1+<sha>"; the SHA belongs in logs, not the UI.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            var v = (plus > 0 ? info[..plus] : info).Trim();
            if (v.Length > 0) return v;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
