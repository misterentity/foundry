using Foundry.Core.Project;

namespace Foundry.Core.Ai;

/// <summary>
/// Staged generation orchestrator (PRD §7). A chat turn is interpreted as an instruction
/// to modify the Project; the pipeline decides which stages to re-run, reports per-stage
/// progress, and returns the assistant's reply. Phase 1 ships a stub; the real staged
/// Claude pipeline drops in behind this interface.
/// </summary>
public interface IPipeline
{
    /// <summary>The canonical pipeline stage names, in order (PRD §7).</summary>
    static readonly string[] Stages =
        { "Spec", "Architecture", "Wiring", "Firmware", "Enclosure", "Validation" };

    /// <summary>
    /// Runs a chat turn against <paramref name="project"/>. Emits <see cref="PipelineStage"/>
    /// updates as stages move pending→live→done, mutates the project in place, and returns
    /// the assistant reply (already carrying its final pipeline snapshot).
    /// </summary>
    Task<ChatMessage> RunTurnAsync(
        Project.Project project,
        string userMessage,
        IProgress<IReadOnlyList<PipelineStage>>? progress = null,
        CancellationToken ct = default);
}
