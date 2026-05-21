using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Project;

namespace Foundry.App.ViewModels;

/// <summary>
/// Project library (PRD §12). Lists saved projects (newest first) and starts a new one from a
/// prompt. Generated projects are persisted to the local library so they survive restarts.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly Action _onNew;
    private readonly Action<string> _onOpen;

    public ObservableCollection<ProjectSummary> Recent { get; } = new();
    public bool HasRecent => Recent.Count > 0;

    public ProjectsViewModel(Action onNew, Action<string> onOpen)
    {
        _onNew = onNew;
        _onOpen = onOpen;
        foreach (var s in ProjectStore.ListSummaries()) Recent.Add(s);
    }

    [RelayCommand] private void New() => _onNew();

    [RelayCommand] private void Open(string? id) { if (!string.IsNullOrEmpty(id)) _onOpen(id); }

    [RelayCommand]
    private void Delete(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        ProjectStore.DeleteById(id);
        var row = Recent.FirstOrDefault(x => x.Id == id);
        if (row is not null) Recent.Remove(row);
        OnPropertyChanged(nameof(HasRecent));
    }
}
