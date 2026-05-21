using CommunityToolkit.Mvvm.ComponentModel;
using Foundry.Core.Ai;
using Foundry.Core.Project;
using Foundry.Core.Security;

namespace Foundry.App.ViewModels;

/// <summary>
/// Root view model + screen router (PRD §12). Owns the canonical Project and the shared
/// services, and swaps the active screen view model (onboarding ↔ projects ↔ workspace).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICredentialStore _credentials;
    private readonly IAnthropicClient _ai;
    private readonly IPipeline _pipeline;

    [ObservableProperty] private ObservableObject _currentView = null!;
    [ObservableProperty] private IReadOnlyList<string> _crumbs = new[] { "Foundry", "Setup" };

    public Project Project { get; }

    public MainViewModel(ICredentialStore credentials, IAnthropicClient ai, IPipeline pipeline)
    {
        _credentials = credentials;
        _ai = ai;
        _pipeline = pipeline;
        Project = DemoData.CreateSoilMoistureProject();

        // First-run goes to onboarding unless a key already exists.
        if (_credentials.Exists(CredentialStore.AnthropicTarget))
            ShowProjects();
        else
            ShowOnboarding();
    }

    public void ShowOnboarding()
    {
        CurrentView = new OnboardingViewModel(_credentials, _ai, onDone: ShowProjects);
        Crumbs = new[] { "Foundry", "Setup" };
    }

    public void ShowProjects()
    {
        CurrentView = new ProjectsViewModel(onOpen: _ => ShowWorkspace(), onNew: ShowWorkspace);
        Crumbs = new[] { "Foundry", "Library" };
    }

    public void ShowWorkspace()
    {
        var shell = new ShellViewModel(Project, _pipeline,
            onBack: ShowProjects, onTabChanged: UpdateWorkspaceCrumb, onSettings: ShowSettings);
        CurrentView = shell;
        UpdateWorkspaceCrumb(shell.SelectedTab?.Label ?? "Workspace");
    }

    public void ShowSettings()
    {
        CurrentView = new SettingsViewModel(_credentials, _ai, onBack: ShowWorkspace);
        Crumbs = new[] { "Foundry", Project.Title, "SETTINGS" };
    }

    private void UpdateWorkspaceCrumb(string tabLabel) =>
        Crumbs = new[] { "Foundry", Project.Title, tabLabel.ToUpperInvariant() };
}
