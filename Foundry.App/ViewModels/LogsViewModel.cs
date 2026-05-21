using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Diagnostics;

namespace Foundry.App.ViewModels;

/// <summary>Diagnostics / audit-trail screen: the app log with live updates, level filter, and search.</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly Action _onBack;

    /// <summary>Filtered, newest-first view bound to the list.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _levelFilter = "ALL";   // ALL | INFO | WARN | ERROR
    [ObservableProperty] private bool _live = true;
    [ObservableProperty] private string _status = "";

    public LogsViewModel(Action onBack)
    {
        _onBack = onBack;
        Reload();
        AppLog.Logged += OnLogged;
    }

    partial void OnSearchChanged(string value) => Reload();
    partial void OnLevelFilterChanged(string value) => Reload();

    private bool Matches(LogEntry e)
    {
        if (LevelFilter != "ALL" && !string.Equals(e.Level, LevelFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            var hit = e.Message.Contains(s, StringComparison.OrdinalIgnoreCase)
                      || e.Category.Contains(s, StringComparison.OrdinalIgnoreCase)
                      || (e.Detail?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit) return false;
        }
        return true;
    }

    private void Reload()
    {
        var all = AppLog.Recent();
        Entries.Clear();
        foreach (var e in all.Where(Matches).Reverse()) Entries.Add(e);   // newest first
        int err = all.Count(e => e.Level == "ERROR"), warn = all.Count(e => e.Level == "WARN");
        Status = $"{Entries.Count} shown · {all.Count} total · {warn} warn · {err} error · {AppLog.LogDir}";
    }

    private void OnLogged(LogEntry e)
    {
        if (!Live) return;
        var disp = System.Windows.Application.Current?.Dispatcher;
        void Apply() { if (Matches(e)) Entries.Insert(0, e); }
        if (disp is null || disp.CheckAccess()) Apply();
        else disp.BeginInvoke(Apply);
    }

    [RelayCommand] private void Refresh() => Reload();
    [RelayCommand] private void SetLevel(string level) => LevelFilter = level;

    [RelayCommand]
    private void Clear()
    {
        AppLog.Clear();
        Reload();
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
                AppLog.Recent().Where(Matches).Select(e => e.Line + (e.HasDetail ? "  | " + e.Detail : "")));
            System.Windows.Clipboard.SetText(text);
            Status = "Copied the filtered log to the clipboard.";
        }
        catch { }
    }

    [RelayCommand]
    private void Back()
    {
        AppLog.Logged -= OnLogged;   // stop receiving live updates when we leave
        _onBack();
    }
}
