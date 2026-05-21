namespace Foundry.Core.Ai;

/// <summary>
/// Offline stand-in for <see cref="IAnthropicClient"/> (PRD F9). Lets the UI run with no
/// API key: model list falls back to the curated catalog and generation returns canned
/// JSON. Swapped for the real client once a valid key is present.
/// </summary>
public sealed class StubAnthropicClient : IAnthropicClient
{
    public bool HasKey => false;

    public Task<ModelListResult> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(new ModelListResult(true, ModelCatalog.Fallback, null));

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, string modelId, CancellationToken ct = default) =>
        Task.FromResult("{}");
}
