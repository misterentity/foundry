using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Foundry.Core.Update;

public sealed record UpdateInfo(
    string Version,
    string TagName,
    string Notes,
    string ReleaseUrl,
    string? InstallerUrl,
    string? InstallerName);

/// <summary>
/// Checks GitHub Releases for a newer version and downloads the installer asset. Mirrors the
/// "update from GitHub releases" pattern: GET /releases/latest, compare the tag to the running
/// version, and download the <c>.exe</c>/<c>.msi</c> asset to run. Defensive — any failure
/// surfaces as "no update / error" rather than throwing into the UI.
/// </summary>
public sealed class GitHubUpdater
{
    private readonly HttpClient _http;

    public GitHubUpdater(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Foundry-Updater");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public sealed record CheckResult(bool Ok, bool UpdateAvailable, UpdateInfo? Info, string Message);

    public async Task<CheckResult> CheckAsync(string owner, string repo, string currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return new CheckResult(false, false, null, "set the update repo in Settings");
        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new CheckResult(true, false, null, "no releases published yet");
            if (!resp.IsSuccessStatusCode)
                return new CheckResult(false, false, null, $"GitHub returned {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            string? installerUrl = null, installerName = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = a.GetProperty("browser_download_url").GetString();
                        installerName = name;
                        break;
                    }
                }
            }

            var latest = NormalizeVersion(tag);
            var current = NormalizeVersion(currentVersion);
            var info = new UpdateInfo(latest.ToString(), tag, notes, htmlUrl, installerUrl, installerName);

            return latest > current
                ? new CheckResult(true, true, info, $"update available: {tag}")
                : new CheckResult(true, false, info, $"you're on the latest ({currentVersion})");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, false, null, ex.Message);
        }
    }

    public async Task<string> DownloadAsync(string url, string fileName, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), fileName);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
        return dest;
    }

    /// <summary>True if <paramref name="candidateTag"/> is a newer version than <paramref name="currentVersion"/>.</summary>
    public static bool IsNewer(string candidateTag, string currentVersion) =>
        NormalizeVersion(candidateTag) > NormalizeVersion(currentVersion);

    private static Version NormalizeVersion(string raw)
    {
        var s = raw.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+' });   // drop pre-release/build metadata
        if (cut > 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }
}
