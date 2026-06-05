using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foundry.Core.Ai;

/// <summary>
/// Real Anthropic Messages API client (PRD §7, §11). Uses a plain <see cref="HttpClient"/> per
/// PRD §11 ("plain HttpClient, or the community Anthropic.SDK"). The per-stage system prompt is
/// marked with <c>cache_control: ephemeral</c> for prompt caching, since the pipeline re-sends
/// the same stable system prompt across turns.
/// </summary>
public sealed class AnthropicClient : IAnthropicClient, IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    // Shared, long-lived HttpClient (the idiomatic pattern). The API key is set per-request,
    // not on the client, so one instance safely serves every AnthropicClient. Disposing an
    // AnthropicClient must never tear this down — see Dispose().
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    // Serialize every Anthropic call through one gate so requests never overlap (a simple FIFO queue).
    // Callers mark themselves in-flight via AiActivity before waiting, so the status bar shows queue depth.
    private static readonly System.Threading.SemaphoreSlim Gate = new(1, 1);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly int _maxTokens;

    public AnthropicClient(string apiKey, int maxTokens = 16384, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _maxTokens = maxTokens;
        _http = http ?? SharedHttp;     // default to the shared client
        _ownsHttp = http is not null;   // only dispose a caller-injected client
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey);

    private void AddAuthHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
    }

    /// <summary>PRD §8.9: validate the key cheaply via GET /v1/models (no token spend).</summary>
    public async Task<ModelListResult> ListModelsAsync(CancellationToken ct = default)
    {
        using var _activity = Diagnostics.AiActivity.Begin("Loading models…");
        await Gate.WaitAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models?limit=100");
            AddAuthHeaders(req);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var msg = $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {ExtractError(body)}";
                Diagnostics.AppLog.Ai("models", "—", 0, body.Length, sw.ElapsedMilliseconds, false, msg);
                return ModelListResult.Failure(msg);
            }

            var parsed = JsonSerializer.Deserialize<ModelsResponse>(body);
            if (parsed?.Data is null || parsed.Data.Count == 0)
                return ModelListResult.Failure("No models returned.");

            var models = parsed.Data
                .Select(m => new ClaudeModel(m.Id, m.DisplayName ?? m.Id, ""))
                .ToList();
            Diagnostics.AppLog.Ai("models", "—", 0, body.Length, sw.ElapsedMilliseconds, true, $"{models.Count} models");
            return new ModelListResult(true, models, null);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Ai("models", "—", 0, 0, sw.ElapsedMilliseconds, false, ex.Message);
            return ModelListResult.Failure(ex.Message);
        }
        finally { Gate.Release(); }
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, string modelId, CancellationToken ct = default)
    {
        var model = string.IsNullOrWhiteSpace(modelId) ? ModelCatalog.DefaultModelId : modelId;
        using var _activity = Diagnostics.AiActivity.Begin("Working with Claude…");
        await Gate.WaitAsync(ct);   // queue: one AI call at a time
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var payload = new MessageRequest
            {
                Model = model,
                MaxTokens = _maxTokens,
                System = new[] { new SystemBlock { Text = systemPrompt, CacheControl = new CacheControl() } },
                Messages = new[] { new RequestMessage { Role = "user", Content = userPrompt } },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
            };
            AddAuthHeaders(req);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = ExtractError(body);
                Diagnostics.AppLog.Ai("messages", model, systemPrompt.Length + userPrompt.Length, 0, sw.ElapsedMilliseconds, false, $"{(int)resp.StatusCode} {err}");
                throw new HttpRequestException($"Anthropic API error {(int)resp.StatusCode}: {err}");
            }

            var message = JsonSerializer.Deserialize<MessageResponse>(body, JsonOpts);
            var text = message?.Content?
                .Where(b => b.Type == "text" && b.Text is not null)
                .Select(b => b.Text)
                .FirstOrDefault() ?? "";
            // Never let a max_tokens truncation be silent: a complex design/firmware pass that overruns the
            // cap returns partial JSON the caller will reject — surface WHY so it isn't read as a clean fallback.
            if (IsTruncated(message?.StopReason))
                Diagnostics.AppLog.Warn("ai",
                    $"Claude hit the {_maxTokens}-token output cap and the response was TRUNCATED ({text.Length} chars) — " +
                    "output is incomplete. Raise the output-token limit in Settings or simplify the request.");
            Diagnostics.AppLog.Ai("messages", model, systemPrompt.Length + userPrompt.Length, text.Length, sw.ElapsedMilliseconds, true);
            return text;
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            Diagnostics.AppLog.Ai("messages", model, systemPrompt.Length + userPrompt.Length, 0, sw.ElapsedMilliseconds, false, ex.Message);
            throw;
        }
        finally { Gate.Release(); }
    }

    private static string ExtractError(string body)
    {
        try
        {
            var err = JsonSerializer.Deserialize<ErrorEnvelope>(body);
            return err?.Error?.Message ?? body;
        }
        catch { return body; }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- wire types ----
    private sealed class ModelsResponse
    {
        [JsonPropertyName("data")] public List<ModelEntry>? Data { get; set; }
    }
    private sealed class ModelEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }

    private sealed class MessageRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")] public SystemBlock[]? System { get; set; }
        [JsonPropertyName("messages")] public RequestMessage[] Messages { get; set; } = Array.Empty<RequestMessage>();
    }
    private sealed class SystemBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "text";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("cache_control")] public CacheControl? CacheControl { get; set; }
    }
    private sealed class CacheControl
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "ephemeral";
    }
    private sealed class RequestMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "user";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class MessageResponse
    {
        [JsonPropertyName("content")] public List<ContentBlock>? Content { get; set; }
        [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    }

    /// <summary>True when the model stopped because it hit the output-token cap — the response is incomplete
    /// (any JSON it was producing is truncated). Pure + testable.</summary>
    internal static bool IsTruncated(string? stopReason) =>
        string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase);
    private sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")] public ErrorBody? Error { get; set; }
    }
    private sealed class ErrorBody
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
