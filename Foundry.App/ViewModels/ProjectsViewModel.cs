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

    /// <summary>Import a shared .foundryproj bundle into the library and open it (PRD v2 G14).</summary>
    [RelayCommand]
    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a Foundry project bundle",
            Filter = $"Foundry bundle (*{ProjectBundle.Extension})|*{ProjectBundle.Extension}|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var p = ProjectBundle.Import(dlg.FileName);
            p.Id = "p_" + Guid.NewGuid().ToString("N")[..8];   // fresh identity so it doesn't clash
            ProjectStore.SaveToLibrary(p);
            Foundry.Core.Diagnostics.AppLog.Info("project", $"imported bundle “{p.Title}” ({dlg.FileName})");
            _onOpen(p.Id);
        }
        catch (Exception ex)
        {
            Foundry.Core.Diagnostics.AppLog.Error("project", $"bundle import failed: {ex.Message}");
            System.Windows.MessageBox.Show($"Couldn't import that bundle: {ex.Message}", "Foundry — import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Asks the user to confirm a destructive action. Swappable so the delete path is unit-testable —
    /// a MessageBox in the view model is otherwise untestable by construction.
    /// </summary>
    public Func<string, string, bool> Confirm { get; set; } = DefaultConfirm;

    private static bool DefaultConfirm(string title, string message) =>
        System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    /// <summary>
    /// Delete a project and its revision history.
    ///
    /// <para>
    /// This ran on a single click of a small "×" with no confirmation at all, next to the row that OPENS
    /// the project — and it takes the .rev history with it, so there was nothing left to restore from.
    /// The revision cleanup landed; the dialog it needed never did.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Delete(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;

        var row = Recent.FirstOrDefault(x => x.Id == id);
        var name = string.IsNullOrWhiteSpace(row?.Title) ? id : row!.Title;
        if (!Confirm("Foundry — delete project",
                $"Delete “{name}” and its version history?\n\nThis cannot be undone."))
            return;

        ProjectStore.DeleteById(id);
        if (row is not null) Recent.Remove(row);
        OnPropertyChanged(nameof(HasRecent));
        Foundry.Core.Diagnostics.AppLog.Info("project", $"deleted “{name}” ({id})");
    }
}
