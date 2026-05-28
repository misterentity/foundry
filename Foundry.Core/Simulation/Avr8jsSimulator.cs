using System.Diagnostics;
using Foundry.Core.Diagnostics;
using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Foundry.Core.Simulation;

/// <summary>
/// AVR-backed simulator using avr8js hosted in ClearScript's V8 engine, in-process (no socket, no second
/// process). For Arduino Uno/Nano/Mega projects it compiles the firmware to an Intel HEX via
/// <see cref="FirmwareBuilder"/>, loads the bundled avr8js runtime, and runs the real program on a single
/// dedicated background thread — pacing CPU cycle batches to wall-clock time. Each avr8js port edge is
/// turned back into an Arduino pin number via <see cref="AvrPinMap"/> and fed into the SAME
/// <see cref="SimSession"/>/<see cref="PinLevel"/> contract Renode uses, so BreadboardControl renders both
/// engines identically. Degrades gracefully (already-stopped session + StatusMessage) on compile/engine
/// failure; never throws on a normal path.
/// </summary>
public sealed class Avr8jsSimulator : ISimulator
{
    private const long CpuHz = 16_000_000;   // ATmega328P/2560 @ 16 MHz

    /// <summary>The avr8js runtime bundle (build-time esbuild output), shipped beside the assembly.</summary>
    public static string BundlePath => System.IO.Path.Combine(
        AppContext.BaseDirectory, "Assets", "avr8js-runtime.js");

    private readonly ComponentKb _kb;

    public Avr8jsSimulator(ComponentKb? kb = null) => _kb = kb ?? ComponentKb.Demo();

    public SimEngine Engine => SimEngine.Avr8js;

    public SimCapability CanSimulate(Project.Project project)
    {
        var fqbn = FirmwareBuilder.Fqbn(project).ToLowerInvariant();

        var isAvr = fqbn.Contains("avr") || fqbn.Contains("uno") || fqbn.Contains("nano")
                    || fqbn.Contains("mega") || fqbn.Contains("leonardo");
        if (!isAvr)
            return SimCapability.No("avr8js only emulates Arduino AVR boards (Uno/Nano/Mega) — flash to run this chip.");

        // Leonardo (ATmega32u4) isn't in the pin tables yet; it compiles but we can't map its USB-native ports.
        if (fqbn.Contains("leonardo") || fqbn.Contains("32u4"))
            return SimCapability.No("Leonardo (ATmega32u4) live model isn't ready — flash to run.");

        if (project.Firmware.Platform.Contains("python", StringComparison.OrdinalIgnoreCase))
            return SimCapability.No("MicroPython firmware has no compiled image to run in avr8js — flash to your board.");

        var pins = GpioPinMap.Build(project.Connections, _kb);
        if (pins.Count == 0)
            return SimCapability.No("No MCU GPIO outputs in the netlist to simulate.");

        if (!System.IO.File.Exists(BundlePath))
            return SimCapability.No("avr8js runtime bundle is missing from the install — reinstall to run live simulation.");

        return SimCapability.Yes(Engine);
    }

