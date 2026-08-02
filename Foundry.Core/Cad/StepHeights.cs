using System.Globalization;
using System.Text.RegularExpressions;

namespace Foundry.Core.Cad;

/// <summary>
/// Resolves a component's real vertical extent from the STEP model KiCad ships with its footprint —
/// the ground truth that makes mechanical fit provable rather than guessed.
///
/// <para>
/// KiCad 10 ships ~7,200 <c>.step</c> models and no VRML. STEP is ASCII (ISO 10303-21), so the model's
/// bounding box in Z is recoverable by reading every CARTESIAN_POINT and taking the extent — no CAD
/// kernel required. Spot-checked against datasheets: R_0805 → 0.45 mm, C_0603 → 0.80 mm,
/// ESP32-WROOM-32 → 3.10 mm above board, DIP-28 → 3.68 mm, TO-220 vertical → 18.77 mm.
/// </para>
///
/// <para>
/// Z is signed about the board plane: POSITIVE is above the PCB (what must clear the lid), NEGATIVE is
/// pin tails below it (what sets the minimum standoff). Both matter, and both are in the model.
/// </para>
/// </summary>
public static class StepHeights
{
    // #12=CARTESIAN_POINT('',(1.0,2.0,3.0));  — whitespace and scientific notation both occur.
    private static readonly Regex Point = new(
        @"CARTESIAN_POINT\s*\(\s*'[^']*'\s*,\s*\(\s*([-0-9.eE+]+)\s*,\s*([-0-9.eE+]+)\s*,\s*([-0-9.eE+]+)\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Heights for the handful of footprints Foundry emits that KiCad ships NO 3D model for. Measured
    /// from the physical parts; without these the four most common maker boards report unproven.
    /// </summary>
    private static readonly Dictionary<string, (double Above, double Below)> Curated =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Module:RaspberryPi_Pico_Common_SMD"] = (3.9, 0.0),    // castellated module, sits flat
            ["Module:Arduino_UNO_R3"]              = (11.0, 3.0),   // stacked headers dominate
            ["Module:Arduino_Nano"]                = (7.5, 3.0),    // header pins below
            ["Package_TO_SOT_SMD:SOT-223-3_TabPin2"] = (1.8, 0.0),
        };

    /// <summary>Where KiCad keeps 3D models for a footprint library, given the footprint dir.</summary>
    public static string ModelDirFor(string footprintDir) =>
        Path.Combine(Path.GetDirectoryName(footprintDir.TrimEnd('/', '\\')) ?? footprintDir, "3dmodels");

    /// <summary>
    /// The vertical extent of <paramref name="libId"/> ("Lib:Footprint"), or an unknown
    /// <see cref="PartHeight"/> when neither a curated entry nor a shipped model can supply it.
    /// Never throws — an unreadable model is reported as unknown, never as zero height.
    /// </summary>
    public static PartHeight For(string libId, string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(libId)) return PartHeight.Unknown(libId ?? "");
        if (Curated.TryGetValue(libId, out var c)) return new PartHeight(libId, c.Above, c.Below);

        var path = ModelPath(libId, modelDir);
        if (path is null) return PartHeight.Unknown(libId);

        var extent = ZExtent(path);
        return extent is var (lo, hi) && extent is not null
            ? new PartHeight(libId, Math.Round(hi, 2), Math.Round(Math.Max(0, -lo), 2))
            : PartHeight.Unknown(libId);
    }

    /// <summary>The .step path for a lib id, or null when the library or model isn't present.</summary>
    public static string? ModelPath(string libId, string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !libId.Contains(':')) return null;
        var parts = libId.Split(':', 2);
        try
        {
            var p = Path.Combine(modelDir, parts[0] + ".3dshapes", parts[1] + ".step");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>Min/max Z over every CARTESIAN_POINT in a STEP file, or null when it has none.</summary>
    internal static (double Lo, double Hi)? ZExtent(string stepPath)
    {
        try
        {
            double lo = double.MaxValue, hi = double.MinValue;
            var found = false;
            foreach (Match m in Point.Matches(File.ReadAllText(stepPath)))
            {
                if (!double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    continue;
                found = true;
                if (z < lo) lo = z;
                if (z > hi) hi = z;
            }
            return found ? (lo, hi) : null;
        }
        catch { return null; }
    }
}
