using Foundry.Core.Project;

namespace Foundry.Core.Ai;

/// <summary>
/// Offline pipeline (PRD F9): animates the staged progress and appends a canned assistant
/// turn so the chat + per-stage pipeline UI is fully exercised without an API key. The
/// real <see cref="IPipeline"/> implementation replaces this once generation is wired.
/// </summary>
public sealed class StubPipeline : IPipeline
{
    private readonly int _stepDelayMs;

    public StubPipeline(int stepDelayMs = 320) => _stepDelayMs = stepDelayMs;

    public async Task<ChatMessage> RunTurnAsync(
        Project.Project project,
        string userMessage,
        IProgress<IReadOnlyList<PipelineStage>>? progress = null,
        CancellationToken ct = default)
    {
        // Echo the user's turn into the project history.
        project.Chat.Add(new ChatMessage
        {
            Role = "user", Text = userMessage, Time = DateTime.Now.ToString("HH:mm"),
        });

        var stages = IPipeline.Stages
            .Select(s => new PipelineStage(s, "pending"))
            .ToList();

        for (int i = 0; i < stages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            stages[i].State = "live";
            progress?.Report(Snapshot(stages));
            await Task.Delay(_stepDelayMs, ct);
            stages[i].State = "done";
            progress?.Report(Snapshot(stages));
        }

        var reply = new ChatMessage
        {
            Role = "assistant",
            Time = DateTime.Now.ToString("HH:mm"),
            Text = "Re-ran the affected stages against the canonical Project. " +
                   "(Offline preview — add your Anthropic key in Settings to generate for real.)",
            Pipeline = Snapshot(stages),
        };
        project.Chat.Add(reply);
        return reply;
    }

    private static List<PipelineStage> Snapshot(List<PipelineStage> stages) =>
        stages.Select(s => new PipelineStage(s.Stage, s.State)).ToList();
}
