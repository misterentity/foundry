using System.Text.Json;

namespace Foundry.Core.Sidecar;

/// <summary>
/// Decides whether a listener answering <c>/health</c> is the sidecar THIS process spawned.
///
/// <para>
/// It used to be enough for the body to contain <c>"service"</c> and <c>"foundry-cad"</c> — a 60-byte
/// constant with no version, no build id and no secret. Any Foundry already running on the machine
/// answers exactly that, so a freshly built app would silently adopt an older installed sidecar and
/// render geometry from code that is not in the working tree, while the status bar read "connected".
/// That is a correctness hole and a verification hole: it makes "I ran it and it looked right"
/// meaningless.
/// </para>
///
/// <para>
/// The fix is a per-spawn token passed to the child through its environment (not argv, which is visible
/// in the process list). A process we did not start cannot know it, so adoption is impossible by
/// construction rather than by convention.
/// </para>
///
/// Pure and unit-testable: no I/O.
/// </summary>
public static class SidecarIdentity
{
    /// <summary>Environment variable carrying the per-spawn token to the child.</summary>
    public const string TokenVar = "FOUNDRY_SIDECAR_TOKEN";

    /// <summary>
    /// An explicit developer override — when set, that URL is trusted without a token so a manually
    /// started <c>server.py</c> can still be used. Opt-in and visible, unlike silent adoption.
    /// </summary>
    public const string UrlVar = "FOUNDRY_SIDECAR_URL";

    public static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// True when <paramref name="healthBody"/> identifies as Foundry's CAD sidecar AND carries
    /// <paramref name="expectedToken"/>. An empty expected token means "trust any Foundry sidecar" and is
    /// only used for the explicit <see cref="UrlVar"/> override.
    /// </summary>
    public static bool Accept(string? healthBody, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(healthBody)) return false;

        string? service = null, token = null;
        try
        {
            using var doc = JsonDocument.Parse(healthBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (doc.RootElement.TryGetProperty("service", out var s) && s.ValueKind == JsonValueKind.String)
                service = s.GetString();
            if (doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                token = t.GetString();
        }
        catch (JsonException) { return false; }

        if (!string.Equals(service, "foundry-cad", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(expectedToken)) return true;   // explicit override path

        // Ordinal, length-checked comparison — a stale sidecar answers with a different token (or none).
        return !string.IsNullOrEmpty(token) && string.Equals(token, expectedToken, StringComparison.Ordinal);
    }
}
