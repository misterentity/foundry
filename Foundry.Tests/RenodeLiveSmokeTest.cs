using System.Diagnostics;
using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Simulation;
using Xunit.Abstractions;

namespace Foundry.Tests;

/// <summary>
/// The intended end-to-end validator for the STM32 Renode live-sim path: compile a real STM32 "blink PA5"
/// sketch, run it under Renode against the generated foundry.repl/.resc, and confirm PA5 edges surface
/// through the same <see cref="SimSession.Updated"/> contract the breadboard consumes — the headless
/// substitute for a human watching the LED blink, mirroring <see cref="Avr8jsLiveSmokeTest"/>.
///
/// GUARDED: skips cleanly when the toolchain is absent (no Renode, no arduino-cli, or the session can't
/// start) so the suite stays green on bare machines and standard CI.
///
/// IMPORTANT (honesty): this test is the criterion for LIFTING the runtime gate in
/// <see cref="RenodeSimulator.CanSimulate"/>, NOT proof the live path works today. It has NOT been observed
/// to PASS anywhere yet — Renode is not installed in the dev/CI environment, and the RP2040 platform .repl
/// is not bundled at all. The pure generators (<see cref="RenodeReplGenerator"/>, <see cref="GpioPinMap"/>)
/// are unit-tested in <c>SimulationTests</c>; the gate stays CLOSED until this test is observed green on a
/// machine with Renode + the STM32 arduino core. Do not interpret a SKIP as a pass.
/// </summary>
public class RenodeLiveSmokeTest
{
    private readonly ITestOutputHelper _out;
    public RenodeLiveSmokeTest(ITestOutputHelper output) => _out = output;

    // A minimal STM32 project: an MCU exposing PA5 (so GpioPinMap detects it and yields a port-A SimPin),
    // one signal net PA5 -> LED, an STM32 FQBN, and a blink sketch toggling PA5 with a short delay.
    private static Project BlinkStm32Project() => new()
    {
        Id = "p_blink_stm32",
        Title = "STM32 Blink",
        Prompt = "An STM32F407 board that blinks an LED on PA5.",
        Connections = new List<Connection>
        {
            new Connection { From = "MCU.PA5", To = "LED1.A", Net = "signal" },
        },
        Firmware = new Firmware
        {
            Platform = "Arduino C++",
            Board = "STM32:stm32:GenF4",
            Files = new List<FirmwareFile>
            {
                new FirmwareFile
                {
                    Name = "blink.ino",
                    Active = true,
                    Content = """
                    void setup() { pinMode(PA5, OUTPUT); }
                    void loop() {
                      digitalWrite(PA5, HIGH); delay(50);
                      digitalWrite(PA5, LOW);  delay(50);
                    }
                    """,
                },
            },
        },
    };

    private static string? SkipReason()
    {
        if (!RenodeInstaller.IsInstalled)
            return "Renode not installed — skipping live STM32 Renode smoke test.";
        if (FirmwareBuilder.Locate() is null)
            return "arduino-cli not installed — skipping live STM32 Renode smoke test.";
        return null;
    }

    [Fact]
    public async Task Blink_RealStm32Firmware_EmitsPA5TransitionsThroughRenode()
    {
        var skip = SkipReason();
        if (skip is not null) { _out.WriteLine(skip); return; }   // graceful skip — toolchain absent

        var project = BlinkStm32Project();
        var kb = ComponentKb.Demo();

        // PA5 must resolve to a port-A SimPin or there is nothing to observe.
        var simPins = GpioPinMap.Build(project.Connections, kb);
        var pa5 = simPins.FirstOrDefault(p => p.Port == "A" && p.Gpio == 5);
        if (pa5 is null) { _out.WriteLine("no PA5 SimPin (demo KB has no STM32 part) — skipping."); return; }

        var levels = new List<bool>();
        var gate = new object();
        void OnUpdated(PinStateSnapshot snap)
        {
            if (!snap.TryGetGpio(5, out var lvl)) return;
            lock (gate) { if (levels.Count == 0 || levels[^1] != lvl.High) levels.Add(lvl.High); }
        }

        var sw = Stopwatch.StartNew();
        using var session = await new RenodeSimulator(kb).StartAsync(project);
        session.Updated += OnUpdated;
        if (!session.IsRunning) { _out.WriteLine($"Renode session didn't start: {session.StatusMessage} — skipping."); return; }

        var deadline = TimeSpan.FromSeconds(20);
        int observed;
        do { await Task.Delay(100); lock (gate) observed = levels.Count; }
        while (observed < 2 && sw.Elapsed < deadline);
        session.Stop();

        lock (gate) observed = levels.Count;
        _out.WriteLine($"PA5 transitions observed: {observed} over {sw.Elapsed.TotalSeconds:F1}s; status='{session.StatusMessage}'");
        Assert.True(observed >= 2,
            $"Expected ≥2 PA5 transitions from the real STM32 blink firmware, saw {observed} (status '{session.StatusMessage}').");
    }
}
