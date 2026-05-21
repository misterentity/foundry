using Foundry.Core.Project;

namespace Foundry.Core.Ai;

/// <summary>
/// Pipeline that drives a chat turn against the real Messages API when a key is configured,
/// and falls back to the offline canned reply otherwise (PRD F9). Stage progress is reported
/// for the chat UI. Full structured per-stage generation (PRD §7 — emitting validated Project
/// diffs) is the next build step; this turn currently produces a natural-language reply grounded
/// in the current Project document.
/// </summary>
public sealed class ChatPipeline : IPipeline
{
    private readonly IAnthropicClient _ai;
    private readonly string _modelId;
    private readonly int _stepDelayMs;

    public ChatPipeline(IAnthropicClient ai, string? modelId = null, int stepDelayMs = 220)
    {
        _ai = ai;
        _modelId = string.IsNullOrWhiteSpace(modelId) ? ModelCatalog.DefaultModelId : modelId;
        _stepDelayMs = stepDelayMs;
    }

    private const string SystemPrompt =
        "You are Foundry, an AI hardware-design studio. You help makers iterate on a hardware " +
        "project described by a canonical JSON document (architecture, BOM, netlist, enclosure, " +
        "firmware, validation). When the user asks to change the design, explain concisely which " +
        "stages you would re-run (Spec, Architecture, Wiring, Firmware, Enclosure, Validation) and " +
        "what changes. Keep replies short and practical. This is a design aid — remind users to " +
        "verify before building when relevant.";

    public async Task<ChatMessage> RunTurnAsync(
        Project.Project project,
        string userMessage,
        IProgress<IReadOnlyList<PipelineStage>>? progress = null,
        CancellationToken ct = default)
    {
        project.Chat.Add(new ChatMessage { Role = "user", Text = userMessage, Time = DateTime.Now.ToString("HH:mm") });

        var stages = IPipeline.Stages.Select(s => new PipelineStage(s, "pending")).ToList();

        string replyText;
        if (_ai.HasKey)
        {
            // Mark the whole pipeline live while the model thinks, then done on return.
            foreach (var s in stages) s.State = "live";
            progress?.Report(Snapshot(stages));
            try
            {
                var prompt = BuildPrompt(project, userMessage);
                replyText = await _ai.CompleteAsync(SystemPrompt, prompt, _modelId, ct);
                if (string.IsNullOrWhiteSpace(replyText))
                    replyText = "(No content returned by the model.)";
            }
            catch (Exception ex)
            {
                replyText = $"Generation failed: {ex.Message}";
            }
            foreach (var s in stages) s.State = "done";
            progress?.Report(Snapshot(stages));
        }
        else
        {
            for (int i = 0; i < stages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                stages[i].State = "live";
                progress?.Report(Snapshot(stages));
                await Task.Delay(_stepDelayMs, ct);
                stages[i].State = "done";
                progress?.Report(Snapshot(stages));
            }
            replyText = "Re-ran the affected stages against the canonical Project. " +
                        "(Offline preview — add your Anthropic key in Settings to generate for real.)";
        }

        var reply = new ChatMessage
        {
            Role = "assistant",
            Time = DateTime.Now.ToString("HH:mm"),
            Text = replyText,
            Pipeline = Snapshot(stages),
        };
        project.Chat.Add(reply);
        return reply;
    }

    private static string BuildPrompt(Project.Project project, string userMessage)
    {
        var parts = project.Subsystems.Select(s => $"{s.Role}: {s.Name} ({s.Mpn})");
        var summary =
            $"Current project: {project.Title}\n" +
            $"Prompt: {project.Prompt}\n" +
            $"Subsystems: {string.Join("; ", parts)}\n" +
            $"Parts: {project.Kpis.Parts}, est. cost ${project.Kpis.Cost:0.00}, battery ~{project.Kpis.BatteryDays} d.\n" +
            $"Open findings: {string.Join("; ", project.Findings.Where(f => f.Severity is "warn" or "fail").Select(f => f.Title))}\n\n" +
            $"User request: {userMessage}";
        return summary;
    }

    private static List<PipelineStage> Snapshot(List<PipelineStage> stages) =>
        stages.Select(s => new PipelineStage(s.Stage, s.State)).ToList();
}
