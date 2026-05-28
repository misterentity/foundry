using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Simulation;

namespace Foundry.Tests;

public class SimulationTests
{
    private static ComponentKb Kb => ComponentKb.Demo();

    // ---- Netlist pin <-> emulator GPIO mapping ----

    [Fact]
    public void GpioPinMap_MapsSignalNetsToGpioLines()
    {
        var pins = GpioPinMap.Build(DemoData.SoilMoistureConnections(), Kb);

        // The two signal nets land on the MCU: GPIO34 ↔ SENSOR.AOUT, GPIO0 ↔ BTN1.A.
        Assert.Equal(2, pins.Count);
        Assert.Contains(pins, p => p.Gpio == 34 && p.Endpoint.Equals("SENSOR.AOUT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pins, p => p.Gpio == 0 && p.Endpoint.Equals("BTN1.A", StringComparison.OrdinalIgnoreCase));

        // Lines are ordered by GPIO number for deterministic .repl/.resc output.
        Assert.Equal(new[] { 0, 34 }, pins.Select(p => p.Gpio).ToArray());

        // Each line gets a unique per-line LED peripheral name.
        Assert.Contains(pins, p => p.Gpio == 34 && p.LedName == "led34");
        Assert.Contains(pins, p => p.Gpio == 0 && p.LedName == "led0");
    }

    [Fact]
    public void GpioPinMap_IgnoresPowerAndGroundNets()
    {
        var pins = GpioPinMap.Build(DemoData.SoilMoistureConnections(), Kb);

        // Power/ground endpoints (3V3, GND, VOUT, VIN) are never simulated as GPIO lines.
        Assert.DoesNotContain(pins, p => p.Net is "power" or "ground");
        Assert.All(pins, p => Assert.Equal("signal", p.Net));
    }

    [Fact]
    public void GpioPinMap_Resolve_FindsLineByGpio()
    {
        var pins = GpioPinMap.Build(DemoData.SoilMoistureConnections(), Kb);

        var p34 = GpioPinMap.Resolve(pins, 34);
        Assert.NotNull(p34);
        Assert.Equal("SENSOR.AOUT", p34!.Endpoint, ignoreCase: true);

        Assert.Null(GpioPinMap.Resolve(pins, 99));
    }

    [Fact]
    public void GpioPinMap_Empty_WhenNoMcu()
    {
        var pins = GpioPinMap.Build(new List<Connection>(), Kb);
        Assert.Empty(pins);
    }

    // ---- PinStateSnapshot copy-on-write indexing ----

    [Fact]
    public void PinStateSnapshot_With_IndexesByGpioAndEndpoint()
    {
        var snap = PinStateSnapshot.Empty
            .With(new PinLevel(34, true, "signal", "SENSOR.AOUT"));

        Assert.True(snap.TryGetGpio(34, out var byGpio));
        Assert.True(byGpio.High);

        // Endpoint lookup is case-insensitive.
        Assert.True(snap.TryGetEndpoint("sensor.aout", out var byEp));
        Assert.Equal(34, byEp.Gpio);

        // Original Empty snapshot is untouched (copy-on-write).
        Assert.False(PinStateSnapshot.Empty.TryGetGpio(34, out _));
    }

    // ---- SimSession lifecycle driven by a FakeSimulator ----

    [Fact]
    public void FakeSimulator_CanSimulate_ReportsSupported()
    {
        var sim = new FakeSimulator();
        var cap = sim.CanSimulate(DemoData.CreateSoilMoistureProject());
        Assert.True(cap.Supported);
        Assert.Equal(SimEngine.Renode, cap.Engine);
    }

    [Fact]
    public async Task SimSession_StartEmitsUpdates_StopEndsStream()
    {
        var sim = new FakeSimulator();
        var project = DemoData.CreateSoilMoistureProject();

        using var session = await sim.StartAsync(project);

        var received = new List<PinStateSnapshot>();
        string? stoppedStatus = null;
        session.Updated += received.Add;
        session.Stopped += s => stoppedStatus = s;

        Assert.True(session.IsRunning);
        Assert.Equal("running", session.StatusMessage);

        // Engine pushes a couple of edges; each raises Updated with the cumulative snapshot.
        sim.Emit(new PinLevel(34, true, "signal", "SENSOR.AOUT"));
        sim.Emit(new PinLevel(0, true, "signal", "BTN1.A"));

        Assert.Equal(2, received.Count);
        Assert.True(received[^1].TryGetGpio(34, out var pin34) && pin34.High);
        Assert.True(received[^1].TryGetGpio(0, out var pin0) && pin0.High);
        Assert.True(session.Current.TryGetGpio(34, out _));

        // Stop ends the run: status flips, Stopped fires once with the final status.
        session.Stop();
        Assert.False(session.IsRunning);
        Assert.Equal("stopped", session.StatusMessage);
        Assert.Equal("stopped", stoppedStatus);

        // Stop is idempotent — a second Stop does not re-raise Stopped.
        int stopCount = 0;
        session.Stopped += _ => stopCount++;
        session.Stop();
        Assert.Equal(0, stopCount);
    }

    [Fact]
    public async Task SimSession_SetSpeed_ForwardsToEngine()
    {
        var sim = new FakeSimulator();
        using var session = await sim.StartAsync(DemoData.CreateSoilMoistureProject());

        session.SetSpeed(2.0);
        Assert.Equal(2.0, sim.LastSpeed);
    }

    [Fact]
    public async Task SimSession_Dispose_StopsAndDropsPushes()
    {
        var sim = new FakeSimulator();
        var session = await sim.StartAsync(DemoData.CreateSoilMoistureProject());

        int count = 0;
        session.Updated += _ => count++;

        session.Dispose();
        Assert.False(session.IsRunning);

        sim.Emit(new PinLevel(34, true, "signal", "SENSOR.AOUT"));
        Assert.Equal(0, count);
    }

    // ---- Pure Renode generators (FQBN-driven platform selection) ----

    [Theory]
    [InlineData("STM32:stm32:genericSTM32F407VGTx", "gpioPortA", "stm32f4.repl")]
    [InlineData("rp2040:rp2040:rpipico", "gpio", "rp2040.repl")]
    public void ReplPlatform_RoutesByFqbn(string fqbn, string node, string includeFragment)
    {
        var (gpioNode, include) = RenodeReplGenerator.Platform(fqbn);
        Assert.Equal(node, gpioNode);
        Assert.NotNull(include);
        Assert.Contains(includeFragment, include);
    }

    [Fact]
    public void ReplPlatform_UnknownFqbn_FallsBackToGenericGpio()
    {
        var (gpioNode, include) = RenodeReplGenerator.Platform("arduino:avr:uno");
        Assert.Equal("gpio", gpioNode);
        Assert.Null(include);
    }

    [Fact]
    public void ReplGenerator_WiresOneLedPerSimPin()
    {
        var pins = new List<SimPin> { new(13, "led13", "LED1.A", "signal") };
        var repl = RenodeReplGenerator.Build("rp2040:rp2040:rpipico", pins);

        Assert.Contains("13 -> led13@0", repl);
        Assert.Contains("led13: Miscellaneous.LED @ gpio 13", repl);
    }

    [Fact]
    public void RescGenerator_PythonMechanism_LoadsElfAndPushesEdges()
    {
        var pins = new List<SimPin> { new(13, "led13", "LED1.A", "signal") };
        var resc = RenodeRescGenerator.Build(@"C:\build\sketch.ino.elf", pins, hostPort: 7777, usepython: true);

        Assert.Contains("mach create \"foundry\"", resc);
        Assert.Contains("machine LoadPlatformDescription @foundry.repl", resc);
        Assert.Contains("sysbus LoadELF @C:/build/sketch.ino.elf", resc); // backslashes normalized
        Assert.Contains("sock.connect(('127.0.0.1', 7777))", resc);
        Assert.Contains("led13.StateChanged", resc);
        Assert.Contains("start", resc);
    }

    [Fact]
    public void RescGenerator_WatchMechanism_PollsLedState()
    {
        var pins = new List<SimPin> { new(13, "led13", "LED1.A", "signal") };
        var resc = RenodeRescGenerator.Build("/tmp/sketch.elf", pins, hostPort: 7777, usepython: false);

        Assert.Contains("watch \"sysbus.led13 State\" 100", resc);
        Assert.DoesNotContain("StateChanged", resc);
    }

    /// <summary>
    /// Test-only <see cref="ISimulator"/>. Builds a real <see cref="SimSession"/> from the demo netlist and
    /// lets the test drive engine-side pin edges via <see cref="Emit"/> (which calls the session's internal Push).
    /// </summary>
    private sealed class FakeSimulator : ISimulator
    {
        private SimSession? _session;
        public double LastSpeed { get; private set; } = 1.0;

        public SimEngine Engine => SimEngine.Renode;

        public SimCapability CanSimulate(Foundry.Core.Project.Project project) =>
            SimCapability.Yes(SimEngine.Renode, "fake");

        public Task<SimSession> StartAsync(Foundry.Core.Project.Project project, CancellationToken ct = default)
        {
            var pins = GpioPinMap.Build(project.Connections, ComponentKb.Demo());
            var session = new SimSession(SimEngine.Renode, pins, "running");
            session.Bind(onSpeed: f => LastSpeed = f, onStop: null);
            _session = session;
            return Task.FromResult(session);
        }

        /// <summary>Push a pin edge into the live session, exactly as the real engine does.</summary>
        public void Emit(PinLevel level) => _session?.Push(level);
    }
}
