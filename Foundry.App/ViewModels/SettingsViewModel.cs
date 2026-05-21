using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core;
using Foundry.Core.Ai;
using Foundry.Core.Config;
using Foundry.Core.Security;
using Microsoft.Win32;

namespace Foundry.App.ViewModels;

/// <summary>
/// Settings (PRD §8.9, F11/F12): Claude key + test + model dropdown (live /v1/models with curated
/// fallback), and generation/export/sourcing options. Secrets go to Credential Manager; non-secret
/// settings persist to disk via ConfigStore.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ICredentialStore _credentials;
    private readonly IAnthropicClient _ai;
    private readonly Action _onBack;
    private readonly Action _onViewLogs;
    private readonly AppConfig _config;

    public SettingsViewModel(ICredentialStore credentials, IAnthropicClient ai, Action onBack, Action onViewLogs)
    {
        _credentials = credentials;
        _ai = ai;
        _onBack = onBack;
        _onViewLogs = onViewLogs;
        _config = ConfigStore.Load();

        _selectedModelId = _config.ModelId;
        _chatModelId = _config.ChatModelId;
        _maxOutputTokens = _config.MaxOutputTokens;
        _temperature = _config.Temperature;
        _firmwarePlatform = _config.FirmwarePlatform;
        _outputFolder = _config.OutputFolder;
        _enclosureFormat = _config.EnclosureFormat;

        foreach (var m in ModelCatalog.Fallback) Models.Add(m);
        RefreshSummaries();
        _ = LoadModelsAsync();
    }

    // ---- model dropdown ----
    public ObservableCollection<ClaudeModel> Models { get; } = new();
    [ObservableProperty] private string _selectedModelId;
    [ObservableProperty] private string _chatModelId;
    [ObservableProperty] private string _modelSource = "curated fallback";

    // ---- generation / export ----
    [ObservableProperty] private int _maxOutputTokens;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private string _firmwarePlatform;
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _enclosureFormat;
    public string[] FirmwarePlatforms { get; } = { "Arduino C++", "MicroPython" };
    public string[] EnclosureFormats { get; } = { "STL", "3MF", "STEP" };

    // ---- keys ----
    [ObservableProperty] private string _anthropicKeyInput = "";
    [ObservableProperty] private string _anthropicSummary = "—";
    [ObservableProperty] private string _nexarKeyInput = "";
    [ObservableProperty] private string _nexarSummary = "—";
    [ObservableProperty] private string _digiKeyInput = "";
    [ObservableProperty] private string _digiKeySummary = "—";
    [ObservableProperty] private string _mouserKeyInput = "";
    [ObservableProperty] private string _mouserSummary = "—";

    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _saveResult = "";

    // ---- updates ----
    public string CurrentVersion => $"v{AppInfo.Version}";

    private void RefreshSummaries()
    {
        AnthropicSummary = CredentialStore.Mask(_credentials.Read(CredentialStore.AnthropicTarget));
        NexarSummary = CredentialStore.Mask(_credentials.Read(CredentialStore.NexarTarget));
        DigiKeySummary = CredentialStore.Mask(_credentials.Read(CredentialStore.DigiKeyTarget));
        MouserSummary = CredentialStore.Mask(_credentials.Read(CredentialStore.MouserTarget));
    }

    private async Task LoadModelsAsync()
    {
        var result = await _ai.ListModelsAsync();
        if (result.Ok && result.Models.Count > 0)
        {
            Models.Clear();
            foreach (var m in result.Models) Models.Add(m);
            ModelSource = _ai.HasKey ? "live · /v1/models" : "curated fallback";
            if (Models.All(m => m.Id != SelectedModelId) && Models.Count > 0)
                SelectedModelId = Models[0].Id;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true; TestResult = "Checking…";
        // Only dispose a throwaway client built from the typed key — never the shared _ai
        // (disposing _ai kills its HttpClient and breaks every later call).
        var typed = !string.IsNullOrWhiteSpace(AnthropicKeyInput);
        var client = typed ? new AnthropicClient(AnthropicKeyInput.Trim()) : _ai;
        try
        {
            var result = await client.ListModelsAsync();
            TestResult = result.Ok ? $"✓ valid · {result.Models.Count} models" : $"✗ {result.Error}";
        }
        catch (Exception ex) { TestResult = $"✗ {ex.Message}"; }
        finally
        {
            if (typed) (client as IDisposable)?.Dispose();
            IsTesting = false;
        }
    }

    [RelayCommand] private void SaveAnthropicKey() { Persist(CredentialStore.AnthropicTarget, AnthropicKeyInput); AnthropicKeyInput = ""; }
    [RelayCommand] private void RemoveAnthropicKey() { _credentials.Delete(CredentialStore.AnthropicTarget); RefreshSummaries(); }
    [RelayCommand] private void SaveNexarKey() { Persist(CredentialStore.NexarTarget, NexarKeyInput); NexarKeyInput = ""; }
    [RelayCommand] private void RemoveNexarKey() { _credentials.Delete(CredentialStore.NexarTarget); RefreshSummaries(); }
    [RelayCommand] private void SaveDigiKey() { Persist(CredentialStore.DigiKeyTarget, DigiKeyInput); DigiKeyInput = ""; }
    [RelayCommand] private void RemoveDigiKey() { _credentials.Delete(CredentialStore.DigiKeyTarget); RefreshSummaries(); }
    [RelayCommand] private void SaveMouserKey() { Persist(CredentialStore.MouserTarget, MouserKeyInput); MouserKeyInput = ""; }
    [RelayCommand] private void RemoveMouserKey() { _credentials.Delete(CredentialStore.MouserTarget); RefreshSummaries(); }

    private void Persist(string target, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _credentials.Save(target, value.Trim());
            Foundry.Core.Diagnostics.AppLog.Info("settings", $"saved credential: {target}");   // never logs the value
        }
        RefreshSummaries();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Default export folder" };
        if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void Save()
    {
        _config.ModelId = SelectedModelId;
        _config.ChatModelId = ChatModelId;
        _config.MaxOutputTokens = MaxOutputTokens;
        _config.Temperature = Temperature;
        _config.FirmwarePlatform = FirmwarePlatform;
        _config.OutputFolder = OutputFolder;
        _config.EnclosureFormat = EnclosureFormat;
        ConfigStore.Save(_config);
        Foundry.Core.Diagnostics.AppLog.Info("settings", $"saved · model {SelectedModelId} · chat {ChatModelId} · platform {FirmwarePlatform}");
        SaveResult = $"Saved · {DateTime.Now:HH:mm}";
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        Save(); // persist owner/repo so the check uses the current values
        if (Application.Current is App app) await app.CheckForUpdatesAsync(interactive: true);
    }

    [RelayCommand] private void ViewLogs() => _onViewLogs();
    public string LogFolder => Foundry.Core.Diagnostics.AppLog.LogDir;

    [RelayCommand] private void Back() => _onBack();
}
