using System.Diagnostics;

namespace Foundry.Core.Sidecar;

/// <summary>
/// Spawns and supervises the bundled Python CAD sidecar (PRD §5, §11, §16). Locates the sidecar
/// directory and a Python interpreter, starts <c>server.py</c> on 127.0.0.1, and health-checks it.
/// Everything degrades gracefully: if Python or the sidecar is missing, <see cref="StartAsync"/>
/// returns null and the UI falls back to the offline preview. Single shared instance per process.
/// </summary>
public sealed class SidecarHost : IDisposable
{
    private const int Port = 8731;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private Process? _process;
    private SidecarClient? _client;
    // Bounded tail of the child's stderr, surfaced if it fails to come up. Draining the pipes is what prevents
    // the deadlock — a long-lived child whose redirected stdout/stderr is never read blocks once the OS pipe
    // buffer fills (uvicorn logs every request), wedging the CAD sidecar with no error.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _stderrTail = new();

    public static SidecarHost Shared { get; } = new();

    public string StatusMessage { get; private set; } = "not started";
    public bool IsRunning => _client is not null;

    /// <summary>Idempotently ensures the sidecar is running; returns a client or null on failure.</summary>
    public async Task<SidecarClient?> StartAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;
        await Gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;

            var baseUrl = $"http://127.0.0.1:{Port}";
            var probe = new SidecarClient(baseUrl);

            // Maybe one is already running (dev) — reuse it.
            if (await probe.HealthAsync(ct))
            {
                _client = probe;
                StatusMessage = $"connected · {baseUrl}";
                return _client;
            }

            var (fileName, args, workDir, kind) = ResolveLauncher();
            if (fileName is null)
            {
                StatusMessage = "sidecar not found (no frozen exe and no Python)";
                Diagnostics.AppLog.Warn("sidecar", "not found — no frozen exe and no Python; enclosure 3D unavailable");
                return null;
            }
            Diagnostics.AppLog.Info("sidecar", $"spawning {kind} on {baseUrl}");

            try
            {
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = args!,
                        WorkingDirectory = workDir!,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                    },
                };
                // Drain both pipes asynchronously so the child can never block writing to a full buffer.
                // stdout is discarded (uvicorn access logs); a bounded tail of stderr is kept for diagnostics.
                _process.OutputDataReceived += (_, _) => { };
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    _stderrTail.Enqueue(e.Data);
                    while (_stderrTail.Count > 30) _stderrTail.TryDequeue(out string _);
                };
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                StatusMessage = $"spawn failed: {ex.Message}";
                return null;
            }

            try
            {
                // Poll /health until ready (~10s).
                for (int i = 0; i < 40; i++)
                {
                    if (_process is { HasExited: true })
                    {
                        StatusMessage = $"sidecar exited (code {_process.ExitCode})";
                        Diagnostics.AppLog.Warn("sidecar", $"exited (code {_process.ExitCode}){StderrTail()}");
                        return null;
                    }
                    if (await probe.HealthAsync(ct))
                    {
                        _client = probe;
                        StatusMessage = $"{kind} · {baseUrl}";
                        Diagnostics.AppLog.Info("sidecar", $"online · {kind} · {baseUrl}");
                        return _client;
                    }
                    await Task.Delay(250, ct);
                }
                StatusMessage = "sidecar health-check timed out";
                Diagnostics.AppLog.Warn("sidecar", $"health-check timed out{StderrTail()}");
                return null;
            }
            catch (OperationCanceledException)
            {
                // The caller cancelled mid-startup — don't orphan the half-started child until app exit.
                KillProcess();
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>Pick how to launch the sidecar: frozen exe (packaged) first, else Python + server.py (dev).</summary>
    private static (string? fileName, string? args, string? workDir, string kind) ResolveLauncher()
    {
        var portArgs = $"--host 127.0.0.1 --port {Port}";

        var frozen = LocateFrozenExe();
        if (frozen is not null)
            return (frozen, portArgs, Path.GetDirectoryName(frozen)!, "frozen sidecar");

        var dir = LocateSidecarDir();
        if (dir is null) return (null, null, null, "");
        var python = LocatePython(dir);
        if (python is null) return (null, null, null, "");
        return (python, $"server.py {portArgs}", dir, "python sidecar");
    }

    /// <summary>Frozen PyInstaller bundle: next to the app (packaged) or in sidecar/dist (dev build).</summary>
    private static string? LocateFrozenExe()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "sidecar", "foundry-cad.exe");
        if (File.Exists(packaged)) return packaged;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sidecar", "dist", "foundry-cad", "foundry-cad.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? LocateSidecarDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sidecar");
            if (File.Exists(Path.Combine(candidate, "server.py"))) return candidate;
        }
        return null;
    }

    private static string? LocatePython(string sidecarDir)
    {
        var venv = Path.Combine(sidecarDir, ".venv", "Scripts", "python.exe");
        if (File.Exists(venv)) return venv;
        foreach (var cmd in new[] { "py", "python", "python3" })
            if (OnPath(cmd) is { } full) return full;
        return null;
    }

    private static string? OnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            foreach (var ext in new[] { "", ".exe", ".cmd", ".bat" })
            {
                var full = Path.Combine(p, exe + ext);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>Last few stderr lines (if any), formatted for a log line.</summary>
    private string StderrTail() =>
        _stderrTail.IsEmpty ? "" : " — last stderr:\n" + string.Join("\n", _stderrTail);

    private void KillProcess()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        KillProcess();
        _client = null;
    }
}
