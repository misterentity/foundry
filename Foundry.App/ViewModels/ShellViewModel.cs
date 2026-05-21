using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Ai;
using Foundry.Core.Project;

namespace Foundry.App.ViewModels;

/// <summary>A left-rail / tabbar entry.</summary>
public sealed class TabDescriptor
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Icon { get; init; }
    public required int Index { get; init; }
    public string? BadgeText { get; init; }
    public string? BadgeKind { get; init; }
    public required Func<Project, ObservableObject> Factory { get; init; }
    public string Number => (Index + 1).ToString("00");
    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);
}

/// <summary>A read-only rail "stage" row (Spec/Architecture/…).</summary>
public sealed record StageRow(int N, string Name, string State)
{
    public bool IsLive => State == "live";
}

/// <summary>Workspace shell: rail + main(tabbar + body) + chat (PRD §12, §8.1).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IPipeline _pipeline;
    private readonly Action _onBack;
    private readonly Action<string> _onTabChanged;
    private readonly Action _onSettings;

    public Project Project { get; }
    public IReadOnlyList<TabDescriptor> Tabs { get; }
    public IReadOnlyList<StageRow> Stages { get; }

    public ObservableCollection<ChatMessage> Chat { get; }
    public ObservableCollection<PipelineStage> LivePipeline { get; } = new();

    [ObservableProperty] private TabDescriptor _selectedTab = null!;
    [ObservableProperty] private ObservableObject _currentTabView = null!;
    [ObservableProperty] private string _chatInput = "";
    [ObservableProperty] private bool _isGenerating;

    public ShellViewModel(Project project, IPipeline pipeline, Action onBack, Action<string> onTabChanged, Action onSettings)
    {
        Project = project;
        _pipeline = pipeline;
        _onBack = onBack;
        _onTabChanged = onTabChanged;
        _onSettings = onSettings;

        Tabs = new List<TabDescriptor>
        {
            new() { Id="overview",  Label="Overview",       Icon="spark",  Index=0, Factory=p => new OverviewViewModel(p) },
            new() { Id="bom",       Label="BOM",            Icon="cart",   Index=1, Factory=p => new BomViewModel(p) },
            new() { Id="wiring",    Label="Wiring",         Icon="wire",   Index=2, Factory=p => new WiringViewModel(p) },
            new() { Id="enclosure", Label="Enclosure",      Icon="cube",   Index=3, Factory=p => new EnclosureViewModel(p) },
            new() { Id="firmware",  Label="Firmware",       Icon="code",   Index=4, Factory=p => new FirmwareViewModel(p) },
            new() { Id="validation",Label="Validation",     Icon="shield", Index=5, BadgeText="2W", BadgeKind="warn", Factory=p => new ValidationViewModel(p) },
            new() { Id="guide",     Label="Assembly guide", Icon="book",   Index=6, Factory=p => new GuideViewModel(p) },
        };

        Stages = new List<StageRow>
        {
            new(1, "Spec", "done"), new(2, "Architecture", "done"), new(3, "Wiring", "done"),
            new(4, "Firmware", "done"), new(5, "Enclosure", "live"), new(6, "Validation", "live"),
        };

        Chat = new ObservableCollection<ChatMessage>(project.Chat);
        var startTab = Environment.GetEnvironmentVariable("FOUNDRY_TAB");
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == startTab) ?? Tabs[0];
    }

    partial void OnSelectedTabChanged(TabDescriptor value)
    {
        if (value is null) return;
        CurrentTabView = value.Factory(Project);
        _onTabChanged(value.Label);
    }

    [RelayCommand] private void Back() => _onBack();
    [RelayCommand] private void Settings() => _onSettings();

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = ChatInput.Trim();
        if (string.IsNullOrEmpty(text) || IsGenerating) return;
        ChatInput = "";
        IsGenerating = true;

        var userMsg = new ChatMessage { Role = "user", Text = text, Time = DateTime.Now.ToString("HH:mm") };
        Chat.Add(userMsg);

        LivePipeline.Clear();
        var progress = new Progress<IReadOnlyList<PipelineStage>>(stages =>
        {
            LivePipeline.Clear();
            foreach (var s in stages) LivePipeline.Add(s);
        });

        try
        {
            var reply = await _pipeline.RunTurnAsync(Project, text, progress);
            // The stub already appended user+assistant to Project.Chat; mirror only the reply.
            Chat.Add(reply);
        }
        finally
        {
            IsGenerating = false;
            LivePipeline.Clear();
        }
    }
}
