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

    public sealed record ScadResult(bool Ok, byte[] Bytes, string Format, string Error);

    /// <summary>POST a raw OpenSCAD script and return the rendered mesh (PRD v2 Phase A).</summary>
    public async Task<ScadResult> RenderScadAsync(string scad, string format = "stl", CancellationToken ct = default)
    {
        var body = "{\"scad\":" + System.Text.Json.JsonSerializer.Serialize(scad ?? "") +
                   ",\"format\":\"" + (format == "3mf" ? "3mf" : "stl") + "\"}";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/enclosure/scad")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return new ScadResult(false, Array.Empty<byte>(), "", err);
        }
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        var fmt = resp.Headers.TryGetValues("X-Foundry-Format", out var v) ? string.Concat(v) : format;
        return new ScadResult(true, data, fmt, "");
    }
}
