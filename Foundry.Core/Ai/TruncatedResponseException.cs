namespace Foundry.Core.Ai;

/// <summary>
/// Thrown when the model stopped because it hit the output-token cap (stop_reason=max_tokens), so the
/// response is INCOMPLETE — any JSON/firmware it was producing is cut off. Surfacing this as an exception
/// (rather than silently returning the partial text) guarantees no caller can mistake a truncated reply for
/// a complete one. <see cref="PartialText"/> is the incomplete output for diagnostics only.
/// </summary>
public sealed class TruncatedResponseException : Exception
{
    public string PartialText { get; }
    public int MaxTokens { get; }

    public TruncatedResponseException(string partialText, int maxTokens)
        : base($"Claude hit the {maxTokens}-token output cap and the response was cut off ({partialText.Length} chars) — " +
               "raise the output-token limit in Settings or simplify the request.")
    {
        PartialText = partialText;
        MaxTokens = maxTokens;
    }
}
