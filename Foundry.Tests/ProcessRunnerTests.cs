using System.Diagnostics;
using Foundry.Core.Diagnostics;

namespace Foundry.Tests;

// Windows-guarded tests for the shared subprocess runner: timeout + process-tree kill, concurrent stream
// drain (no pipe-buffer deadlock), and caller-cancel propagation. They spawn real cmd.exe/ping processes,
// so they are skipped off-Windows to keep the cross-platform suite green.
public class ProcessRunnerTests
{
    private static bool Win => OperatingSystem.IsWindows();
    private const string Cmd = "cmd.exe";

    [Fact]
    public async Task RunAsync_KillsAndReportsTimeout_OnSlowChild()
    {
        if (!Win) return;

        var sw = Stopwatch.StartNew();
        // ping -n 30 ⇒ ~29s; a 500ms timeout must kill it and return promptly.
        var r = await ProcessRunner.RunAsync(Cmd, "/c ping -n 30 127.0.0.1", TimeSpan.FromMilliseconds(500));
        sw.Stop();

        Assert.True(r.TimedOut);
        Assert.NotEqual(0, r.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"timeout should kill fast; took {sw.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task RunAsync_FastChild_ReturnsStdoutAndZeroExit()
    {
        if (!Win) return;

        var r = await ProcessRunner.RunAsync(Cmd, "/c echo hello-foundry", TimeSpan.FromSeconds(30));
        Assert.False(r.TimedOut);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hello-foundry", r.Stdout);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_IsReported_NotTimedOut()
    {
        if (!Win) return;

        var r = await ProcessRunner.RunAsync(Cmd, "/c exit 3", TimeSpan.FromSeconds(30));
        Assert.False(r.TimedOut);
        Assert.Equal(3, r.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CallerCancel_ThrowsOperationCanceled()
    {
        if (!Win) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync(Cmd, "/c ping -n 30 127.0.0.1", TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact]
    public async Task RunAsync_LargeStderr_DoesNotDeadlock()
    {
        if (!Win) return;

        // Emit a lot of output: if stdout/stderr were drained sequentially after WaitForExit, a full pipe
        // buffer would deadlock. Concurrent reads must complete it well under the timeout.
        var r = await ProcessRunner.RunAsync(Cmd, "/c for /L %i in (1,1,5000) do @echo line-%i 1>&2", TimeSpan.FromSeconds(60));
        Assert.False(r.TimedOut);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("line-5000", r.Stderr);
    }
}
