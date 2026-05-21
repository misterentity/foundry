using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Ai;
using Foundry.Core.Security;

namespace Foundry.App.ViewModels;

/// <summary>First-run API-key setup (PRD §8.9 / §12). Keys go to Credential Manager.</summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly ICredentialStore _credentials;
    private readonly IAnthropicClient _ai;
    private readonly Action _onDone;

    [ObservableProperty] private string _activeTab = "anthropic";
    [ObservableProperty] private string _anthropicKey = "";
    [ObservableProperty] private string _nexarKey = "";
    [ObservableProperty] private string _digiKeyKey = "";
    [ObservableProperty] private string _mouserKey = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isTesting;

    public bool IsAnthropicTab => ActiveTab == "anthropic";
    public bool IsSourcingTab => ActiveTab == "sourcing";

    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsAnthropicTab));
        OnPropertyChanged(nameof(IsSourcingTab));
    }

    public OnboardingViewModel(ICredentialStore credentials, IAnthropicClient ai, Action onDone)
    {
        _credentials = credentials;
        _ai = ai;
        _onDone = onDone;
    }

    [RelayCommand] private void SelectTab(string tab) => ActiveTab = tab;

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResult = "Checking…";
        try
        {
            // Validate the key the user just typed (not whatever is stored).
            IAnthropicClient client = string.IsNullOrWhiteSpace(AnthropicKey)
                ? _ai
                : new AnthropicClient(AnthropicKey.Trim());
            var result = await client.ListModelsAsync();
            TestResult = result.Ok
                ? $"✓ valid · {result.Models.Count} models"
                : $"✗ {result.Error}";
            (client as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            TestResult = $"✗ {ex.Message}";
        }
        finally { IsTesting = false; }
    }

    [RelayCommand]
    private void Continue()
    {
        SaveKeys();
        _onDone();
    }

    [RelayCommand] private void Skip() => _onDone();

    private void SaveKeys()
    {
        Persist(CredentialStore.AnthropicTarget, AnthropicKey);
        Persist(CredentialStore.NexarTarget, NexarKey);
        Persist(CredentialStore.DigiKeyTarget, DigiKeyKey);
        Persist(CredentialStore.MouserTarget, MouserKey);
    }

    private void Persist(string target, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _credentials.Save(target, value.Trim());
    }
}
