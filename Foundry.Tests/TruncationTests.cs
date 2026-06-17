using System.Net;
using System.Net.Http;
using Foundry.Core.Ai;
using Foundry.Core.Generation;

namespace Foundry.Tests;

public class TruncationTests
{
    // A minimal but complete, parseable Foundry design JSON (mirrors GenerationTests' fixture).
    private const string Design = """
    {
      "title": "Plant Pal",
      "summary": "A battery soil sensor.",
      "subsystems": [{"role":"Controller","name":"ESP32","mpn":"ESP32-DEVKITC-32E","specs":[["Logic","3.3 V"]]}],
      "components": [
        {"alias":"MCU","ref":"esp32","name":"ESP32","logicV":3.3,"inputV":[3.0,5.5],"currentMa":80,
         "pins":[{"name":"3V3","kind":"power"},{"name":"GND","kind":"ground"},{"name":"GPIO34","kind":"analog"}]},
        {"alias":"SENSOR","ref":"cap","name":"Soil sensor","logicV":3.3,"currentMa":5,
         "pins":[{"name":"VCC","kind":"power"},{"name":"GND","kind":"ground"},{"name":"AOUT","kind":"output"}]}
      ],
      "bom": [{"qty":1,"name":"ESP32","mpn":"ESP32-DEVKITC-32E","price":8.5,"stock":1442,"lead":"Stock","dist":"DigiKey","note":"MCU"}],
      "connections": [{"from":"MCU.3V3","to":"SENSOR.VCC","net":"power"},
                      {"from":"MCU.GND","to":"SENSOR.GND","net":"ground"},
                      {"from":"MCU.GPIO34","to":"SENSOR.AOUT","net":"signal"}],
      "enclosure": {"inner":[60,40,25],"wall":2.0,"lid":"snap","standoffs":4},
      "firmwarePlatform": "Arduino C++",
      "assembly": [{"title":"Wire it","body":"Connect the sensor.","chips":["GPIO34"]}]
    }
    """;

    // ---- AnthropicClient throws on a token-cap cutoff instead of returning the partial text ----------
    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly string _body;
        public JsonHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task CompleteAsync_StopReasonMaxTokens_Throws()
    {
        var body = """{"content":[{"type":"text","text":"{\"partial\": tru"}],"stop_reason":"max_tokens"}""";
        using var http = new HttpClient(new JsonHandler(body));
        var client = new AnthropicClient("sk-test", maxTokens: 16384, http: http);
        await Assert.ThrowsAsync<TruncatedResponseException>(() =>
            client.CompleteAsync("sys", "user", "claude-opus-4-8"));
    }

    [Fact]
    public async Task CompleteAsync_NormalStop_ReturnsText()
    {
        var body = """{"content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn"}""";
        using var http = new HttpClient(new JsonHandler(body));
        var client = new AnthropicClient("sk-test", maxTokens: 16384, http: http);
        var text = await client.CompleteAsync("sys", "user", "claude-opus-4-8");
        Assert.Equal("hello", text);
    }

    // ---- generation path: truncation is NOT silently accepted -----------------------------------------
    // Always-truncating design pass -> Ok=false (after the retry), never a half-built project as "Generated".
    private sealed class AlwaysTruncatesAi : IAnthropicClient
    {
        public bool HasKey => true;
        public Task<ModelListResult> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModelListResult(true, ModelCatalog.Fallback, null));
        public Task<string> CompleteAsync(string s, string u, string m, CancellationToken ct = default) =>
            throw new TruncatedResponseException("{\"title\": \"half", 16384);
    }

    [Fact]
    public async Task GenerateAsync_DesignTruncated_ReturnsNotOk()
    {
        var gen = new ProjectGenerator(new AlwaysTruncatesAi(), "claude-opus-4-8");
        var r = await gen.GenerateAsync("a soil moisture sensor");
        Assert.False(r.Ok);
        Assert.Null(r.Project);
    }

    // Design succeeds, but the firmware enrichment pass truncates -> the project is still Ok (deterministic
    // firmware fallback), and we NEVER ship a truncated AI firmware. Proves the firmware blocker is closed.
    private sealed class DesignOkFirmwareTruncatesAi : IAnthropicClient
    {
        private readonly string _designJson;
        private int _calls;
        public DesignOkFirmwareTruncatesAi(string designJson) => _designJson = designJson;
        public bool HasKey => true;
        public Task<ModelListResult> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModelListResult(true, ModelCatalog.Fallback, null));
        public Task<string> CompleteAsync(string s, string u, string m, CancellationToken ct = default)
        {
            // first call = the design pass (succeeds); any later call (firmware enrichment) = truncated.
            if (System.Threading.Interlocked.Increment(ref _calls) == 1) return Task.FromResult(_designJson);
            throw new TruncatedResponseException("{\"files\":[{\"name\":\"main.ino\",\"content\":\"void set", 16384);
        }
    }

    [Fact]
    public async Task GenerateAsync_FirmwareTruncated_KeepsProjectWithDeterministicFirmware()
    {
        var gen = new ProjectGenerator(new DesignOkFirmwareTruncatesAi(Design), "claude-opus-4-8");
        var r = await gen.GenerateAsync("a soil moisture sensor");
        Assert.True(r.Ok);                                   // the design itself is fine
        Assert.NotNull(r.Project);
        Assert.NotEmpty(r.Project!.Firmware.Files);          // deterministic firmware present, not truncated AI output
    }
}
