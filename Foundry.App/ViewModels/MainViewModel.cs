using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Ai;
using Foundry.Core.Config;
using Foundry.Core.Diagnostics;
using Foundry.Core.Generation;
using Foundry.Core.Project;
using Foundry.Core.Security;
using Foundry.Core.Sourcing;

namespace Foundry.App.ViewModels;

/// <summary>
/// Root view model + screen router (PRD §12). Holds the active Project (created by generation or by
/// loading the sample — no canned project on startup) and swaps the active screen view model.
/// Services (AI client, pipeline, sourcing) are rebuilt from Credential Manager whenever keys change,
/// so a key entered in onboarding/Settings takes effect immediately without a restart.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICredentialStore _credentials;
    private IAnthropicClient _ai = new StubAnthropicClient();
    private IPipeline _pipeline = new ChatPipeline(new StubAnthropicClient());

    [ObservableProperty] private ObservableObject _currentView = null!;

    // Dispose the outgoing view when it's swapped out (e.g. ShellViewModel detaches from the static
    // AppLog.Logged event) so it — and its whole tab/project graph — can be collected.
    partial void OnCurrentViewChanging(ObservableObject oldValue, ObservableObject newValue)
    {
        if (!ReferenceEquals(oldValue, newValue) && oldValue is IDisposable d) d.Dispose();
    }
    [ObservableProperty] private IReadOnlyList<string> _crumbs = new[] { "Foundry", "Setup" };

    // status-bar state (reflects the active model + whether a key is connected)
    [ObservableProperty] private string _modelLabel = "";
    [ObservableProperty] private string _keyLabel = "";
    [ObservableProperty] private string _keyDotSeverity = "warn";

    // global AI activity (any in-flight Claude call)
    [ObservableProperty] private bool _aiBusy;
    [ObservableProperty] private string _aiActivityLabel = "";

    public Project? Project { get; private set; }
    public string AppVersionLabel => $"v{Foundry.Core.AppInfo.Version}";

    /// <summary>True when the active project is library-backed (persist edits); false for the ephemeral sample.</summary>
    private bool _tracked;

    public MainViewModel(ICredentialStore credentials)
    {
        _credentials = credentials;
        Foundry.Core.Diagnostics.AiActivity.Changed += OnAiActivityChanged;
        RefreshServices();

        if (_credentials.Exists(CredentialStore.AnthropicTarget))
            ShowProjects();
        else
            ShowOnboarding();
    }

    private void OnAiActivityChanged()
    {
        void Apply()
        {
            AiBusy = Foundry.Core.Diagnostics.AiActivity.Busy;
            var label = Foundry.Core.Diagnostics.AiActivity.Label ?? "";
            var queued = Foundry.Core.Diagnostics.AiActivity.InFlight - 1;   // others waiting behind the running call
            AiActivityLabel = queued > 0 ? $"{label} · {queued} queued" : label;
        }
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) Apply();
        else disp.Invoke(Apply);
    }

    /// <summary>Rebuild AI/pipeline/sourcing from the currently stored keys.</summary>
    private void RefreshServices()
    {
        var anthropicKey = _credentials.Read(CredentialStore.AnthropicTarget);
        bool hasKey = !string.IsNullOrWhiteSpace(anthropicKey);
        var cfg = ConfigStore.Load();
        _ai = hasKey ? new AnthropicClient(anthropicKey!, cfg.MaxOutputTokens, temperature: cfg.Temperature) : new StubAnthropicClient();
        var modelId = cfg.ModelId;
        _pipeline = new ChatPipeline(_ai, modelId);

        var nexarKey = _credentials.Read(CredentialStore.NexarTarget);
        SourcingService.Shared = new SourcingService(
            string.IsNullOrWhiteSpace(nexarKey) ? new NullSourcingProvider() : new NexarSourcingProvider(nexarKey));

        // Fab order (v2.7): prefer a keyed PCBWay (live quotes), then a keyed JLCPCB (estimate + handoff),
        // else the offline fallback. Keys are user-provided; the order path always stops at an assisted handoff.
        var pcbWayKey = _credentials.Read(CredentialStore.PcbWayTarget);
        var jlcpcbConfigured = _credentials.Exists(CredentialStore.JlcpcbTarget);
        Foundry.Core.Pcb.Fab.FabService.Shared = new Foundry.Core.Pcb.Fab.FabService(
            Foundry.Core.Pcb.Fab.FabService.Select(pcbWayKey, jlcpcbConfigured));

        AppLog.Info("services", $"rebuilt · model {modelId} · key {(hasKey ? "connected" : "none")} · sourcing {(string.IsNullOrWhiteSpace(nexarKey) ? "offline" : "Nexar")} · fab {Foundry.Core.Pcb.Fab.FabService.Shared.ProviderName}");
        ModelLabel = FormatModel(modelId);
        KeyLabel = hasKey ? "KEY CONNECTED" : "NO KEY · OFFLINE";
        KeyDotSeverity = hasKey ? "ok" : "warn";
    }

    /// <summary>"claude-opus-4-8" → "CLAUDE · OPUS 4.8" (prefers the catalog display name).</summary>
    private static string FormatModel(string id)
    {
        var dn = ModelCatalog.Fallback.FirstOrDefault(m => m.Id == id)?.DisplayName;
        if (dn is not null) return "CLAUDE · " + dn.Replace("Claude ", "").ToUpperInvariant();
        var parts = id.Replace("claude-", "").Split('-');
        return parts.Length >= 2
            ? $"CLAUDE · {parts[0].ToUpperInvariant()} {string.Join(".", parts.Skip(1))}"
            : "CLAUDE · " + id.ToUpperInvariant();
    }

    public void ShowOnboarding()
    {
        CurrentView = new OnboardingViewModel(_credentials, _ai, onDone: () => { RefreshServices(); ShowProjects(); });
        Crumbs = new[] { "Foundry", "Setup" };
    }

    public void ShowProjects()
    {
        PersistTracked();   // save any edits made in the workspace before leaving it
        CurrentView = new ProjectsViewModel(onNew: ShowNewProject, onOpen: OpenSaved);
        Crumbs = new[] { "Foundry", "Library" };
    }

    public void ShowNewProject()
    {
        var model = ConfigStore.Load().ModelId;
        CurrentView = new NewProjectViewModel(_ai, model,
            onGenerated: p =>
            {
                Project = p;
                ProjectStore.SaveToLibrary(p);   // persist the generated project
                RevisionStore.Capture(p, "Generated from prompt");
                _tracked = true;
                ShowWorkspace();
            },
            onCancel: ShowProjects);
        Crumbs = new[] { "Foundry", "New project" };
    }

    public void OpenSample()
    {
        Project = DemoData.CreateSoilMoistureProject();
        _tracked = false;   // the sample is ephemeral, never written to the library
        AppLog.Info("project", "opened the sample project");
        ShowWorkspace();
    }

    /// <summary>Open a saved project from the library.</summary>
    public void OpenSaved(string id)
    {
        var p = ProjectStore.LoadById(id);
        if (p is null) { AppLog.Warn("project", $"open failed — {id} not found"); ShowProjects(); return; }
        Project = p;
        _tracked = true;
        AppLog.Info("project", $"opened “{p.Title}” ({id})");
        ShowWorkspace();
    }

    /// <summary>Open an already-generated project in the workspace (dev hook).</summary>
    public void OpenGenerated(Project p)
    {
        Project = p;
        ProjectStore.SaveToLibrary(p);
        _tracked = true;
        ShowWorkspace();
    }

    private void PersistTracked()
    {
        if (_tracked && Project is not null)
            try { ProjectStore.SaveToLibrary(Project); } catch { /* best effort */ }
    }

    public void ShowWorkspace()
    {
        if (Project is null) { ShowProjects(); return; }
        var cfg = ConfigStore.Load();
        var reviser = new ProjectGenerator(_ai, cfg.ChatModelId);   // chat/edits + fixes use the fast model
        var rebuilder = new ProjectGenerator(_ai, cfg.ModelId);     // full rebuild uses the main model
        var shell = new ShellViewModel(Project, _pipeline, reviser, rebuilder,
            onBack: ShowProjects, onTabChanged: UpdateWorkspaceCrumb, onSettings: ShowSettings, onLogs: ShowLogs,
            onProjectRevised: (p, label) =>
            {
                Project = p;
                _tracked = true;
                try { ProjectStore.SaveToLibrary(p); RevisionStore.Capture(p, label); } catch { }
            });
        CurrentView = shell;
        UpdateWorkspaceCrumb(shell.SelectedTab?.Label ?? "Workspace");
    }

    [RelayCommand]
    public void ShowSettings()
    {
        CurrentView = new SettingsViewModel(_credentials, _ai,
            onBack: () => { RefreshServices(); if (Project is null) ShowProjects(); else ShowWorkspace(); },
            onViewLogs: ShowLogs);
        Crumbs = new[] { "Foundry", Project?.Title ?? "Foundry", "SETTINGS" };
    }

    public void ShowLogs()
    {
        CurrentView = new LogsViewModel(onBack: ShowSettings);
        Crumbs = new[] { "Foundry", Project?.Title ?? "Foundry", "DIAGNOSTICS" };
    }

    private void UpdateWorkspaceCrumb(string tabLabel) =>
        Crumbs = new[] { "Foundry", Project?.Title ?? "Foundry", tabLabel.ToUpperInvariant() };
}
