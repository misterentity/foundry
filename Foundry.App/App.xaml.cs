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

        // Composition root (lightweight DI). Phase 1 uses the offline stubs so the whole
        // app runs without an API key; the real Anthropic client/pipeline drop in here.
        var credentials = new CredentialStore();
        IAnthropicClient ai = new StubAnthropicClient();
        IPipeline pipeline = new StubPipeline();

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
}
