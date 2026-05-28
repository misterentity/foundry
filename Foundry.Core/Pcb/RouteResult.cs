using System.Text.Json;

namespace Foundry.Core.Pcb;

/// <summary>
/// Result of routing a placed <c>.kicad_pcb</c> with FreeRouting (Track B v2.4) — mirrors
/// <see cref="PcbResult"/>'s Installed/Ok/Summary shape. <see cref="RoutedPcbPath"/> is the SES-applied
/// board (owned by the caller) when <see cref="Ok"/>. Outcome stats are board-derived (authoritative):
/// <see cref="UnroutedCount"/> 0 ⇒ fully routed, N>0 ⇒ N nets unrouted; <see cref="TrackCount"/> &gt; 0
/// confirms copper was applied.
/// </summary>
public sealed record RouteResult(
    bool Installed,
    bool Ok,
    string Summary,
    string? RoutedPcbPath,
    int TrackCount,
    int ViaCount,
    int UnroutedCount,
    bool FullyRouted,
    IReadOnlyList<string> Notes)
{
    public static RouteResult NotInstalled() =>
        new(false, false,
            $"Routing needs KiCad + Java (JRE 21+) + the FreeRouting jar. Install JRE 21+ from {FreeRoutingInstaller.JdkDownloadUrl}; the jar downloads on demand.",
            null, 0, 0, 0, false, Array.Empty<string>());

    public static RouteResult Failed(string summary, IEnumerable<string>? notes = null) =>
        new(true, false, summary, null, 0, 0, 0, false, (notes ?? Array.Empty<string>()).ToArray());

    /// <summary>
    /// Build a result from the import_ses.py JSON stats + the routed board path. The board-derived
    /// <paramref name="importStdout"/> (unconnected/tracks/vias) is authoritative; FreeRouting's log
    /// (<paramref name="routerLog"/>) is folded into notes as a secondary signal.
    /// </summary>
    public static RouteResult Parse(string importStdout, string importStderr, int importExit,
        string? routerLog, string expectedOut)
    {
        var notes = new List<string>();
        bool ok = importExit == 0;
        string? outPath = null;
        int tracks = 0, vias = 0, unconnected = 0;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(importStdout) ? "{}" : LastJsonLine(importStdout));
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ok = o.GetBoolean();
            if (root.TryGetProperty("out", out var op) && op.ValueKind == JsonValueKind.String)
                outPath = op.GetString();
            if (root.TryGetProperty("tracks", out var t) && t.TryGetInt32(out var tv)) tracks = tv;
            if (root.TryGetProperty("vias", out var v) && v.TryGetInt32(out var vv)) vias = vv;
            if (root.TryGetProperty("unconnected", out var u) && u.TryGetInt32(out var uv)) unconnected = uv;
            if (root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String)
            {
                notes.Add(er.GetString() ?? "");
                ok = false;
            }
        }
        catch
        {
            if (!ok && !string.IsNullOrWhiteSpace(importStderr)) notes.Add(importStderr.Trim());
        }

        if (!ok && notes.Count == 0)
            notes.Add(string.IsNullOrWhiteSpace(importStderr) ? "SES import failed." : importStderr.Trim());

        var path = outPath ?? (ok ? expectedOut : null);
        if (ok && (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)))
        {
            ok = false;
            notes.Add("Import reported success but no routed .kicad_pcb was written.");
        }

        var routerNote = SummarizeRouterLog(routerLog);
        if (routerNote is not null) notes.Add(routerNote);

        bool fullyRouted = ok && unconnected == 0;
        var summary = ok
            ? (fullyRouted
                ? $"Routed {System.IO.Path.GetFileName(path)} — {tracks} tracks, {vias} vias, fully connected."
                : $"Routed {System.IO.Path.GetFileName(path)} — {tracks} tracks, {vias} vias, {unconnected} net(s) unrouted.")
            : "Couldn't route the PCB.";

        return new RouteResult(true, ok, summary, ok ? path : null, tracks, vias, unconnected, fullyRouted, notes);
    }

    /// <summary>import_ses.py prints one JSON line last; tolerate prior log lines on stdout.</summary>
    private static string LastJsonLine(string stdout)
    {
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var s = line.Trim();
            if (s.StartsWith("{") && s.EndsWith("}")) return s;
        }
        return stdout.Trim();
    }

    /// <summary>Pull an "incomplete"/pass hint out of FreeRouting's INFO log, if it said anything useful.</summary>
    private static string? SummarizeRouterLog(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;
        foreach (var line in log.Split('\n').Reverse())
        {
            var s = line.Trim();
            if (s.Length == 0) continue;
            var low = s.ToLowerInvariant();
            if (low.Contains("incomplete") || low.Contains("unrouted") || low.Contains("completed"))
                return "FreeRouting: " + s;
        }
        return null;
    }
}
