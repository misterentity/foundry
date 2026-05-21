using CommunityToolkit.Mvvm.ComponentModel;
using Foundry.Core.Ai;
using Foundry.Core.Config;
using Foundry.Core.Project;
using Foundry.Core.Security;

namespace Foundry.App.ViewModels;

/// <summary>
/// Root view model + screen router (PRD §12). Holds the active Project (created by generation or by
/// loading the sample — no canned project on startup) and swaps the active screen view model.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICredentialStore _credentials;
    private readonly IAnthropicClient _ai;
    private readonly IPipeline _pipeline;

    [ObservableProperty] private ObservableObject _currentView = null!;
    [ObservableProperty] private IReadOnlyList<string> _crumbs = new[] { "Foundry", "Setup" };

    public Project? Project { get; private set; }

    public MainViewModel(ICredentialStore credentials, IAnthropicClient ai, IPipeline pipeline)
    {
        _credentials = credentials;
        _ai = ai;
        _pipeline = pipeline;

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

    public void ShowWorkspace()
    {
        if (Project is null) { ShowProjects(); return; }
        var shell = new ShellViewModel(Project, _pipeline,
            onBack: ShowProjects, onTabChanged: UpdateWorkspaceCrumb, onSettings: ShowSettings);
        CurrentView = shell;
        UpdateWorkspaceCrumb(shell.SelectedTab?.Label ?? "Workspace");
    }

    public void ShowSettings()
    {
        CurrentView = new SettingsViewModel(_credentials, _ai, onBack: () => { if (Project is null) ShowProjects(); else ShowWorkspace(); });
        Crumbs = new[] { "Foundry", Project?.Title ?? "Foundry", "SETTINGS" };
    }

    private void UpdateWorkspaceCrumb(string tabLabel) =>
        Crumbs = new[] { "Foundry", Project?.Title ?? "Foundry", tabLabel.ToUpperInvariant() };
}
