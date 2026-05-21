using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Ai;
using Foundry.Core.Generation;
using Foundry.Core.Project;

namespace Foundry.App.ViewModels;

/// <summary>A left-rail / tabbar entry. Badge is observable so it can update live (e.g. validation).</summary>
public sealed partial class TabDescriptor : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Icon { get; init; }
    public required int Index { get; init; }
    public required Func<Project, ObservableObject> Factory { get; init; }
    public string Number => (Index + 1).ToString("00");

    [ObservableProperty] private string? _badgeText;
    [ObservableProperty] private string? _badgeKind = "warn";
    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);
    partial void OnBadgeTextChanged(string? value) => OnPropertyChanged(nameof(HasBadge));
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
    private readonly ProjectGenerator _reviser;
    private readonly Action _onBack;
    private readonly Action<string> _onTabChanged;
    private readonly Action _onSettings;
    private readonly Action<Project> _onProjectRevised;
    private TabDescriptor? _validationTab;

    public Project Project { get; private set; }
    public IReadOnlyList<TabDescriptor> Tabs { get; }
    public IReadOnlyList<StageRow> Stages { get; }

    public ObservableCollection<ChatMessage> Chat { get; }
    public ObservableCollection<PipelineStage> LivePipeline { get; } = new();

    [ObservableProperty] private TabDescriptor _selectedTab = null!;
    [ObservableProperty] private ObservableObject _currentTabView = null!;
    [ObservableProperty] private string _chatInput = "";
    [ObservableProperty] private bool _isGenerating;

    public ShellViewModel(Project project, IPipeline pipeline, ProjectGenerator reviser,
        Action onBack, Action<string> onTabChanged, Action onSettings, Action<Project> onProjectRevised)
    {
        Project = project;
        _pipeline = pipeline;
        _reviser = reviser;
        _onBack = onBack;
        _onTabChanged = onTabChanged;
        _onSettings = onSettings;
        _onProjectRevised = onProjectRevised;

        Tabs = new List<TabDescriptor>
        {
            new() { Id="overview",  Label="Overview",       Icon="spark",  Index=0, Factory=p => new OverviewViewModel(p) },
            new() { Id="bom",       Label="BOM",            Icon="cart",   Index=1, Factory=p => new BomViewModel(p) },
            new() { Id="wiring",    Label="Wiring",         Icon="wire",   Index=2, Factory=p => new WiringViewModel(p) },
            new() { Id="enclosure", Label="Enclosure",      Icon="cube",   Index=3, Factory=p => new EnclosureViewModel(p) },
            new() { Id="firmware",  Label="Firmware",       Icon="code",   Index=4, Factory=p => new FirmwareViewModel(p) },
            new() { Id="validation",Label="Validation",     Icon="shield", Index=5, Factory=p => new ValidationViewModel(p) },
            new() { Id="guide",     Label="Assembly guide", Icon="book",   Index=6, Factory=p => new GuideViewModel(p) },
        };
        _validationTab = Tabs.First(t => t.Id == "validation");
        UpdateValidationBadge();

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
        var view = value.Factory(Project);
        WireTab(view);
        CurrentTabView = view;
        _onTabChanged(value.Label);
    }

    private void WireTab(ObservableObject view)
    {
        if (view is ValidationViewModel vvm)
        {
            vvm.FindingsChanged += UpdateValidationBadge;
            vvm.FixRequested += f => _ = ApplyAiFixAsync(f);
        }
    }

    /// <summary>Have the AI generate a fix for a validation finding, apply it, and re-validate.</summary>
    private async Task ApplyAiFixAsync(Finding finding)
    {
        if (IsGenerating) return;
        IsGenerating = true;
        _cts = new CancellationTokenSource();
        Chat.Add(new ChatMessage { Role = "user", Time = DateTime.Now.ToString("HH:mm"), Text = $"Fix: {finding.Title}" });
        try
        {
            var req = $"Resolve this electrical validation finding by editing the design, then return the full " +
                      $"updated project: [{finding.Code}] {finding.Title}. {finding.Description} " +
                      $"Suggested fix: {finding.Fix}. Make the minimal change that resolves it.";
            var result = await _reviser.ReviseAsync(Project, req, _cts.Token, forceEdit: true);
            if (result.Ok && result.Project is not null)
            {
                bool stillThere = result.Project.Findings.Any(f => f.Code == finding.Code && f.Title == finding.Title);
                ApplyRevision(result.Project, stillThere
                    ? $"Reworked the design for {finding.Code}, but the check still flags it — may need a manual change."
                    : $"Fixed {finding.Code}: {finding.Title}. Re-validated.");
            }
            else
                Chat.Add(new ChatMessage { Role = "assistant", Time = DateTime.Now.ToString("HH:mm"), Text = result.Message });
        }
        catch (OperationCanceledException)
        {
            Chat.Add(new ChatMessage { Role = "assistant", Time = DateTime.Now.ToString("HH:mm"), Text = "Cancelled." });
        }
        catch (Exception ex)
        {
            Chat.Add(new ChatMessage { Role = "assistant", Time = DateTime.Now.ToString("HH:mm"), Text = $"Couldn't generate a fix: {ex.Message}" });
        }
        finally { IsGenerating = false; }
    }

    /// <summary>Recompute the validation rail badge from the current findings (fails over warns; hidden when clean).</summary>
    private void UpdateValidationBadge()
    {
        if (_validationTab is null) return;
        var fails = Project.Findings.Count(f => f.Severity == "fail");
        var warns = Project.Findings.Count(f => f.Severity == "warn");
        _validationTab.BadgeKind = fails > 0 ? "fail" : "warn";
        _validationTab.BadgeText = fails > 0 ? $"{fails}F" : warns > 0 ? $"{warns}W" : null;
    }

    [RelayCommand] private void Back() => _onBack();
    [RelayCommand] private void Settings() => _onSettings();

    /// <summary>Export the branded project-spec PDF from any tab.</summary>
    [RelayCommand]
    private void Export()
    {
        try
        {
            var dir = Foundry.Core.Config.ConfigStore.Load().OutputFolder;
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "project-spec.pdf");
            System.IO.File.WriteAllBytes(path, Foundry.Core.Export.PdfExporter.ProjectPdf(Project));
            Foundry.Core.Diagnostics.AppLog.Info("export", $"project PDF → {path}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { Foundry.Core.Diagnostics.AppLog.Error("export", $"PDF export failed: {ex.Message}"); }
    }

    private CancellationTokenSource? _cts;

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = ChatInput.Trim();
        if (string.IsNullOrEmpty(text) || IsGenerating) return;
        ChatInput = "";
        IsGenerating = true;
        _cts = new CancellationTokenSource();

        var userMsg = new ChatMessage { Role = "user", Text = text, Time = DateTime.Now.ToString("HH:mm") };
        Chat.Add(userMsg);

        try
        {
            // Chat edits the design: revise the project, then swap it in and re-run downstream stages.
            var result = await _reviser.ReviseAsync(Project, text, _cts.Token);
            if (result.Ok && result.Project is not null)
                ApplyRevision(result.Project, $"Done — applied “{text}”. BOM, wiring, firmware, enclosure and validation updated.");
            else
                Chat.Add(new ChatMessage { Role = "assistant", Time = DateTime.Now.ToString("HH:mm"), Text = result.Message });
        }
        catch (OperationCanceledException)
        {
            Chat.Add(new ChatMessage { Role = "assistant", Time = DateTime.Now.ToString("HH:mm"), Text = "Cancelled." });
        }
        catch (Exception ex)
        {
            Chat.Add(new ChatMessage { Role = "assistant", Time = DateTime.Now.ToString("HH:mm"), Text = $"Couldn't apply that: {ex.Message}" });
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private void ApplyRevision(Project revised, string summary)
    {
        var assistant = new ChatMessage { Role = "assistant", Text = summary, Time = DateTime.Now.ToString("HH:mm") };
        Chat.Add(assistant);
        revised.Chat = Chat.ToList();           // persist the running conversation

        Project = revised;
        OnPropertyChanged(nameof(Project));
        _onProjectRevised(revised);             // let the root update its ref + save to the library

        UpdateValidationBadge();
        var view = SelectedTab.Factory(Project);         // rebuild the active tab against the new project
        WireTab(view);
        CurrentTabView = view;
    }
}
