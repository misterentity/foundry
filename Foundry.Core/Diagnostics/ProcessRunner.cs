using System.Diagnostics;

namespace Foundry.Core.Diagnostics;

/// <summary>Outcome of a child-process run. <see cref="TimedOut"/> distinguishes a watchdog kill
/// (CancelAfter) from a caller cancel; on either, <see cref="ExitCode"/> is non-zero (we killed it).</summary>
public readonly record struct ProcRun(string Stdout, string Stderr, int ExitCode, bool TimedOut);

/// <summary>
/// The one shared subprocess runner for the toolchain call-sites (kicad-cli, pcbnew, FreeRouting,
/// arduino-cli). Replaces the per-file private RunAsync helpers, which all (a) read stdout then stderr
/// sequentially before WaitForExit — a pipe-buffer deadlock on verbose children — and (b) had no timeout
/// and never killed a hung child. This drains both streams concurrently, bounds the run by a timeout AND
/// the caller's token, and kills the whole process tree on timeout/cancel. Public so it is unit-testable
/// (matches the BuildArgs/Parse/ReadScript public-helper convention). Never leaks a running child.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Default timeouts by call-site class. kicad-cli/pcbnew are fast; FreeRouting is the long pole.</summary>
    public static readonly TimeSpan KicadTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan RouterTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ArduinoTimeout = TimeSpan.FromMinutes(5);  // core install / compile / upload

    /// <summary>
    /// Start <paramref name="exe"/> with <paramref name="args"/>, drain stdout+stderr concurrently (no
    /// pipe-buffer deadlock), bounded by <paramref name="timeout"/> AND <paramref name="ct"/>. On timeout or
    /// cancel the ENTIRE process tree is killed before returning/throwing. A caller cancel throws
    /// <see cref="OperationCanceledException"/> (preserves the existing <c>catch(OCE){throw;}</c> contract);
    /// a watchdog timeout returns <see cref="ProcRun"/> with <see cref="ProcRun.TimedOut"/> true and a
    /// non-zero exit code so callers translate it to a Failed result (and keep the best board).
    /// </summary>
    public static async Task<ProcRun> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi)!;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Start BOTH reads first (no await) so neither pipe buffer can fill and block the child.
        var outTask = p.StandardOutput.ReadToEndAsync(linked.Token);
        var errTask = p.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token);
            return new ProcRun(await outTask, await errTask, p.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            // FreeRouting/arduino-cli spawn children — kill the whole tree, then drain best-effort output.
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            string o = "", e = "";
            try { o = await outTask; } catch { }
            try { e = await errTask; } catch { }

            bool timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            if (timedOut)
            {
                AppLog.Warn("proc", $"timed out after {timeout.TotalSeconds:0}s, killed process tree: {System.IO.Path.GetFileName(exe)}");
                return new ProcRun(o, e + $"\n[timed out after {timeout.TotalSeconds:0}s — process killed]", -1, true);
            }

            ct.ThrowIfCancellationRequested();   // caller cancel → propagate (existing OCE contract)
            return new ProcRun(o, e, -1, true);  // defensive; not normally reached
        }
    }
}
