using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Foundry.Core.Simulation;

/// <summary>
/// Spawns and supervises a single long-lived headless Renode process and exposes a
/// <see cref="RenodeClient"/> on its Monitor TCP socket (port 3456). Mirrors
/// <see cref="Sidecar.SidecarHost"/>: idempotent start, health-check, reuse a running instance, kill on
/// dispose. <see cref="Dispose"/> must be wired into app shutdown next to SidecarHost so the emulator is
/// not orphaned. Single shared instance per process.
/// </summary>
public sealed class RenodeHost : IDisposable
{
    /// <summary>Renode Monitor TCP port we drive the emulator over.</summary>
    public const int MonitorPort = 3456;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private Process? _process;
    private RenodeClient? _client;

    public static RenodeHost Shared { get; } = new();

    public string StatusMessage { get; private set; } = "not started";
    public bool IsRunning => _client is not null;

    /// <summary>Idempotently ensures Renode is running; returns a connected client or null on failure.</summary>
    public async Task<RenodeClient?> StartAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;
        await Gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;

            var probe = new RenodeClient(MonitorPort);

            // A Renode may already be listening (dev / previous run) — reuse it.
            if (await probe.HealthAsync(ct))
            {
                _client = probe;
                StatusMessage = $"connected · monitor :{MonitorPort}";
                return _client;
            }

            var exe = RenodeInstaller.Locate();
            if (exe is null)
            {
                StatusMessage = "Renode not installed";
                Diagnostics.AppLog.Warn("sim", "Renode not found — install it to run live simulation");
                return null;
            }
            Diagnostics.AppLog.Info("sim", $"spawning headless Renode on monitor :{MonitorPort}");

            try
            {
                _process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--disable-gui --console -P {MonitorPort} -e \"logLevel 3\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"spawn failed: {ex.Message}";
                Diagnostics.AppLog.Error("sim", $"Renode spawn failed: {ex.Message}");
                return null;
            }

            // Poll the Monitor socket until it accepts (~15s; Renode + mono startup is slow on first run).
            for (int i = 0; i < 60; i++)
            {
                if (_process is { HasExited: true })
                {
                    StatusMessage = $"Renode exited (code {_process.ExitCode})";
                    Diagnostics.AppLog.Warn("sim", StatusMessage);
                    return null;
                }
                if (await probe.HealthAsync(ct))
                {
                    _client = probe;
                    StatusMessage = $"online · monitor :{MonitorPort}";
                    Diagnostics.AppLog.Info("sim", StatusMessage);
                    return _client;
                }
                await Task.Delay(250, ct);
            }
            StatusMessage = "Renode health-check timed out";
            Diagnostics.AppLog.Warn("sim", StatusMessage);
            return null;
        }
        finally { Gate.Release(); }
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
        _process?.Dispose();
        _process = null;
        StatusMessage = "stopped";
    }
}

/// <summary>
/// Talks the Renode Monitor line protocol over a TcpClient to 127.0.0.1: send <c>command\n</c>, read until
/// the prompt returns. Mirrors <see cref="Sidecar.SidecarClient"/>'s framing discipline. Also owns the host
/// TCP listener (port 7777) the .resc's Python hook pushes <c>pin=level\n</c> edges to (Mechanism B).
/// </summary>
public sealed class RenodeClient : IDisposable
{
    /// <summary>Host listener port the .resc's embedded Python connects back to (Mechanism B push).</summary>
    public const int HostPushPort = 7777;

    private const string Prompt = "(monitor)";

    private readonly int _monitorPort;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _io = new(1, 1);

    public RenodeClient(int monitorPort = RenodeHost.MonitorPort) => _monitorPort = monitorPort;

    /// <summary>True when the Monitor socket accepts a connection (or is already connected).</summary>
    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            return _tcp?.Connected == true;
        }
        catch { return false; }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_tcp?.Connected == true) return;
        _tcp = new TcpClient();
        await _tcp.ConnectAsync("127.0.0.1", _monitorPort, ct);
        _stream = _tcp.GetStream();
    }

    /// <summary>Send a Monitor command and return everything Renode prints before the next prompt.</summary>
    public async Task<string> CommandAsync(string command, CancellationToken ct = default)
    {
        await _io.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            var stream = _stream!;
            var bytes = Encoding.ASCII.GetBytes(command + "\n");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);

            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(buf, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().Contains(Prompt)) break;
            }
            return sb.ToString();
        }
        finally { _io.Release(); }
    }

    /// <summary>Load and execute a .resc script from <paramref name="rescPath"/> via the Monitor.</summary>
    public Task<string> LoadScriptAsync(string rescPath, CancellationToken ct = default) =>
        CommandAsync($"include @{rescPath.Replace('\\', '/')}", ct);

    /// <summary>Tell Renode to quit, then close the socket.</summary>
    public async Task QuitAsync(CancellationToken ct = default)
    {
        try { await CommandAsync("quit", ct); } catch { }
        Dispose();
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
    }
}
