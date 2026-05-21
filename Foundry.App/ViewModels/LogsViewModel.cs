using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Diagnostics;

namespace Foundry.App.ViewModels;

/// <summary>Diagnostics / audit-trail screen: the app log (AI calls, generation phases, errors).</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly Action _onBack;

    public ObservableCollection<LogEntry> Entries { get; } = new();
    [ObservableProperty] private string _status = "";

    public LogsViewModel(Action onBack)
    {
        _onBack = onBack;
        Load();
    }

    private void Load()
    {
        Entries.Clear();
        foreach (var e in AppLog.Recent().Reverse()) Entries.Add(e);   // newest first
        Status = $"{Entries.Count} entries · {AppLog.LogDir}";
    }

    [RelayCommand] private void Refresh() => Load();

    [RelayCommand]
    private void Clear()
    {
        AppLog.Clear();
        Load();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.LogDir);
            Process.Start(new ProcessStartInfo { FileName = AppLog.LogDir, UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            var text = string.Join(Environment.NewLine,
                AppLog.Recent().Select(e => e.Line + (e.HasDetail ? "  | " + e.Detail : "")));
            System.Windows.Clipboard.SetText(text);
            Status = "Copied the full log to the clipboard.";
        }
        catch { }
    }

    [RelayCommand] private void Back() => _onBack();
}
