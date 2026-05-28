namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Knobs for a v2.6 fab-package export. <see cref="Layers"/> is the standard 2-layer KiCad-9 token set
/// (accepted by both KiCad 8 and 9); <see cref="SeparateTh"/> splits plated/non-plated drill files (PTH/NPTH),
/// matching the recipe. Defaults are exactly what JLCPCB/PCBWay accept for a 2-layer board, so callers
/// normally pass nothing.
/// </summary>
public sealed record FabOptions(
    string Layers = FabOptions.DefaultLayers,
    bool SeparateTh = true,
    bool GenerateDrillMap = true)
{
    /// <summary>
    /// The 9-token KiCad-9 layer set: copper, paste, silkscreen, mask (front+back) plus the board outline.
    /// KiCad 9 renamed <c>F.SilkS</c>→<c>F.Silkscreen</c>; the new tokens are accepted by 8 and 9 both.
    /// </summary>
    public const string DefaultLayers =
        "F.Cu,B.Cu,F.Paste,B.Paste,F.Silkscreen,B.Silkscreen,F.Mask,B.Mask,Edge.Cuts";

    public static FabOptions Default => new();
}
