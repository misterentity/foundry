using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Ai;
using Foundry.Core.Config;
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
    [ObservableProperty] private IReadOnlyList<string> _crumbs = new[] { "Foundry", "Setup" };

    public Project? Project { get; private set; }
    public string AppVersionLabel => $"v{Foundry.Core.AppInfo.Version}";

    public MainViewModel(ICredentialStore credentials)
    {
        _credentials = credentials;
        RefreshServices();

        if (_credentials.Exists(CredentialStore.AnthropicTarget))
            ShowProjects();
        else
            ShowOnboarding();
    }

    /// <summary>Rebuild AI/pipeline/sourcing from the currently stored keys.</summary>
    private void RefreshServices()
    {
        var anthropicKey = _credentials.Read(CredentialStore.AnthropicTarget);
        _ai = string.IsNullOrWhiteSpace(anthropicKey) ? new StubAnthropicClient() : new AnthropicClient(anthropicKey);
        _pipeline = new ChatPipeline(_ai, ConfigStore.Load().ModelId);

        var nexarKey = _credentials.Read(CredentialStore.NexarTarget);
        SourcingService.Shared = new SourcingService(
            string.IsNullOrWhiteSpace(nexarKey) ? new NullSourcingProvider() : new NexarSourcingProvider(nexarKey));
    }

    public void ShowOnboarding()
    {
        CurrentView = new OnboardingViewModel(_credentials, _ai, onDone: () => { RefreshServices(); ShowProjects(); });
        Crumbs = new[] { "Foundry", "Setup" };
    }

    public void ShowProjects()
    {
        CurrentView = new ProjectsViewModel(onNew: ShowNewProject, onOpenSample: OpenSample);
        Crumbs = new[] { "Foundry", "Library" };
    }

    public void ShowNewProject()
    {
        var model = ConfigStore.Load().ModelId;
        CurrentView = new NewProjectViewModel(_ai, model,
            onGenerated: p => { Project = p; ShowWorkspace(); },
            onCancel: ShowProjects);
        Crumbs = new[] { "Foundry", "New project" };
    }

    public void OpenSample()
    {
        Project = DemoData.CreateSoilMoistureProject();
        ShowWorkspace();
    }

    /// <summary>Open an already-generated project in the workspace.</summary>
    public void OpenGenerated(Project p)
    {
        Project = p;
        ShowWorkspace();
    }

    public void ShowWorkspace()
    {
        if (Project is null) { ShowProjects(); return; }
        var shell = new ShellViewModel(Project, _pipeline,
            onBack: ShowProjects, onTabChanged: UpdateWorkspaceCrumb, onSettings: ShowSettings);
        CurrentView = shell;
        UpdateWorkspaceCrumb(shell.SelectedTab?.Label ?? "Workspace");
    }

    [RelayCommand]
    public void ShowSettings()
    {
        CurrentView = new SettingsViewModel(_credentials, _ai,
            onBack: () => { RefreshServices(); if (Project is null) ShowProjects(); else ShowWorkspace(); });
        Crumbs = new[] { "Foundry", Project?.Title ?? "Foundry", "SETTINGS" };
    }

    private void UpdateWorkspaceCrumb(string tabLabel) =>
        Crumbs = new[] { "Foundry", Project?.Title ?? "Foundry", tabLabel.ToUpperInvariant() };
}
