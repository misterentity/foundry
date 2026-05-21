using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Project;

namespace Foundry.App.ViewModels;

/// <summary>Project library screen (PRD §12). Featured "continue" card + grid.</summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly Action<ProjectSummary> _onOpen;
    private readonly Action _onNew;

    [ObservableProperty] private string _query = "";

    public ObservableCollection<ProjectSummary> All { get; }
    public ProjectSummary Featured { get; }
    public ObservableCollection<ProjectSummary> Rest { get; }

    public ProjectsViewModel(Action<ProjectSummary> onOpen, Action onNew)
    {
        _onOpen = onOpen;
        _onNew = onNew;
        var projects = DemoData.RecentProjects();
        All = new ObservableCollection<ProjectSummary>(projects);
        Featured = projects[0];
        Rest = new ObservableCollection<ProjectSummary>(projects.Skip(1));
    }

    [RelayCommand] private void Open(ProjectSummary? p) => _onOpen(p ?? Featured);
    [RelayCommand] private void New() => _onNew();
}
