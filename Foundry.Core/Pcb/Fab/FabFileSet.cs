namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Pure, fake-able validation of the produced fab file set — the real success gate (the export exit code is
/// only a first pass). A 2-layer JLCPCB/PCBWay-acceptable package must carry both copper gerbers, the
/// Edge.Cuts outline, and at least one Excellon drill file. Tests probe on extension / required-file presence
/// (Protel extensions when X2 is kept), never on exact filename stems, so renaming a stem can't break the gate.
/// </summary>
public static class FabFileSet
{
    /// <summary>Front/back copper gerber extensions (Protel) — both required.</summary>
    private static readonly string[] FrontCopperExt = { ".gtl" };
    private static readonly string[] BackCopperExt = { ".gbl" };

    /// <summary>Board-outline gerber extension (Protel) — required.</summary>
    private static readonly string[] OutlineExt = { ".gm1" };

    /// <summary>Excellon drill extension — at least one required (the -PTH.drl when separate-th).</summary>
    private const string DrillExt = ".drl";

    public sealed record Validation(bool Ok, IReadOnlyList<string> Missing)
    {
        public static Validation Pass() => new(true, Array.Empty<string>());
    }

    /// <summary>
    /// Validate the set of produced file paths (or names). Requires: a front-copper gerber, a back-copper
    /// gerber, the Edge.Cuts outline gerber, and at least one Excellon drill. Returns the list of missing
    /// classes when it fails. Pure — never touches the filesystem, so tests can pass a fake name list.
    /// </summary>
    public static Validation Validate(IEnumerable<string> producedFiles)
    {
        var names = (producedFiles ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(System.IO.Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        var missing = new List<string>();
        if (!HasExt(names, FrontCopperExt)) missing.Add("front copper (*.gtl)");
        if (!HasExt(names, BackCopperExt)) missing.Add("back copper (*.gbl)");
        if (!HasExt(names, OutlineExt)) missing.Add("board outline (*.gm1)");
        if (!HasDrill(names)) missing.Add("drill (*.drl)");

        return missing.Count == 0 ? Validation.Pass() : new Validation(false, missing);
    }

    private static bool HasExt(IEnumerable<string> names, string[] exts) =>
        names.Any(n => exts.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    private static bool HasDrill(IEnumerable<string> names) =>
        names.Any(n => n.EndsWith(DrillExt, StringComparison.OrdinalIgnoreCase));
}
