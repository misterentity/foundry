using System.Text.Json.Serialization;

namespace Foundry.Core.Diagnostics;

/// <summary>One audit/log record. Never contains secrets — API keys live in Credential Manager only.</summary>
public sealed record LogEntry(DateTime Time, string Level, string Category, string Message, string? Detail = null)
{
    [JsonIgnore] public string TimeText => Time.ToString("HH:mm:ss");
    [JsonIgnore] public string Line => $"[{Time:yyyy-MM-dd HH:mm:ss}] {Level,-5} {Category}: {Message}";
    /// <summary>Maps the level to a severity token the UI's SevBrush converter understands.</summary>
    [JsonIgnore] public string Sev => Level switch { "ERROR" => "fail", "WARN" => "warn", _ => "info" };
    [JsonIgnore] public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>
/// Lightweight app-wide logger + audit trail (PRD §14). Keeps a rolling in-memory buffer for the
/// Diagnostics screen and appends every entry to a daily file under %AppData%/Foundry/logs. AI calls
/// are logged with metadata (model, sizes, duration, status) — never prompts/keys-as-secrets.
/// </summary>
public static class AppLog
{
    private const int Cap = 2000;
    private static readonly object Gate = new();
    private static readonly LinkedList<LogEntry> Buffer = new();

    /// <summary>
    /// Env var redirecting the log directory. The test projects set it so a test run does not write into the
    /// user's real diagnostics — they did, and it actively hindered triage: an investigation of the app log
    /// had to separate genuine failures from "store: f.json is unusable", which was a unit test exercising
    /// backup recovery on its own temp file.
    /// </summary>
    public const string LogDirVar = "FOUNDRY_LOG_DIR";

    public static string LogDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(LogDirVar);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Foundry", "logs")
                : overridden;
        }
    }

    private static string FilePath => Path.Combine(LogDir, $"foundry-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Raised after each entry (UI may subscribe for live updates).</summary>
    public static event Action<LogEntry>? Logged;

    public static void Log(string level, string category, string message, string? detail = null)
    {
        var entry = new LogEntry(DateTime.Now, level, category, message, detail);
        lock (Gate)
        {
            Buffer.AddLast(entry);
            while (Buffer.Count > Cap) Buffer.RemoveFirst();
            try
            {
                Directory.CreateDirectory(LogDir);
                var line = entry.Line + (detail is null ? "" : "  | " + detail.Replace("\r", " ").Replace("\n", " "));
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
        }
        try { Logged?.Invoke(entry); } catch { }
    }

    public static void Info(string category, string message, string? detail = null) => Log("INFO", category, message, detail);
    public static void Warn(string category, string message, string? detail = null) => Log("WARN", category, message, detail);
    public static void Error(string category, string message, string? detail = null) => Log("ERROR", category, message, detail);

    /// <summary>Audit an AI call: phase, model, input/output sizes, duration, outcome.</summary>
    public static void Ai(string phase, string model, int inChars, int outChars, long ms, bool ok, string? error = null) =>
        Log(ok ? "INFO" : "ERROR", "ai",
            $"{phase} · {model} · in {inChars} / out {outChars} chars · {ms} ms · {(ok ? "ok" : "ERROR")}", error);

    public static IReadOnlyList<LogEntry> Recent()
    {
        lock (Gate) return Buffer.ToList();
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
        Log("INFO", "app", "Log cleared.");
    }
}
