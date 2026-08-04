namespace Foundry.Core.Ai;

public sealed record ClaudeModel(string Id, string DisplayName, string Note);

/// <summary>
/// Curated fallback list of public Claude models (PRD §8.9), used offline or when the
/// live <c>GET /v1/models</c> call fails. Ordered most → least capable.
/// </summary>
public static class ModelCatalog
{
    /// <summary>Generic fallback model (offline / when no model is specified): fast + strong.</summary>
    public const string DefaultModelId = "claude-sonnet-5";

    /// <summary>Default model for full project GENERATION — the most capable, for complex designs + long
    /// structured JSON. Chat/edits use the faster <see cref="DefaultModelId"/>.</summary>
    public const string GenerationModelId = "claude-opus-5";

    public static readonly IReadOnlyList<ClaudeModel> Fallback = new[]
    {
        new ClaudeModel("claude-opus-5",             "Claude Opus 5",     "Most capable; best for complex full designs + long structured JSON"),
        new ClaudeModel("claude-sonnet-5",           "Claude Sonnet 5",   "Recommended default; fast + strong"),
        new ClaudeModel("claude-haiku-4-5-20251001", "Claude Haiku 4.5",  "Fastest/cheapest; good for small chat edits"),
    };

    /// <summary>
    /// Map a model id that is no longer offered onto its current equivalent.
    ///
    /// <para>
    /// The selected model is PERSISTED in config.json, so bumping this catalog does nothing on its own for
    /// anyone who has already run the app — an install pinned to <c>claude-opus-4-7</c> keeps asking for
    /// <c>claude-opus-4-7</c> forever, and the model picker shows a value that is not in the list.
    /// </para>
    ///
    /// <para>
    /// The mapping preserves the TIER the user chose: a retired Opus becomes the current Opus, a retired
    /// Sonnet becomes the current Sonnet. Someone who deliberately picked the cheap fast model does not get
    /// silently upgraded to the expensive one. Anything already current, or unrecognised (a model this build
    /// has never heard of, which may simply be newer), is left exactly as it is.
    /// </para>
    /// </summary>
    public static string Migrate(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return GenerationModelId;

        var trimmed = id.Trim();
        if (Fallback.Any(m => m.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) return trimmed;

        // Only retire ids we know are gone: the 4.x families this app previously shipped.
        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("claude-opus-4", StringComparison.Ordinal)) return "claude-opus-5";
        if (lower.StartsWith("claude-sonnet-4", StringComparison.Ordinal)) return "claude-sonnet-5";
        if (lower.StartsWith("claude-opus-3", StringComparison.Ordinal)) return "claude-opus-5";
        if (lower.StartsWith("claude-sonnet-3", StringComparison.Ordinal)) return "claude-sonnet-5";
        if (lower.StartsWith("claude-haiku-3", StringComparison.Ordinal)) return "claude-haiku-4-5-20251001";

        return trimmed;
    }
}
