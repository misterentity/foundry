namespace Foundry.Core.Simulation;

/// <summary>Which emulator backs a simulation. Renode covers ARM Cortex-M; avr8js is the AVR fallback.</summary>
public enum SimEngine { Renode, Avr8js }

/// <summary>
/// Whether a project can be simulated and by which engine. <see cref="Reason"/> is the user-facing
/// explanation shown when <see cref="Supported"/> is false (e.g. "ESP32 has no live model — flash to run").
/// </summary>
public sealed record SimCapability(bool Supported, SimEngine? Engine, string Reason)
{
    public static SimCapability No(string reason) => new(false, null, reason);
    public static SimCapability Yes(SimEngine engine, string reason = "") => new(true, engine, reason);
}

/// <summary>
/// A simulation backend. Implementations decide whether they can run a project (<see cref="CanSimulate"/>)
/// and, if so, spin up a live <see cref="SimSession"/> that streams pin state. Kept deliberately small so
/// tests can supply a fake.
/// </summary>
public interface ISimulator
{
    SimEngine Engine { get; }

    /// <summary>Pure capability check — does NOT start anything. Safe to call on the UI thread.</summary>
    SimCapability CanSimulate(Project.Project project);

    /// <summary>
    /// Start a live session for <paramref name="project"/>. Compiles/loads firmware as needed and begins
    /// streaming pin updates via <see cref="SimSession.Updated"/>. Throws on unrecoverable setup failure;
    /// callers should guard with <see cref="CanSimulate"/> first.
    /// </summary>
    Task<SimSession> StartAsync(Project.Project project, CancellationToken ct = default);
}
