using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Simulation;

namespace Foundry.Tests;

/// <summary>
/// Deterministic coverage for the avr8js engine seam: the AVR pin table, engine selection in
/// <see cref="SimulatorFactory"/>, and <see cref="Avr8jsSimulator.CanSimulate"/>. None of these tests
/// launch V8 or compile real firmware — those paths are guarded on tool availability.
/// </summary>
public class AvrSimulationTests
{
    private static ComponentKb Kb => ComponentKb.Demo();

    /// <summary>
    /// A minimal AVR project whose inferred FQBN comes from the board keyword in the prompt. Components are
    /// left empty on purpose: <see cref="Foundry.Core.Firmware.FirmwareBuilder.Fqbn"/> scans component names
    /// too, and the demo KB's "ESP32 DevKit v1" would otherwise win the board-inference race. The simulators
    /// take their KB from the injected <see cref="ComponentKb"/>, not from <c>project.Components</c>.
    /// </summary>
    private static Project AvrProject(string boardKeyword) => new()
    {
        Id = "p_avr",
        Title = "Blink",
        Prompt = $"An Arduino {boardKeyword} that blinks the onboard LED.",
        Connections = DemoData.SoilMoistureConnections(),
    };

    // ---- AvrPinMap: Uno (ATmega328P) ----

    [Fact]
    public void PortMap_Uno_MapsD13ToPortB5()
    {
        // LED_BUILTIN on the Uno/Nano is D13 = PB5.
        var map = AvrPinMap.PortMap(mega: false);
        Assert.Equal(13, map["B5"]);
    }

    [Fact]
    public void PortMap_Uno_KnownPins()
    {
        var map = AvrPinMap.PortMap(mega: false);

        // PORTD carries D0..D7, PORTB carries D8..D13, PORTC carries A0..A5 (=D14..D19).
        Assert.Equal(0, map["D0"]);
        Assert.Equal(7, map["D7"]);
        Assert.Equal(8, map["B0"]);
        Assert.Equal(14, map["C0"]);   // A0
        Assert.Equal(19, map["C5"]);   // A5
    }

    [Fact]
    public void PortMap_Uno_DoesNotMapCrystalOrResetPins()
    {
        var map = AvrPinMap.PortMap(mega: false);

        // PB6/PB7 are the crystal, PC6 is reset — none are Arduino pins.
        Assert.False(map.ContainsKey("B6"));
        Assert.False(map.ContainsKey("B7"));
        Assert.False(map.ContainsKey("C6"));
    }

    [Fact]
    public void PortMap_Uno_KeysAreCaseInsensitive()
    {
        var map = AvrPinMap.PortMap(mega: false);
        Assert.Equal(13, map["b5"]);
    }

    [Fact]
    public void PortMap_Uno_RoundTripsGpioToPortBit()
    {
        // Every Arduino pin maps to exactly one port-bit, and that port-bit maps straight back.
        var map = AvrPinMap.PortMap(mega: false);
        Assert.Equal(20, map.Count);                 // D0..D13 (14) + A0..A5 (6)
        Assert.Equal(map.Count, map.Values.Distinct().Count());  // no two ports collide on one pin
    }

    // ---- AvrPinMap: Mega (ATmega2560) ----

    [Fact]
    public void PortMap_Mega_MapsD13ToPortB7()
    {
        // On the Mega the onboard LED (D13) is PB7, not PB5.
        var map = AvrPinMap.PortMap(mega: true);
        Assert.Equal(13, map["B7"]);
    }

    [Fact]
    public void PortMap_Mega_KnownPins()
    {
        var map = AvrPinMap.PortMap(mega: true);

        Assert.Equal(0, map["E0"]);
        Assert.Equal(10, map["B4"]);
        Assert.Equal(53, map["B0"]);
        Assert.Equal(54, map["F0"]);   // A0
        Assert.Equal(69, map["K7"]);   // A15
    }

    [Fact]
    public void PortMap_Mega_DiffersFromUno()
    {
        var uno = AvrPinMap.PortMap(mega: false);
        var mega = AvrPinMap.PortMap(mega: true);

        // The Mega exposes far more pins, and D13 sits on a different bit (PB7 vs PB5).
        Assert.True(mega.Count > uno.Count);
        Assert.Equal(13, mega["B7"]);
        Assert.False(uno.ContainsKey("B7"));
    }

