namespace Foundry.Core.Ai;

/// <summary>
/// Thin abstraction over the Anthropic Messages API (PRD §7). The real implementation
/// talks HTTPS; the stub returns canned data so the whole app runs without a key.
/// </summary>
public interface IAnthropicClient
{
    /// <summary>True when a usable API key is configured.</summary>
    bool HasKey { get; }

    /// <summary>
    /// Validates the key cheaply (PRD §8.9): <c>GET /v1/models</c>, no token spend.
    /// Returns the public model list on success.
    /// </summary>
    Task<ModelListResult> ListModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a single structured generation request and returns raw JSON text for the
    /// caller (the Pipeline) to parse + schema-validate before mutating the Project.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, string modelId, CancellationToken ct = default);
}

public sealed record ModelListResult(bool Ok, IReadOnlyList<ClaudeModel> Models, string? Error)
{
    public static ModelListResult Failure(string error) => new(false, ModelCatalog.Fallback, error);
}
