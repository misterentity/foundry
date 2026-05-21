namespace Foundry.Core.Diagnostics;

/// <summary>
/// Tracks in-flight AI calls so the UI can show a global progress indicator. Every AnthropicClient
/// call wraps its work in <see cref="Begin"/>; the status bar reflects <see cref="Busy"/> + <see cref="Label"/>.
/// </summary>
public static class AiActivity
{
    private static readonly object Gate = new();
    private static int _count;
    private static string? _label;

    /// <summary>Raised whenever activity starts or stops (may fire on a background thread).</summary>
    public static event Action? Changed;

    public static bool Busy { get { lock (Gate) return _count > 0; } }
    public static string? Label { get { lock (Gate) return _label; } }

    /// <summary>Mark an AI call in-flight; dispose when it completes.</summary>
    public static IDisposable Begin(string label)
    {
        lock (Gate) { _count++; _label = label; }
        Raise();
        return new Scope();
    }

    private static void End()
    {
        lock (Gate) { _count = Math.Max(0, _count - 1); if (_count == 0) _label = null; }
        Raise();
    }

    private static void Raise() { try { Changed?.Invoke(); } catch { } }

    private sealed class Scope : IDisposable
    {
        private bool _done;
        public void Dispose() { if (_done) return; _done = true; End(); }
    }
}
