using System.Diagnostics;
using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Simulation;
using Microsoft.ClearScript.V8;
using Xunit.Abstractions;

namespace Foundry.Tests;

/// <summary>
/// End-to-end proof that the avr8js engine actually runs real firmware: compile a genuine Arduino Uno
/// "blink D13" sketch with arduino-cli, feed the HEX into <see cref="Avr8jsSimulator"/> hosted in V8, and
/// confirm real PORTB bit5 -> Arduino D13 edges surface through the same <see cref="SimSession.Updated"/>
/// contract the breadboard consumes. This is the headless substitute for a human watching the LED blink.
///
/// GUARDED: skips cleanly (never hard-fails) when the toolchain is absent — no arduino-cli, no avr8js
/// runtime bundle, or a V8 native lib that won't load — so the suite stays green on bare machines.
/// </summary>
public class Avr8jsLiveSmokeTest
{
    private readonly ITestOutputHelper _out;
    public Avr8jsLiveSmokeTest(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A minimal Arduino Uno project: the demo KB's "MCU" alias (which exposes GPIO pins so
    /// <see cref="GpioPinMap"/> detects it as the MCU), one signal net from MCU.D13 to an LED, and a real
    /// blink sketch toggling pin 13 with a SHORT delay so edges arrive fast. Components are left empty so
    /// <see cref="FirmwareBuilder.Fqbn"/> infers "arduino:avr:uno" from the prompt rather than the demo KB.
    /// </summary>
    private static Project BlinkUnoProject() => new()
    {
        Id = "p_blink",
        Title = "Blink",
        Prompt = "An Arduino Uno that blinks the onboard LED on pin 13.",
        Connections = new List<Connection>
        {
            // MCU.D13 -> LED1.A on a signal net => GpioPinMap yields a SimPin with Gpio=13, Endpoint="LED1.A".
            new Connection { From = "MCU.D13", To = "LED1.A", Net = "signal" },
        },
        Firmware = new Firmware
        {
            Platform = "Arduino C++",
            Board = "arduino:avr:uno",
            Files = new List<FirmwareFile>
            {
                new FirmwareFile
                {
                    Name = "blink.ino",
                    Active = true,
                    // delay(50) -> ~10 toggles/second; millis()/delay() are driven by Timer0, which the
                    // avr8js runner constructs, so this exercises the real timer path too.
                    Content = """
                    void setup() {
                      pinMode(13, OUTPUT);
                    }
                    void loop() {
                      digitalWrite(13, HIGH);
                      delay(50);
                      digitalWrite(13, LOW);
                      delay(50);
                    }
                    """,
                },
            },
        },
    };

    /// <summary>
    /// Returns a skip reason when the live toolchain isn't usable on this machine, else null. Checks both
    /// the compiler (<see cref="FirmwareBuilder.Locate"/>), the shipped avr8js bundle, and that V8 actually
    /// constructs — so a machine missing any of them skips instead of failing.
    /// </summary>
    private static string? SkipReason()
    {
        if (FirmwareBuilder.Locate() is null)
            return "arduino-cli not installed — skipping live avr8js smoke test.";
        if (!System.IO.File.Exists(Avr8jsSimulator.BundlePath))
            return "avr8js runtime bundle missing from test output — skipping live smoke test.";
        try { using var probe = new V8ScriptEngine(); }
        catch (Exception ex) { return $"ClearScript V8 won't start here ({ex.Message}) — skipping live smoke test."; }
        return null;
    }

    [Fact]
    public async Task Blink_RealUnoFirmware_EmitsD13TransitionsThroughAvr8js()
    {
        var skip = SkipReason();
        if (skip is not null)
        {
            _out.WriteLine(skip);
            return;   // graceful skip — toolchain absent, suite stays green
        }

        var project = BlinkUnoProject();

        // Sanity: the netlist must produce a D13 SimPin, or there is nothing to observe.
        var simPins = GpioPinMap.Build(project.Connections, ComponentKb.Demo());
        Assert.Contains(simPins, p => p.Gpio == 13);

        // Collect distinct D13 edges (de-duplicated by level so we count real transitions, not repeats).
        var d13Levels = new List<bool>();
        var gate = new object();
        void OnUpdated(PinStateSnapshot snap)
        {
            if (!snap.TryGetGpio(13, out var lvl)) return;
            lock (gate)
            {
                if (d13Levels.Count == 0 || d13Levels[^1] != lvl.High)
                    d13Levels.Add(lvl.High);
            }
        }

        var sw = Stopwatch.StartNew();
        using var session = await new Avr8jsSimulator(ComponentKb.Demo()).StartAsync(project);
        session.Updated += OnUpdated;

        // The session may have already gone non-running if compile/engine setup degraded; surface why.
        Assert.True(session.IsRunning,
            $"avr8js session did not start running. status='{session.StatusMessage}'");

        // Poll up to ~12s for at least 2 distinct transitions (HIGH->LOW->HIGH proves the loop ran).
        var deadline = TimeSpan.FromSeconds(12);
        int observed;
        do
        {
            await Task.Delay(100);
            lock (gate) observed = d13Levels.Count;
        }
        while (observed < 2 && sw.Elapsed < deadline);

        session.Stop();
        sw.Stop();

        lock (gate) observed = d13Levels.Count;
        _out.WriteLine($"D13 distinct transitions observed: {observed} over {sw.Elapsed.TotalSeconds:F1}s; status='{session.StatusMessage}'");

        Assert.True(observed >= 2,
            $"Expected at least 2 distinct D13 transitions from the real blink firmware, but saw {observed} " +
            $"in {sw.Elapsed.TotalSeconds:F1}s (session status '{session.StatusMessage}'). " +
            "The firmware did not toggle pin 13 through avr8js -> C#.");
    }
}
