using System.Text.Json;

namespace Foundry.Core.Pcb;

/// <summary>One PCB-build note/diagnostic — the <see cref="Foundry.Core.Firmware.BuildDiagnostic"/> analogue.</summary>
public sealed record PcbDiagnostic(string Severity, string Message)
{
    public static PcbDiagnostic Warn(string m) => new("warning", m);
    public static PcbDiagnostic Error(string m) => new("error", m);
}

/// <summary>
/// Result of building a <c>.kicad_pcb</c> from the netlist — mirrors
/// <see cref="Foundry.Core.Firmware.BuildResult"/>'s Installed/Ok/Summary shape. <see cref="KicadPcbPath"/>
/// is the saved board (owned by the caller) when <see cref="Ok"/>.
/// </summary>
public sealed record PcbResult(bool Installed, bool Ok, string Summary, string? KicadPcbPath, IReadOnlyList<string> Notes)
{
    public static PcbResult NotInstalled() =>
        new(false, false,
            $"KiCad isn't installed — install it from {KiCadInstaller.DownloadUrl} to export a PCB.",
            null, Array.Empty<string>());

    public static PcbResult Skipped(string why) => new(true, false, why, null, Array.Empty<string>());

    public static PcbResult Failed(string summary, IEnumerable<string>? notes = null) =>
        new(true, false, summary, null, (notes ?? Array.Empty<string>()).ToArray());

    /// <summary>
    /// Parse build_board.py's single-line JSON stdout (try-JSON, else stderr scrape) into a result —
    /// the <see cref="Foundry.Core.Firmware.FirmwareBuilder.Parse"/> idiom. Expects
    /// <c>{"ok":bool,"out":path,"components":n,"nets":n,"notes":[...]}</c>.
    /// </summary>
    public static PcbResult Parse(string stdout, string stderr, int exitCode, string expectedOut)
    {
        var notes = new List<string>();
        bool ok = exitCode == 0;
        string? outPath = null;
        int? comps = null, nets = null;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdout) ? "{}" : LastJsonLine(stdout));
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ok = o.GetBoolean();
            if (root.TryGetProperty("out", out var op) && op.ValueKind == JsonValueKind.String)
                outPath = op.GetString();
            if (root.TryGetProperty("components", out var c) && c.TryGetInt32(out var cv)) comps = cv;
            if (root.TryGetProperty("nets", out var ne) && ne.TryGetInt32(out var nv)) nets = nv;
            if (root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String)
            {
                notes.Add(er.GetString() ?? "");
                ok = false;
            }
            if (root.TryGetProperty("notes", out var na) && na.ValueKind == JsonValueKind.Array)
                foreach (var nn in na.EnumerateArray())
                    if (nn.ValueKind == JsonValueKind.String) notes.Add(nn.GetString() ?? "");
        }
        catch
        {
            if (!ok && !string.IsNullOrWhiteSpace(stderr)) notes.Add(stderr.Trim());
        }

        if (!ok && notes.Count == 0)
            notes.Add(string.IsNullOrWhiteSpace(stderr) ? "PCB build failed." : stderr.Trim());

        var path = outPath ?? (ok ? expectedOut : null);
        if (ok && (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)))
        {
            ok = false;
            notes.Add("Script reported success but no .kicad_pcb was written.");
        }

        var summary = ok
            ? $"Built {System.IO.Path.GetFileName(path)} — {comps ?? 0} parts, {nets ?? 0} nets."
            : "Couldn't build the PCB.";
        return new PcbResult(true, ok, summary, ok ? path : null, notes);
    }

    /// <summary>build_board.py prints one JSON line last; tolerate prior log lines on stdout.</summary>
    private static string LastJsonLine(string stdout)
    {
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var t = line.Trim();
            if (t.StartsWith("{") && t.EndsWith("}")) return t;
        }
        return stdout.Trim();
    }
}
