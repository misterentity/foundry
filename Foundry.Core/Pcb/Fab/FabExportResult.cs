namespace Foundry.Core.Pcb.Fab;

/// <summary>
/// Result of the v2.6 Gerber + Excellon drill export and fab-ZIP package (<see cref="GerberExporter"/>) —
/// mirrors <see cref="DrcReport"/>/<see cref="RouteResult"/>'s Installed/Ok/Summary shape. <see cref="Ok"/>
/// means both kicad-cli export runs exited 0, the produced set validated (<see cref="FabFileSet"/>), and the
/// single fab ZIP was written. <see cref="ZipPath"/> is the <c>&lt;name&gt;-fab.zip</c> — a standard 2-layer set
/// in the format board houses expect, to REVIEW (in a Gerber viewer) before ordering, not a manufacturability
/// guarantee; <see cref="Files"/> are the produced gerber/drill files. <see cref="NotInstalled"/> when kicad-cli
/// is absent. Never throws.
/// </summary>
public sealed record FabExportResult(
    bool Installed,
    bool Ok,
    string Summary,
    string? ZipPath,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Notes)
{
    public static FabExportResult NotInstalled() =>
        new(false, false,
            $"Fab export needs KiCad — install it from {KiCadInstaller.DownloadUrl} to run kicad-cli pcb export.",
            null, Array.Empty<string>(), Array.Empty<string>());

    public static FabExportResult Failed(string summary, IEnumerable<string>? notes = null,
        IReadOnlyList<string>? files = null) =>
        new(true, false, summary, null, files ?? Array.Empty<string>(),
            (notes ?? Array.Empty<string>()).ToArray());

    /// <summary>
    /// Build the result from the two export exit codes + their stderr, the produced file list, and the
    /// zip path/entry count. Pure and fake-able (no process, no real zip): tests drive it with synthetic
    /// values. The gate is: both exits 0 AND the file set validates AND the zip exists with a non-trivial
    /// entry count. Mirrors <see cref="DrcReport.Parse"/>'s exit-code-then-file approach. Never throws.
    /// </summary>
    public static FabExportResult Parse(
        int gerberExit, string? gerberStderr,
        int drillExit, string? drillStderr,
        IReadOnlyList<string> producedFiles,
        string? zipPath, int zipEntryCount)
    {
        var notes = new List<string>();

        if (gerberExit != 0)
            notes.Add(Note("Gerber export", gerberExit, gerberStderr));
        if (drillExit != 0)
            notes.Add(Note("Drill export", drillExit, drillStderr));
        if (gerberExit != 0 || drillExit != 0)
            return Failed("Couldn't export fab files.", notes, producedFiles);

        var validation = FabFileSet.Validate(producedFiles);
        if (!validation.Ok)
        {
            notes.Add("Missing: " + string.Join(", ", validation.Missing));
            return Failed("Fab export incomplete — required files not produced.", notes, producedFiles);
        }

        if (string.IsNullOrEmpty(zipPath) || zipEntryCount <= 0)
        {
            notes.Add("The fab ZIP was empty or not written.");
            return Failed("Couldn't package the fab ZIP.", notes, producedFiles);
        }

        var summary = $"Fab files exported — {producedFiles.Count} files → {System.IO.Path.GetFileName(zipPath)}. " +
                      "Design aid — review the Gerbers in a viewer before ordering.";
        return new FabExportResult(true, true, summary, zipPath, producedFiles, notes);
    }

    private static string Note(string label, int exit, string? stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? $"{label} exited {exit}." : $"{label}: {stderr!.Trim()}";
}
