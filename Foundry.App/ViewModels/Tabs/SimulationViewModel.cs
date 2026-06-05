using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Config;
using Foundry.Core.Export;
using Foundry.Core.Firmware;
using Foundry.Core.Project;
using Foundry.Core.Simulation;
using Foundry.Core.Sourcing;
using Foundry.Core.Validation;
using Microsoft.Win32;

namespace Foundry.App.ViewModels;

/// <summary>
/// Live-simulation control for the Wiring tab (Track A step 3/4). Asks <see cref="SimulatorFactory"/> for the
/// right engine for this board — avr8js for Arduino AVR (Uno/Nano/Mega), Renode for STM32/RP2040 — then
/// compiles/loads the firmware, starts the session, and streams per-GPIO edges into <see cref="LivePinState"/>,
/// which the breadboard binds to and renders as glowing pins/wires. Engine-agnostic: the same one
/// <c>pin=level</c> contract drives the UI regardless of which engine produced the edges, so the install
/// affordance (INSTALL RENODE) only surfaces for the Renode engine when it's the chosen one and missing.
/// </summary>
public sealed partial class SimulationViewModel : ObservableObject
{
    private readonly Project _project;
    private readonly ISimulator _simulator;
    private SimSession? _session;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private PinStateSnapshot? _livePinState;
    [ObservableProperty] private bool _renodeInstalled;

    /// <summary>True when the chosen engine is Renode (which has an on-demand install step).</summary>
    private readonly bool _usesRenode;

    /// <summary>Raised just before a run starts so the Wiring view can flip to the breadboard renderer.</summary>
    public event Action? RunStarting;

    public SimulationViewModel(Project project, ISimulator? simulator = null)
    {
        _project = project;
        _simulator = simulator ?? SimulatorFactory.For(project);
        _usesRenode = _simulator.Engine == SimEngine.Renode;
        RenodeInstalled = RenodeInstaller.IsInstalled;
        var cap = _simulator.CanSimulate(project);
        CanSimulate = cap.Supported;
        Status = cap.Supported
            ? (NeedsRenode ? cap.Reason : "Ready to simulate — press RUN.")
            : cap.Reason;
    }

    /// <summary>Whether this board has any live-simulation model at all (false ⇒ "flash to run").</summary>
    public bool CanSimulate { get; }

    /// <summary>Only the Renode engine needs a one-time install; avr8js runs in-process.</summary>
    public bool NeedsRenode => CanSimulate && _usesRenode && !RenodeInstalled;

    partial void OnRenodeInstalledChanged(bool value) => OnPropertyChanged(nameof(NeedsRenode));

    partial void OnSpeedChanged(double value) => _session?.SetSpeed(value);

    /// <summary>Compile → start the engine → subscribe to pin edges (marshalled to the UI thread).</summary>
    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning || IsStarting || !CanSimulate) return;
        if (_usesRenode && !RenodeInstaller.IsInstalled) { Status = "Renode isn't installed — click INSTALL RENODE."; return; }

        IsStarting = true;
        RunStarting?.Invoke();
        Status = "Starting simulation…";
        _cts = new CancellationTokenSource();
        try
        {
            var session = await _simulator.StartAsync(_project, _cts.Token);
            _session = session;
            LivePinState = session.Current;
            Status = session.StatusMessage;

            if (!session.IsRunning)
            {
                // The simulator degrades gracefully (compile/engine failure) — it returns a stopped session.
                session.Dispose();
                _session = null;
                return;
            }

            session.Updated += OnSessionUpdated;
            session.Stopped += OnSessionStopped;
            session.SetSpeed(Speed);
            IsRunning = true;
            Foundry.Core.Diagnostics.AppLog.Info("sim", $"UI sim started · {session.Pins.Count} pin(s)");
        }
        catch (OperationCanceledException) { Status = "Start cancelled."; }
        catch (Exception ex) { Status = $"Couldn't start simulation: {ex.Message}"; }
        finally { IsStarting = false; }
    }

    private void OnSessionUpdated(PinStateSnapshot snapshot) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            LivePinState = snapshot;
            if (_session is not null) Status = _session.StatusMessage;
        }));

    private void OnSessionStopped(string final) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            IsRunning = false;
            Status = final;
        }));

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        var s = _session;
        _session = null;
        if (s is not null)
        {
            s.Updated -= OnSessionUpdated;
            s.Stopped -= OnSessionStopped;
            s.Dispose();
        }
        IsRunning = false;
        LivePinState = null;
        Status = "Stopped.";
    }

    /// <summary>Download a portable Renode to the app tools folder on demand (one-time).</summary>
    [RelayCommand]
    private async Task InstallRenode()
    {
        if (IsStarting || !_usesRenode) return;
        IsStarting = true;
        Status = "Downloading Renode (~120 MB, one-time)…";
        try
        {
            await RenodeInstaller.DownloadAsync();
            RenodeInstalled = RenodeInstaller.IsInstalled;
            Status = RenodeInstalled ? "Renode installed — press RUN to simulate." : "Renode install didn't complete.";
        }
        catch (Exception ex) { Status = $"Install failed: {ex.Message}"; }
        finally { IsStarting = false; }
    }
}
