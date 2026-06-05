using System.Text.RegularExpressions;

namespace Foundry.Core.Cad;

/// <summary>
/// Guards the OpenSCAD render path against file-access directives in AI-authored (or prompt-injected) scripts.
/// OpenSCAD's <c>include &lt;…&gt;</c>, <c>use &lt;…&gt;</c>, <c>import(…)</c> and <c>surface(…)</c> read external
/// files — a hostile script could exfiltrate or read arbitrary local paths through the renderer. A legitimate
/// parametric enclosure never needs them, so any occurrence is refused before the script reaches OpenSCAD.
/// (AI-SCAD is a render-only preview; the EXPORT path is the deterministic schema→mesh build.)
/// </summary>
public static class ScadSafety
{
    private static readonly (string Token, Regex Rx)[] Dangerous =
    {
        ("include", new Regex(@"\binclude\s*<", RegexOptions.IgnoreCase)),
        ("use",     new Regex(@"\buse\s*<", RegexOptions.IgnoreCase)),
        ("import",  new Regex(@"\bimport\s*\(", RegexOptions.IgnoreCase)),
        ("surface", new Regex(@"\bsurface\s*\(", RegexOptions.IgnoreCase)),
    };

    /// <summary>The first file-access directive found, or null when the script is safe to render.</summary>
    public static string? FindUnsafeDirective(string? scad)
    {
        if (string.IsNullOrEmpty(scad)) return null;
        foreach (var (token, rx) in Dangerous)
            if (rx.IsMatch(scad)) return token;
        return null;
    }

    public static bool IsSafe(string? scad) => FindUnsafeDirective(scad) is null;
}
