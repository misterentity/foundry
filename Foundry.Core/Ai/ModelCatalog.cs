namespace Foundry.Core.Ai;

public sealed record ClaudeModel(string Id, string DisplayName, string Note);

/// <summary>
/// Curated fallback list of public Claude models (PRD §8.9), used offline or when the
/// live <c>GET /v1/models</c> call fails. Ordered most → least capable.
/// </summary>
public static class ModelCatalog
{
    /// <summary>Generic fallback model (offline / when no model is specified): fast + strong.</summary>
    public const string DefaultModelId = "claude-sonnet-4-6";

    /// <summary>Default model for full project GENERATION — the most capable, for complex designs + long
    /// structured JSON (matches the shipped "Opus 4.8" framing). Chat/edits use the faster <see cref="DefaultModelId"/>.</summary>
    public const string GenerationModelId = "claude-opus-4-8";

    public static readonly IReadOnlyList<ClaudeModel> Fallback = new[]
    {
        new ClaudeModel("claude-opus-4-8",          "Claude Opus 4.8",   "Most capable; best for complex full designs + long structured JSON"),
        new ClaudeModel("claude-sonnet-4-6",        "Claude Sonnet 4.6", "Recommended default; fast + strong"),
        new ClaudeModel("claude-haiku-4-5-20251001","Claude Haiku 4.5",  "Fastest/cheapest; good for small chat edits"),
    };
}
