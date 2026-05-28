using Foundry.Core.Firmware;
using Foundry.Core.Kb;

namespace Foundry.Core.Simulation;

/// <summary>
/// The single place the UI picks a simulation engine. Routes a project to the right
/// <see cref="ISimulator"/> by its inferred FQBN: avr8js for Arduino AVR boards (Uno/Nano/Mega), Renode
/// for STM32/RP2040, and a no-op <see cref="UnsupportedSimulator"/> for everything else (whose
/// <see cref="ISimulator.CanSimulate"/> explains why). Keeps <see cref="SimulationViewModel"/> engine-agnostic.
/// </summary>
public static class SimulatorFactory
{
    /// <summary>Return the simulator that should handle <paramref name="project"/>. Never null.</summary>
    public static ISimulator For(Project.Project project, ComponentKb? kb = null)
    {
        var fqbn = FirmwareBuilder.Fqbn(project).ToLowerInvariant();

        if (fqbn.Contains("avr") || fqbn.Contains("uno") || fqbn.Contains("nano")
            || fqbn.Contains("mega") || fqbn.Contains("leonardo"))
            return new Avr8jsSimulator(kb);

        if (fqbn.Contains("stm32") || fqbn.Contains("rp2040") || fqbn.Contains("pico"))
            return new RenodeSimulator(kb);

        return new UnsupportedSimulator(fqbn);
    }
}

/// <summary>
/// Fallback simulator for chips no engine models (e.g. ESP32/ESP8266). Always reports unsupported and
/// never starts a run — keeps <see cref="SimulatorFactory.For"/> total so the UI never deals with null.
/// </summary>
public sealed class UnsupportedSimulator : ISimulator
{
    private readonly string _fqbn;
    public UnsupportedSimulator(string fqbn) => _fqbn = fqbn;

    public SimEngine Engine => SimEngine.Renode;

    public SimCapability CanSimulate(Project.Project project) =>
        SimCapability.No($"No live simulation model for {_fqbn} — flash to run.");

    public Task<SimSession> StartAsync(Project.Project project, CancellationToken ct = default)
    {
        var session = new SimSession(Engine, Array.Empty<SimPin>(),
            $"No live simulation model for {_fqbn} — flash to run.");
        session.Stop();
        return Task.FromResult(session);
    }
}
