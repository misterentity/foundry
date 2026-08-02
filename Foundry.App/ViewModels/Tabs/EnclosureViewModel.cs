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

public sealed partial class EnclosureViewModel : TabViewModelBase
{
    private readonly Foundry.Core.Generation.ProjectGenerator? _ai;
    [ObservableProperty] private string _view = "ISO";
    [ObservableProperty] private bool _meshReady;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _showOffline;
    [ObservableProperty] private bool _sidecarOnline;
    [ObservableProperty] private string _sidecarStatus = "connecting to CAD sidecar…";
    [ObservableProperty] private byte[]? _stlBytes;

    // v2 Phase B/C/D: AI-written OpenSCAD ("Advanced" mode) + parametric sliders
    [ObservableProperty] private bool _advanced;
    [ObservableProperty] private string _scad = "";
    [ObservableProperty] private bool _scadBusy;
    [ObservableProperty] private string _scadStatus = "";
    [ObservableProperty] private string _scadError = "";
    public bool OpenScadInstalled => Foundry.Core.Cad.OpenScadInstaller.IsInstalled;
    public ObservableCollection<ScadParamRow> Parameters { get; } = new();
    public bool HasScadError => !string.IsNullOrEmpty(ScadError);
    partial void OnScadErrorChanged(string value) => OnPropertyChanged(nameof(HasScadError));

    private System.Windows.Threading.DispatcherTimer? _scadDebounce;
    private System.Threading.CancellationTokenSource? _scadCts;
    [RelayCommand] private void CancelScad() => _scadCts?.Cancel();

    public EnclosureViewModel(Project project, Foundry.Core.Generation.ProjectGenerator? ai = null) : base(project)
    {
        _ai = ai;
        _scad = project.Enclosure.Scad ?? "";
        _ = LoadMeshAsync();
    }

    public Enclosure E => Project.Enclosure;
    public string WallText => E.Wall.ToString("0.0");
    public string LengthText => Dim(0);
    public string WidthText => Dim(1);
    public string HeightText => Dim(2);
    private string Dim(int i) => E.Inner is { } a && a.Length > i ? a[i].ToString("0") : "—";

    // purpose-built feature readouts
    public string LidText => E.Lid?.ToLowerInvariant() == "screw" ? "Screw-down" : "Snap-fit";
    public string MountText => E.Mount?.ToLowerInvariant() switch { "wall-tabs" => "Wall tabs", "flange" => "Perimeter flange", _ => "None" };
    public string StandoffText => E.Standoffs > 0 ? $"{E.Standoffs} bosses" : "None";
    public int VentCount => E.Vents.Sum(v => v.Count);
    public string VentText => VentCount > 0 ? $"{VentCount} slots ({string.Join("/", E.Vents.Select(v => v.Face))})" : "None";

    private async Task LoadMeshAsync()
    {
        try
        {
            var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync();
            if (client is null)
            {
                SidecarOnline = false;
                IsLoading = false;
                ShowOffline = true;
                SidecarStatus = $"CAD sidecar offline ({Foundry.Core.Sidecar.SidecarHost.Shared.StatusMessage})";
                return;
            }
            var schema = Foundry.Core.Sidecar.EnclosureSchema.ToJson(
                E, board: Foundry.Core.Cad.EnclosureFit.PlaceBoard(Project));
            var mesh = await client.BuildEnclosureAsync(schema);
            StlBytes = mesh.Stl;
            SidecarOnline = true;
            IsLoading = false;
            MeshReady = true;
            SidecarStatus = $"{mesh.Kernel} · {mesh.Triangles} tris · {client.BaseUrl}";
        }
        catch (Exception ex)
        {
            SidecarOnline = false;
            IsLoading = false;
            ShowOffline = true;
            SidecarStatus = $"CAD sidecar error: {ex.Message}";
        }
    }

    // ----- v2 Phase B/C: Advanced OpenSCAD mode -----
    [RelayCommand]
    private async Task ToggleAdvanced()
    {
        Advanced = !Advanced;
        if (!Advanced) { await LoadMeshAsync(); return; }   // back to schema render
        if (!OpenScadInstalled) { ScadStatus = "OpenSCAD isn't installed — click INSTALL OPENSCAD."; return; }
        if (string.IsNullOrWhiteSpace(Scad)) await GenerateScad();
        else await RenderScad();
    }

