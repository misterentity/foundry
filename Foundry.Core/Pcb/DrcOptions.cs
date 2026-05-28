namespace Foundry.Core.Pcb;

/// <summary>
/// Knobs for a <c>kicad-cli pcb drc</c> run (v2.5). <see cref="Strict"/> adds <c>--severity-warning</c>
/// so warnings gate too (default: errors only). <see cref="Units"/> maps to <c>--units</c> (default mm,
/// matching the placer's coordinate space). <see cref="MaxIterations"/> bounds the <see cref="PcbDesigner"/>
/// fix loop. Defaults are sane for the small boards Track B produces.
/// </summary>
public sealed record DrcOptions(bool Strict = false, string Units = "mm", int MaxIterations = 3)
{
    public static DrcOptions Default => new();
}
