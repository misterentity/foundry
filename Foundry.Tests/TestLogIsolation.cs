using System.Runtime.CompilerServices;

namespace Foundry.Tests;

/// <summary>
/// Keeps test output out of the user's real diagnostics.
///
/// <para>
/// AppLog writes to %AppData%\Foundry\logs unconditionally, so every test run appended to the same file the
/// application uses. That is not just untidy — it defeats the log's purpose. Triaging the app log meant
/// separating real failures from test noise ("store: f.json is unusable — trying the backup" is
/// AtomicFileTests exercising backup recovery, not a user's project going bad).
/// </para>
///
/// <para>
/// A module initializer runs before the first test in the assembly, which is early enough to catch static
/// initialisation inside AppLog itself.
/// </para>
/// </summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "foundry-test-logs");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(Foundry.Core.Diagnostics.AppLog.LogDirVar, dir);
    }
}