    // ---- AvrPinMap.IsMega ----

    [Theory]
    [InlineData("arduino:avr:mega", true)]
    [InlineData("ARDUINO:AVR:MEGA", true)]
    [InlineData("arduino:avr:uno", false)]
    [InlineData("arduino:avr:nano", false)]
    public void IsMega_DetectsBoardFamily(string fqbn, bool expected)
    {
        Assert.Equal(expected, AvrPinMap.IsMega(fqbn));
    }

    // ---- SimulatorFactory: engine selection ----

    [Theory]
    [InlineData("uno")]
    [InlineData("nano")]
    [InlineData("mega")]
    public void Factory_RoutesAvrBoardsToAvr8js(string board)
    {
        var sim = SimulatorFactory.For(AvrProject(board), Kb);
        Assert.IsType<Avr8jsSimulator>(sim);
        Assert.Equal(SimEngine.Avr8js, sim.Engine);
    }

    [Theory]
    // rp2040/pico is inferred from the prompt; STM32 isn't in the keyword table so it rides an explicit FQBN.
    [InlineData("A Raspberry Pi Pico (rp2040) project.", "")]
    [InlineData("A black pill dev board.", "STM32:stm32:genericSTM32F407VGTx")]
    public void Factory_RoutesArmBoardsToRenode(string prompt, string board)
    {
        var project = new Project
        {
            Prompt = prompt,
            Connections = DemoData.SoilMoistureConnections(),
            Firmware = new Firmware { Board = board },
        };
        var sim = SimulatorFactory.For(project, Kb);
        Assert.IsType<RenodeSimulator>(sim);
    }

    [Fact]
    public void Factory_RoutesEsp32ToUnsupported()
    {
        var project = new Project { Prompt = "An ESP32 Wi-Fi sensor.", Connections = DemoData.SoilMoistureConnections() };
        var sim = SimulatorFactory.For(project, Kb);
        Assert.IsType<UnsupportedSimulator>(sim);
        Assert.False(sim.CanSimulate(project).Supported);
    }

    // ---- Avr8jsSimulator.CanSimulate ----

    [Theory]
    [InlineData("uno")]
    [InlineData("nano")]
    [InlineData("mega")]
    public void CanSimulate_SupportsAvrBoards(string board)
    {
        var sim = new Avr8jsSimulator(Kb);
        var project = AvrProject(board);
        var cap = sim.CanSimulate(project);

        // Only assert "Supported" when the runtime bundle is actually present in the test output;
        // otherwise CanSimulate correctly reports the missing-bundle reason and we just assert the
        // board family was accepted (i.e. we didn't bail on the AVR check).
        if (System.IO.File.Exists(Avr8jsSimulator.BundlePath))
        {
            Assert.True(cap.Supported);
            Assert.Equal(SimEngine.Avr8js, cap.Engine);
        }
        else
        {
            Assert.False(cap.Supported);
            Assert.Contains("bundle", cap.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CanSimulate_RejectsNonAvrChip()
    {
        var sim = new Avr8jsSimulator(Kb);
        var project = new Project { Prompt = "An ESP32 sensor.", Connections = DemoData.SoilMoistureConnections() };

        var cap = sim.CanSimulate(project);
        Assert.False(cap.Supported);
        Assert.Contains("AVR", cap.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanSimulate_RejectsLeonardo()
    {
        var sim = new Avr8jsSimulator(Kb);
        var cap = sim.CanSimulate(AvrProject("leonardo"));

        Assert.False(cap.Supported);
        Assert.Contains("leonardo", cap.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanSimulate_RejectsWhenNoGpioOutputs()
    {
        var sim = new Avr8jsSimulator(Kb);
        var project = new Project { Prompt = "An Arduino Uno blink sketch.", Connections = new List<Connection>() };

        var cap = sim.CanSimulate(project);
        Assert.False(cap.Supported);
    }

    [Fact]
    public void Engine_IsAvr8js()
    {
        Assert.Equal(SimEngine.Avr8js, new Avr8jsSimulator(Kb).Engine);
    }
}
