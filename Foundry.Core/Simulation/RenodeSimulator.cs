using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Foundry.Core.Firmware;
using Foundry.Core.Kb;

namespace Foundry.Core.Simulation;

/// <summary>
/// Renode-backed simulator (recipe-driven). Decides whether a project's chip has a live model
/// (STM32 first-class, RP2040 community model; ESP32/AVR degrade to "flash to run"), then for supported
/// projects: compiles the firmware to an ELF, generates <c>foundry.repl</c>/<c>foundry.resc</c>, loads them
/// into the shared headless Renode, and streams per-GPIO edges into a <see cref="SimSession"/> over the
/// one <c>pin=level\n</c> socket contract. Prefers the Python push hook (Mechanism B); falls back to
/// Monitor <c>watch</c> polling (Mechanism A) by parsing the Monitor stream.
/// </summary>
public sealed class RenodeSimulator : ISimulator
{
    private static readonly Regex PinLine = new(@"^\s*(\d+)\s*=\s*([01])\s*$", RegexOptions.Compiled);
    private static readonly Regex StateLine = new(@"(led\d+).*?(True|False)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ComponentKb _kb;
    private readonly RenodeHost _host;

    public RenodeSimulator(ComponentKb? kb = null, RenodeHost? host = null)
    {
        _kb = kb ?? ComponentKb.Demo();
        _host = host ?? RenodeHost.Shared;
    }

    public SimEngine Engine => SimEngine.Renode;

    public SimCapability CanSimulate(Project.Project project)
    {
        var fqbn = FirmwareBuilder.Fqbn(project).ToLowerInvariant();

        if (fqbn.Contains("esp32") || fqbn.Contains("esp8266"))
            return SimCapability.No("ESP32/ESP8266 has no live GPIO model in Renode yet — flash to run.");
        if (fqbn.Contains("avr") || fqbn.Contains("uno") || fqbn.Contains("nano") || fqbn.Contains("mega"))
            return SimCapability.No("AVR isn't emulated by Renode — the avr8js engine handles AVR boards.");

        // HONEST GATE — still CLOSED. The STM32 generator is fixed + unit-tested (port-aware GpioPinMap;
        // per-port gpioPort<X> nodes), and the generated STM32 .repl was confirmed to LOAD CLEAN in real Renode
        // 1.16.1 (using platforms/cpus/stm32f4.repl resolves; the per-port LED wiring parses). But the full LIVE
        // path is NOT wired end-to-end: Foundry's firmware build can't produce an STM32 ELF yet (FirmwareBuilder
        // .Fqbn doesn't infer STM32 and EnsureCoreAsync has no STM32 board-manager URL), and RP2040 has no
        // bundled platform .repl at all. Keep it gated rather than letting RUN start and fail. Lift ONLY when
        // RenodeLiveSmokeTest is observed green (real STM32 ELF → PA5 edges) on a machine with the STM32 core.
        return SimCapability.No("Live simulation for STM32/RP2040 isn't wired up yet — flash to run. (AVR boards do simulate live.)");
    }

    public async Task<SimSession> StartAsync(Project.Project project, CancellationToken ct = default)
    {
        var pins = GpioPinMap.Build(project.Connections, _kb);
        var fqbn = FirmwareBuilder.Fqbn(project);
        var session = new SimSession(Engine, pins, "compiling firmware…");

        // 1) Compile to an ELF the emulator can load. BuildDir is caller-owned; we keep it for Renode.
        var buildDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_sim_" + Guid.NewGuid().ToString("N")[..8]);
        var image = await FirmwareBuilder.CompileToImageAsync(project, buildDir, ct);
        if (!image.HasElf)
        {
            var why = image.Diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message ?? "firmware didn't compile";
            session.SetStatus($"can't simulate — {why}");
            session.Stop();
            try { System.IO.Directory.Delete(buildDir, true); } catch { }
            return session;
        }

        // 2) Start (or reuse) the shared headless Renode.
        session.SetStatus("starting Renode…");
        var client = await _host.StartAsync(ct);
        if (client is null)
        {
            session.SetStatus(_host.StatusMessage);
            session.Stop();
            return session;
        }

        // 3) Open the host push listener (Mechanism B) before the .resc connects to it.
        var listener = new GpioListener(RenodeClient.HostPushPort);
        bool pythonOk = listener.TryStart();

        // 4) Generate .repl/.resc next to the ELF (relative @paths resolve from the script's folder).
        var repl = RenodeReplGenerator.Build(fqbn, pins);
        var resc = RenodeRescGenerator.Build(image.ElfPath!, pins, RenodeClient.HostPushPort, usepython: pythonOk);
        var replPath = System.IO.Path.Combine(buildDir, "foundry.repl");
        var rescPath = System.IO.Path.Combine(buildDir, "foundry.resc");
        await System.IO.File.WriteAllTextAsync(replPath, repl, ct);
        await System.IO.File.WriteAllTextAsync(rescPath, resc, ct);

        // 5) Wire pin-edge plumbing onto the session (resolve net/endpoint identity per GPIO).
        void Emit(int gpio, bool high)
        {
            var sp = GpioPinMap.Resolve(pins, gpio);
            session.Push(new PinLevel(gpio, high, sp?.Net, sp?.Endpoint));
        }

        if (pythonOk)
        {
            listener.PinChanged += Emit;
            _ = listener.PumpAsync(CancellationToken.None);
        }
        else
        {
            // Mechanism A fallback — parse the Monitor watch stream for "ledN ... True/False".
            Diagnostics.AppLog.Info("sim", "Renode Python push unavailable — falling back to Monitor watch polling");
            listener.Dispose();
            _ = PollMonitorAsync(client, pins, Emit, ct);
        }

        // 6) Load and start the run.
        try { await client.LoadScriptAsync(rescPath, ct); }
        catch (Exception ex)
        {
            session.SetStatus($"Renode load failed: {ex.Message}");
            Diagnostics.AppLog.Error("sim", $"resc load failed: {ex.Message}");
            session.Stop();
        }

        // 7) Lifecycle: stop unloads the machine and stops the emulator; dispose closes the listener.
        session.Bind(
            onSpeed: factor => { try { _ = client.CommandAsync($"machine SetGlobalQuantum \"{0.0001 / Math.Max(0.01, factor):F6}\"", CancellationToken.None); } catch { } },
            onStop: () =>
            {
                try { _ = client.CommandAsync("pause", CancellationToken.None); } catch { }
                listener.Dispose();
                try { System.IO.Directory.Delete(buildDir, true); } catch { }
            });

        session.SetStatus(pythonOk ? "running · Renode (push)" : "running · Renode (poll)");
        Diagnostics.AppLog.Info("sim", $"simulation running · {fqbn} · {pins.Count} pin(s)");
        return session;
    }

    /// <summary>Mechanism A: poll the Monitor watch stream, parsing "ledN State -> True/False" lines.</summary>
    private static async Task PollMonitorAsync(RenodeClient client, IReadOnlyList<SimPin> pins, Action<int, bool> emit, CancellationToken ct)
    {
        var ledToGpio = pins.ToDictionary(p => p.LedName, p => p.Gpio, StringComparer.OrdinalIgnoreCase);
        var last = new Dictionary<int, bool>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var p in pins)
                {
                    var text = await client.CommandAsync($"sysbus.{p.LedName} State", ct);
                    var m = StateLine.Match(text);
                    if (!m.Success) continue;
                    if (!ledToGpio.TryGetValue(m.Groups[1].Value, out var gpio)) gpio = p.Gpio;
                    var high = m.Groups[2].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
                    if (!last.TryGetValue(gpio, out var prev) || prev != high)
                    {
                        last[gpio] = high;
                        emit(gpio, high);
                    }
                }
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.AppLog.Warn("sim", $"monitor poll ended: {ex.Message}"); }
    }

    /// <summary>
    /// Host-owned TCP listener for the <c>pin=level\n</c> push contract (Mechanism B). Renode's embedded
    /// Python connects in and streams one line per GPIO edge; the avr8js fallback uses the same protocol.
    /// </summary>
    private sealed class GpioListener : IDisposable
    {
        private readonly int _port;
        private TcpListener? _server;
        private TcpClient? _peer;

        public event Action<int, bool>? PinChanged;

        public GpioListener(int port) => _port = port;

        public bool TryStart()
        {
            try
            {
                _server = new TcpListener(IPAddress.Loopback, _port);
                _server.Start();
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Warn("sim", $"GPIO push listener couldn't bind :{_port} — {ex.Message}");
                _server = null;
                return false;
            }
        }

        /// <summary>Accept the Renode connection and parse <c>pin=level\n</c> lines until the peer closes.</summary>
        public async Task PumpAsync(CancellationToken ct)
        {
            if (_server is null) return;
            try
            {
                _peer = await _server.AcceptTcpClientAsync(ct);
                var stream = _peer.GetStream();
                var buf = new byte[1024];
                var acc = new StringBuilder();
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, ct);
                    if (n <= 0) break;
                    acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                    int nl;
                    while ((nl = IndexOfNewline(acc)) >= 0)
                    {
                        var line = acc.ToString(0, nl);
                        acc.Remove(0, nl + 1);
                        var m = PinLine.Match(line);
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var gpio))
                            PinChanged?.Invoke(gpio, m.Groups[2].Value == "1");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.AppLog.Warn("sim", $"GPIO push pump ended: {ex.Message}"); }
        }

        private static int IndexOfNewline(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
            return -1;
        }

        public void Dispose()
        {
            try { _peer?.Close(); } catch { }
            try { _server?.Stop(); } catch { }
            _peer = null;
            _server = null;
        }
    }
}
