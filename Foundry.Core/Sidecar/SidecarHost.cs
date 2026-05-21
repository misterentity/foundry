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

            var sidecarDir = LocateSidecarDir();
            if (sidecarDir is null)
            {
                StatusMessage = "sidecar files not found";
                return null;
            }
            var python = LocatePython(sidecarDir);
            if (python is null)
            {
                StatusMessage = "python interpreter not found";
                return null;
            }

            try
            {
                _process = Process.Start(new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = $"server.py --host 127.0.0.1 --port {Port}",
                    WorkingDirectory = sidecarDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"spawn failed: {ex.Message}";
                return null;
            }

            // Poll /health until ready (~10s).
            for (int i = 0; i < 40; i++)
            {
                if (_process is { HasExited: true })
                {
                    StatusMessage = $"sidecar exited (code {_process.ExitCode})";
                    return null;
                }
                if (await probe.HealthAsync(ct))
                {
                    _client = probe;
                    StatusMessage = $"build123d · {baseUrl}";
                    return _client;
                }
                await Task.Delay(250, ct);
            }
            StatusMessage = "sidecar health-check timed out";
            return null;
        }
        finally { Gate.Release(); }
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

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
        _process?.Dispose();
        _process = null;
        _client = null;
    }
}
