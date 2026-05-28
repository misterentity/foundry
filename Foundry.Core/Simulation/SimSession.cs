namespace Foundry.Core.Simulation;

/// <summary>
/// A live simulation run. Holds the current <see cref="PinStateSnapshot"/>, raises <see cref="Updated"/>
/// whenever a pin changes, and exposes lifecycle controls (<see cref="SetSpeed"/>, <see cref="Stop"/>,
/// <see cref="Dispose"/>). The engine pushes edges in via the internal <see cref="Push"/>; the UI consumes
/// <see cref="Updated"/> (marshalling to the dispatcher) and binds <see cref="Current"/> to the breadboard's
/// LivePinState. Engine-agnostic by design — Renode and the avr8js fallback both feed it the same way.
/// </summary>
public sealed class SimSession : IDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<SimPin> _pins;
    private Action<double>? _onSpeed;
    private Action? _onStop;
    private bool _disposed;

    public SimSession(SimEngine engine, IReadOnlyList<SimPin> pins, string status = "starting…")
    {
        Engine = engine;
        _pins = pins;
        StatusMessage = status;
        Current = PinStateSnapshot.Empty;
        IsRunning = true;
    }

    public SimEngine Engine { get; }
    public IReadOnlyList<SimPin> Pins => _pins;

    public PinStateSnapshot Current { get; private set; }
    public bool IsRunning { get; private set; }
    public string StatusMessage { get; private set; }

    /// <summary>Raised on every pin-state change with the new full snapshot. Marshal to the UI thread.</summary>
    public event Action<PinStateSnapshot>? Updated;

    /// <summary>Raised when the run stops (engine exit, error, or explicit Stop). Carries the final status.</summary>
    public event Action<string>? Stopped;

    /// <summary>Wire engine-side hooks for speed/stop. Called once by the simulator that created the session.</summary>
    public void Bind(Action<double>? onSpeed, Action? onStop)
    {
        _onSpeed = onSpeed;
        _onStop = onStop;
    }

    public void SetStatus(string status)
    {
        StatusMessage = status;
    }

    /// <summary>Adjust emulation speed (1.0 = real time). Forwarded to the engine if it supports it.</summary>
    public void SetSpeed(double factor)
    {
        try { _onSpeed?.Invoke(factor); } catch { }
    }

    /// <summary>Feed one pin edge from the engine. Updates <see cref="Current"/> and raises <see cref="Updated"/>.</summary>
    internal void Push(PinLevel level)
    {
        PinStateSnapshot next;
        lock (_gate)
        {
            if (_disposed) return;
            next = Current.With(level);
            Current = next;
        }
        try { Updated?.Invoke(next); } catch { }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;
        }
        try { _onStop?.Invoke(); } catch { }
        StatusMessage = "stopped";
        try { Stopped?.Invoke(StatusMessage); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }
}
