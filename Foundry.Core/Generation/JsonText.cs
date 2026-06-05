using System.Text.Json;

namespace Foundry.Core.Generation;

/// <summary>
/// Shared helper for pulling a JSON object out of an AI response that may carry markdown fences or leading
/// prose. The single hardened implementation (null-guard + outermost-brace slice + parse-validate) replaces
/// the two divergent private copies that used to live in <see cref="ProjectGenerator"/> and
/// <see cref="Foundry.Core.Pcb.PcbPlanner"/> (one lacked the null-guard, the other never validated the slice).
/// </summary>
public static class JsonText
{
    /// <summary>The outermost <c>{…}</c> object in <paramref name="raw"/> if it parses as JSON, else null.</summary>
    public static string? Extract(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var slice = raw[start..(end + 1)];
        try { using var _ = JsonDocument.Parse(slice); return slice; }
        catch { return null; }
    }
}
