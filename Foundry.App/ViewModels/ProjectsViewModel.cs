using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Foundry.App.ViewModels;

/// <summary>
/// Project library launcher (PRD §12). Starts empty — no canned data; a tester creates a real
/// project from a prompt or opens the labelled sample to explore the UI without a key.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly Action _onNew;
    private readonly Action _onOpenSample;

    public ProjectsViewModel(Action onNew, Action onOpenSample)
    {
        _onNew = onNew;
        _onOpenSample = onOpenSample;
    }

    [RelayCommand] private void New() => _onNew();
    [RelayCommand] private void OpenSample() => _onOpenSample();
}
