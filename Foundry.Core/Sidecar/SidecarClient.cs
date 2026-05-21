using System.Net.Http;
using System.Text;

namespace Foundry.Core.Sidecar;

public sealed record EnclosureMesh(byte[] Stl, string Kernel, int Triangles, string OuterMm);

/// <summary>HTTP client for the CAD sidecar on 127.0.0.1 (PRD §5).</summary>
public sealed class SidecarClient
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public SidecarClient(string baseUrl, HttpClient? http = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>POST the enclosure schema and return the generated STL + stats.</summary>
    public async Task<EnclosureMesh> BuildEnclosureAsync(string schemaJson, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/enclosure")
        {
            Content = new StringContent(schemaJson, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var stl = await resp.Content.ReadAsByteArrayAsync(ct);

        string Header(string name) =>
            resp.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : "";
        int.TryParse(Header("X-Foundry-Triangles"), out var tris);
        return new EnclosureMesh(stl, Header("X-Foundry-Kernel"), tris, Header("X-Foundry-Outer"));
    }
}