    /// <summary>Ask the AI to write parametric OpenSCAD for this enclosure (PRD v2 Phase B).</summary>
    [RelayCommand]
    private async Task GenerateScad()
    {
        if (ScadBusy || _ai is null) return;
        ScadBusy = true;
        ScadStatus = "Writing OpenSCAD…";
        try
        {
            var (ok, scad, msg) = await _ai.GenerateEnclosureScadAsync(Project);
            if (!ok) { ScadStatus = msg; return; }
            Scad = scad;
            Project.Enclosure.Scad = scad;
            RebuildParameters();
            ScadStatus = $"Generated · {scad.Length} chars · {Parameters.Count} parameters · rendering…";
            await RenderScad();
        }
        catch (Exception ex) { ScadStatus = $"Failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    /// <summary>Render the current SCAD via the sidecar's OpenSCAD path; updates the 3D preview.</summary>
    [RelayCommand]
    private async Task RenderScad()
    {
        if (string.IsNullOrWhiteSpace(Scad)) { ScadStatus = "No SCAD yet — click REGENERATE."; return; }
        ScadBusy = true;
        _scadCts?.Cancel();
        _scadCts = new System.Threading.CancellationTokenSource();
        var ct = _scadCts.Token;
        try
        {
            var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync(ct);
            if (client is null) { ScadStatus = "CAD sidecar offline."; return; }
            ScadStatus = "Rendering with OpenSCAD…";
            var r = await client.RenderScadAsync(Scad, "stl", ct);
            if (!r.Ok)
            {
                ScadStatus = "OpenSCAD couldn't build the script.";
                ScadError = ExtractScadError(r.Error);
                return;
            }
            ScadError = "";
            StlBytes = r.Bytes;
            IsLoading = false; ShowOffline = false; MeshReady = true;
            // Preview only — EXPORT always uses the deterministic schema geometry, not this AI-authored SCAD.
            ScadStatus = $"Rendered with OpenSCAD · {r.Bytes.Length:N0} bytes — experimental preview (EXPORT uses the schema geometry)";
            // Mirror the active render in the on-model status badge so it no longer reads the stale
            // schema-build value once Advanced takes over. Schema rebuilds (LoadMeshAsync) overwrite this.
            SidecarStatus = $"openscad · {(r.Format.Length == 0 ? "stl" : r.Format)} · {r.Bytes.Length:N0} bytes · {client.BaseUrl}";
        }
        catch (OperationCanceledException) { ScadStatus = "Render cancelled."; }
        catch (Exception ex) { ScadStatus = $"Render failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    /// <summary>Have the AI fix an OpenSCAD compile error (mirrors firmware FIX BUILD).</summary>
    [RelayCommand]
    private async Task FixScad()
    {
        if (_ai is null || ScadBusy || string.IsNullOrWhiteSpace(ScadError)) return;
        ScadBusy = true;
        ScadStatus = "Asking the AI to fix the SCAD…";
        try
        {
            var (ok, scad, _) = await _ai.FixEnclosureScadAsync(Project, Scad, ScadError);
            if (!ok) { ScadStatus = "Couldn't generate a SCAD fix."; return; }
            Scad = scad; Project.Enclosure.Scad = scad;
            RebuildParameters();
            ScadStatus = "Reworked the SCAD — rendering…";
            await RenderScad();
        }
        catch (Exception ex) { ScadStatus = $"Fix failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    private void RebuildParameters()
    {
        Parameters.Clear();
        foreach (var p in Foundry.Core.Cad.ScadParameters.Parse(Scad))
            Parameters.Add(new ScadParamRow(p, OnParamChanged));
    }

    private void OnParamChanged(ScadParamRow row)
    {
        Scad = Foundry.Core.Cad.ScadParameters.Patch(Scad, row.Name, row.Value);
        Project.Enclosure.Scad = Scad;
        // debounce 600ms so dragging a slider doesn't fire one OpenSCAD call per pixel
        _scadDebounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _scadDebounce.Stop();
        _scadDebounce.Tick -= ScadDebounceTick;
        _scadDebounce.Tick += ScadDebounceTick;
        _scadDebounce.Start();
    }
    private async void ScadDebounceTick(object? sender, EventArgs e)
    {
        _scadDebounce?.Stop();
        await RenderScad();
    }

    private static string ExtractScadError(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("stderr", out var s)) return s.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("detail", out var d)) return d.GetString() ?? raw;
        }
        catch { }
        return raw;
    }

    /// <summary>Download a portable OpenSCAD to the app tools folder (one-time, ~22 MB).</summary>
    [RelayCommand]
    private async Task InstallOpenScad()
    {
        if (ScadBusy) return;
        ScadBusy = true;
        ScadStatus = "Downloading OpenSCAD (~22 MB)…";
        try
        {
            await Foundry.Core.Cad.OpenScadInstaller.DownloadAsync();
            OnPropertyChanged(nameof(OpenScadInstalled));
            ScadStatus = "OpenSCAD installed. Click GENERATE to write the parametric SCAD.";
        }
        catch (Exception ex) { ScadStatus = $"Install failed: {ex.Message}"; }
        finally { ScadBusy = false; }
    }

    public string ExportLabel => $"EXPORT {((ConfigStore.Load().EnclosureFormat ?? "STL").ToUpperInvariant() == "3MF" ? "3MF" : "STL")}";

    /// <summary>Export the enclosure mesh in the configured format (STL or 3MF) to the export folder (PRD F7).</summary>
    [RelayCommand]
    private async Task ExportStl()
    {
        try
        {
            var fmt = (ConfigStore.Load().EnclosureFormat ?? "STL").ToLowerInvariant() == "3mf" ? "3mf" : "stl";
            // Determinism boundary: EXPORT is ALWAYS the deterministic schema→mesh build (enclosure.py CSG),
            // never the AI-authored OpenSCAD preview. AI fills the schema; geometry is computed from it.
            var client = await Foundry.Core.Sidecar.SidecarHost.Shared.StartAsync();
            if (client is null) { SidecarStatus = "can't export — CAD sidecar offline"; return; }
            // "print", not the preview arrangement: the file the user takes away must be slicable.
            var mesh = await client.BuildEnclosureAsync(Foundry.Core.Sidecar.EnclosureSchema.ToJson(
                E, fmt, arrange: "print", board: Foundry.Core.Cad.EnclosureFit.PlaceBoard(Project)));
            var data = mesh.Stl;
            if (data is null || data.Length == 0) { SidecarStatus = "no mesh to export"; return; }
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"enclosure.{fmt}");
            File.WriteAllBytes(path, data);
            Foundry.Core.Diagnostics.AppLog.Info("export", $"enclosure {fmt.ToUpperInvariant()} → {path}");
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            SidecarStatus = $"{fmt.ToUpperInvariant()} exported to {path} — schema-built geometry; check fit/cutouts before printing.";
        }
        catch (Exception ex) { SidecarStatus = $"export failed: {ex.Message}"; }
    }
}