    public async Task<SimSession> StartAsync(Project.Project project, CancellationToken ct = default)
    {
        var pins = GpioPinMap.Build(project.Connections, _kb);
        var fqbn = FirmwareBuilder.Fqbn(project);
        var session = new SimSession(Engine, pins, "compiling firmware…");

        // 1) Compile to an Intel HEX — avr8js loads the HEX into program memory (not the ELF).
        var buildDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundry_avr_" + Guid.NewGuid().ToString("N")[..8]);
        var image = await FirmwareBuilder.CompileToImageAsync(project, buildDir, ct);
        if (!image.HasHex)
        {
            var why = image.Diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message ?? "firmware didn't compile";
            session.SetStatus($"can't simulate — {why}");
            session.Stop();
            CleanDir(buildDir);
            return session;
        }

        // 2) Spin up V8 and load the bundle. If V8's native lib can't load, degrade — never crash the app.
        V8ScriptEngine engine;
        try
        {
            session.SetStatus("starting avr8js…");
            engine = new V8ScriptEngine();
            var bundle = await System.IO.File.ReadAllTextAsync(BundlePath, ct);
            engine.Execute(bundle);
        }
        catch (Exception ex)
        {
            AppLog.Error("sim", $"avr8js engine load failed: {ex.Message}");
            session.SetStatus("can't simulate — the avr8js engine failed to start on this machine.");
            session.Stop();
            CleanDir(buildDir);
            return session;
        }

        // 3) Edge plumbing — IDENTICAL to RenodeSimulator's Emit so the same snapshot reaches the breadboard.
        void Emit(int gpio, bool high)
        {
            var sp = GpioPinMap.Resolve(pins, gpio);
            session.Push(new PinLevel(gpio, high, sp?.Net, sp?.Endpoint));
        }

        // 4) Build the runner inside V8: createRunner(hexText, host, mega, portMap).
        var isMega = AvrPinMap.IsMega(fqbn);
        try
        {
            var hex = await System.IO.File.ReadAllTextAsync(image.HexPath!, ct);
            engine.AddHostObject("host", new PinBridge(Emit));
            engine.AddHostObject("portMap", new PropertyBag());
            foreach (var (key, gpio) in AvrPinMap.PortMap(isMega))
                ((PropertyBag)engine.Script.portMap)[key] = gpio;

            engine.Script.hexText = hex;
            engine.Script.runner = engine.Script.Avr8.createRunner(
                engine.Script.hexText, engine.Script.host, isMega, engine.Script.portMap);
        }
        catch (Exception ex)
        {
            AppLog.Error("sim", $"avr8js runner init failed: {ex.Message}");
            session.SetStatus("can't simulate — couldn't load the compiled firmware into avr8js.");
            try { engine.Dispose(); } catch { }
            session.Stop();
            CleanDir(buildDir);
            return session;
        }

        // 5) Run loop on ONE dedicated background thread — V8 is single-threaded; confine every Script.* call.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        double speed = 1.0;
        var thread = new Thread(() => RunLoop(engine, cts.Token, () => Volatile.Read(ref speed)))
        {
            IsBackground = true,
            Name = "avr8js-run",
        };
        thread.Start();

        // 6) Lifecycle: speed feeds the pacing loop; stop cancels, joins, disposes V8, cleans the build dir.
        session.Bind(
            onSpeed: f => Volatile.Write(ref speed, Math.Max(0.01, f)),
            onStop: () =>
            {
                try { cts.Cancel(); } catch { }
                try { thread.Join(500); } catch { }
                try { engine.Dispose(); } catch { }
                try { cts.Dispose(); } catch { }
                CleanDir(buildDir);
            });

        session.SetStatus("running · avr8js");
        AppLog.Info("sim", $"simulation running · {fqbn} · avr8js · {pins.Count} pin(s)");
        return session;
    }

    /// <summary>
    /// Pace cycle batches to wall time so firmware runs roughly real-time. Each iteration advances
    /// <c>16MHz · dt · speed</c> cycles, capped so a tight busy-loop can never block the thread. All
    /// access to <paramref name="engine"/>.Script stays on this one thread.
    /// </summary>
    private static void RunLoop(V8ScriptEngine engine, CancellationToken ct, Func<double> speed)
    {
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = sw.Elapsed;
                var dt = (now - last).TotalSeconds;
                last = now;
                if (dt > 0.05) dt = 0.05;   // cap a long pause so we never fast-forward a huge batch

                long n = (long)(CpuHz * dt * speed());
                if (n > 0) engine.Script.runner.runCycles(n);

                Thread.Sleep(8);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn("sim", $"avr8js run loop ended: {ex.Message}");
        }
        catch { /* cancellation / disposed engine — expected on stop */ }
    }

    private static void CleanDir(string dir)
    {
        try { System.IO.Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// In-process bridge handed to the JS runner as <c>host</c>. avr8js's port listener calls
    /// <c>host.onPin(gpio, high)</c> only on a level change; we forward each edge to the session emitter.
    /// </summary>
    public sealed class PinBridge
    {
        private readonly Action<int, bool> _emit;
        public PinBridge(Action<int, bool> emit) => _emit = emit;

        // Called from JS — name/casing must match the bundle's host.onPin(...) call.
        public void onPin(int gpio, bool high) => _emit(gpio, high);
    }
}
