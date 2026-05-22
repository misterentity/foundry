using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Ai;
using Foundry.Core.Generation;

namespace Foundry.App.ViewModels;

/// <summary>New-project screen: a prompt → real generation (PRD §1, §7). Gated when no key.</summary>
public sealed partial class NewProjectViewModel : ObservableObject
{
    private readonly ProjectGenerator _generator;
    private readonly Action<Foundry.Core.Project.Project> _onGenerated;
    private readonly Action _onCancel;

    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasKey;

    public string[] Examples { get; } =
    {
        "A battery-powered soil-moisture sensor that texts me when my plants are dry.",
        "A Raspberry Pi Pico weather station with temp, humidity and pressure on an OLED.",
        "An ESP32 garage-door sensor that reports open/closed to Home Assistant.",
    };

    /// <summary>Saved full-project templates — start a new project from one instantly (no AI).</summary>
    public System.Collections.ObjectModel.ObservableCollection<Foundry.Core.Project.ProjectSummary> Templates { get; } = new();
    public bool HasTemplates => Templates.Count > 0;

    public NewProjectViewModel(IAnthropicClient ai, string model, Action<Foundry.Core.Project.Project> onGenerated, Action onCancel)
    {
        _generator = new ProjectGenerator(ai, model);
        _onGenerated = onGenerated;
        _onCancel = onCancel;
        _hasKey = ai.HasKey;
        if (!_hasKey) _status = "Add your Anthropic API key in Settings to generate. You can still open the sample project.";
        foreach (var t in Foundry.Core.Project.TemplateStore.List()) Templates.Add(t);
    }

    private System.Threading.CancellationTokenSource? _cts;

    [RelayCommand] private void UseExample(string example) => Prompt = example;

    /// <summary>Instantiate a saved template into a new project (no AI call).</summary>
    [RelayCommand]
    private void UseTemplate(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var p = Foundry.Core.Project.TemplateStore.Load(id);
        if (p is not null) _onGenerated(p);
    }
    [RelayCommand] private void Cancel() => _onCancel();
    [RelayCommand] private void CancelGenerate() => _cts?.Cancel();

    [RelayCommand]
    private async Task Generate()
    {
        if (IsGenerating) return;
        IsGenerating = true;
        _cts = new System.Threading.CancellationTokenSource();
        Status = "Designing your project, then writing full firmware — this can take a minute…";
        try
        {
            var result = await _generator.GenerateAsync(Prompt, _cts.Token);
            if (result.Ok && result.Project is not null)
                _onGenerated(result.Project);
            else
                Status = result.Message;
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception ex) { Status = $"Generation failed: {ex.Message}"; }
        finally { IsGenerating = false; }
    }
}
