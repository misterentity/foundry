using System.Windows;
using Foundry.App.ViewModels;
using Foundry.Core.Ai;
using Foundry.Core.Security;

namespace Foundry.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Composition root (lightweight DI). The real Anthropic client is used when a key is
        // stored in Credential Manager; otherwise the app runs fully offline (PRD F9).
        var credentials = new CredentialStore();
        var anthropicKey = credentials.Read(CredentialStore.AnthropicTarget);
        IAnthropicClient ai = string.IsNullOrWhiteSpace(anthropicKey)
            ? new StubAnthropicClient()
            : new AnthropicClient(anthropicKey);
        IPipeline pipeline = new ChatPipeline(ai);

        var main = new MainViewModel(credentials, ai, pipeline);

        // Dev affordance: FOUNDRY_START=projects|workspace jumps straight to a screen,
        // FOUNDRY_TAB=<tabId> preselects a workspace tab (used for screenshots/verification).
        switch (Environment.GetEnvironmentVariable("FOUNDRY_START"))
        {
            case "projects": main.ShowProjects(); break;
            case "workspace": main.ShowWorkspace(); break;
        }

        var window = new MainWindow { DataContext = main };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Foundry.Core.Sidecar.SidecarHost.Shared.Dispose(); // kill the spawned CAD sidecar
        base.OnExit(e);
    }
}
