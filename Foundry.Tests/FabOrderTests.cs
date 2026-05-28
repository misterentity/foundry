using System.Net;
using System.Text;
using Foundry.Core.Pcb.Fab;

namespace Foundry.Tests;

// A fake HttpMessageHandler so the live PCBWay path can be exercised WITHOUT a real network call.
// Records the request it saw and returns a canned response. Mirrors the no-real-endpoints guardrail.
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    public int Calls;
    public HttpRequestMessage? LastRequest;
    public string? LastRequestBody;

    public FakeHttpHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}

// A handler that fails the test if it is ever called — proves the no-key path never touches HTTP.
internal sealed class ExplodingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Fail("No HTTP call should have been made on this path.");
        throw new InvalidOperationException("unreachable");
    }
}

// ---- BoardDimensions — pure Edge.Cuts bbox parser -----------------------------------------------

public class BoardDimensionsTests
{
    [Fact]
    public void FromKicadPcb_RectOutline_ReturnsBoundingBox()
    {
        // A 60 x 40 mm outline drawn as four Edge.Cuts lines (corners 10,10 .. 70,50).
        var board = """
            (kicad_pcb
              (gr_line (start 10 10) (end 70 10) (layer "Edge.Cuts") (width 0.1))
              (gr_line (start 70 10) (end 70 50) (layer "Edge.Cuts") (width 0.1))
              (gr_line (start 70 50) (end 10 50) (layer "Edge.Cuts") (width 0.1))
              (gr_line (start 10 50) (end 10 10) (layer "Edge.Cuts") (width 0.1))
            )
            """;
        var (w, h) = BoardDimensions.FromKicadPcb(board);
        Assert.Equal(60.0, w);
        Assert.Equal(40.0, h);
    }

    [Fact]
    public void FromKicadPcb_DecimalCoords_RoundTo2Dp()
    {
        var board = "(gr_line (start 0.125 0.0) (end 12.347 8.991) (layer \"Edge.Cuts\"))";
        var (w, h) = BoardDimensions.FromKicadPcb(board);
        Assert.Equal(12.22, w);
        Assert.Equal(8.99, h);
    }

    [Fact]
    public void FromKicadPcb_IgnoresNonEdgeCutsShapes()
    {
        // A copper line on F.Cu *after* the outline must NOT widen the board; only Edge.Cuts counts.
        var board = """
            (gr_line (start 5 5) (end 25 35) (layer "Edge.Cuts"))
            (gr_line (start 0 0) (end 200 200) (layer "F.Cu"))
            """;
        var (w, h) = BoardDimensions.FromKicadPcb(board);
        Assert.Equal(20.0, w);
        Assert.Equal(30.0, h);
    }

    [Fact]
    public void FromKicadPcb_EmptyOrGarbage_ReturnsDefault()
    {
        Assert.Equal(BoardDimensions.Default, BoardDimensions.FromKicadPcb(null));
        Assert.Equal(BoardDimensions.Default, BoardDimensions.FromKicadPcb(""));
        Assert.Equal(BoardDimensions.Default, BoardDimensions.FromKicadPcb("not a board at all"));
    }
}

// ---- FabEstimator — pure local quote math + handoff params ---------------------------------------

public class FabEstimatorTests
{
    [Fact]
    public void Estimate_SmallBatch2Layer_HitsFlatPromoTier()
    {
        // ≤100 cm², ≤5 pcs, 2-layer → base 4 + area(25cm²*0.06*1) + qty(5*0.30) = 4 + 1.5 + 1.5 = 7.00.
        var spec = new FabOrderSpec("x-fab.zip", WidthMm: 50, HeightMm: 50, Layers: 2, Quantity: 5);
        var q = FabEstimator.Estimate("JLCPCB", spec);

        Assert.Equal("JLCPCB", q.Provider);
        Assert.Equal(FabQuoteSource.Estimate, q.Source);
        Assert.Equal("USD", q.Currency);
        Assert.Equal(7.00m, q.Price);
        Assert.Equal(2, q.LeadTimeDays);
        Assert.Contains(q.Notes, n => n.Contains("NOT a live quote"));
    }

    [Fact]
    public void Estimate_LargerAndMore_CostsMore_AndLongerLead()
    {
        var small = FabEstimator.Estimate("PCBWay", new FabOrderSpec("z.zip", 50, 50, Quantity: 5));
        var big = FabEstimator.Estimate("PCBWay", new FabOrderSpec("z.zip", 150, 150, Quantity: 20));

        Assert.True(big.Price > small.Price);
        Assert.True(big.LeadTimeDays >= small.LeadTimeDays);
    }

    [Fact]
    public void Estimate_HouseMultiplier_ScalesPrice()
    {
        var spec = new FabOrderSpec("z.zip", 50, 50, Quantity: 5);
        var baseQ = FabEstimator.Estimate("X", spec, 1.0);
        var dearer = FabEstimator.Estimate("X", spec, 1.5);
        Assert.True(dearer.Price > baseQ.Price);
    }

    [Fact]
    public void HandoffParams_CarryBoardFactsAndZip()
    {
        var spec = new FabOrderSpec("C:/out/widget-fab.zip", 60, 40, Quantity: 10, BoardName: "widget");
        var (parms, clip) = FabEstimator.HandoffParams(spec);

        Assert.Equal("2", parms["Layers"]);
        Assert.Equal("10", parms["Quantity"]);
        Assert.Equal("widget", parms["Board name"]);
        Assert.Equal("C:/out/widget-fab.zip", parms["Fab ZIP"]);
        Assert.Contains("Quantity: 10", clip);
    }
}

// ---- JLCPCB provider — estimate + assisted handoff, never live, never HTTP -----------------------

public class JlcpcbProviderTests
{
    [Fact]
    public async Task Quote_IsEstimate_NeverLive()
    {
        var p = new JlcpcbProvider();
        Assert.True(p.NeedsApiKey);
        Assert.False(p.IsLive);

        var q = await p.QuoteAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(FabQuoteSource.Estimate, q.Source);
        Assert.Equal("JLCPCB", q.Provider);
    }

    [Fact]
    public async Task PrepareOrder_ReturnsHandoff_NotASubmittedOrder()
    {
        var p = new JlcpcbProvider();
        var h = await p.PrepareOrderAsync(new FabOrderSpec("C:/out/b-fab.zip", 50, 50));

        Assert.Equal(JlcpcbProvider.PortalUrl, h.PortalUrl);
        Assert.Equal("C:/out/b-fab.zip", h.ZipPath);
        Assert.Contains(h.Notes, n => n.Contains("never submits", StringComparison.OrdinalIgnoreCase));
        // The handoff describes a manual finish — it must not claim an order was placed.
        Assert.DoesNotContain(h.Notes, n => n.Contains("order placed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(h.Notes, n => n.Contains("submitted to", StringComparison.OrdinalIgnoreCase));
    }
}

// ---- PCBWay provider — live path via fake handler; no-key path never touches HTTP ----------------

public class PcbWayProviderTests
{
    [Fact]
    public async Task NoKey_ReturnsEstimate_AndNeverCallsHttp()
    {
        // An exploding handler proves the no-key path never reaches the network.
        using var p = new PcbWayProvider(apiKey: null, handler: new ExplodingHttpHandler());
        Assert.True(p.NeedsApiKey);
        Assert.False(p.IsLive);

        var q = await p.QuoteAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(FabQuoteSource.Estimate, q.Source);
    }

    [Fact]
    public async Task KeyedQuote_AgainstFakeHandler_ParsesLiveQuote()
    {
        var json = """
            { "price": 23.45, "currency": "USD", "buildDays": 3, "message": "in stock" }
            """;
        var handler = new FakeHttpHandler(HttpStatusCode.OK, json);
        using var p = new PcbWayProvider("test-key", handler);
        Assert.True(p.IsLive);

        var q = await p.QuoteAsync(new FabOrderSpec("z.zip", 60, 40, Quantity: 5));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(FabQuoteSource.Live, q.Source);
        Assert.Equal(23.45m, q.Price);
        Assert.Equal("USD", q.Currency);
        Assert.Equal(3, q.LeadTimeDays);
        // The key must travel as the documented api-key header to the published endpoint — and nowhere real.
        Assert.Equal(PcbWayProvider.BaseUrl + PcbWayProvider.QuoteEndpoint, handler.LastRequest!.RequestUri!.ToString());
        Assert.True(handler.LastRequest.Headers.TryGetValues("api-key", out var keys));
        Assert.Contains("test-key", keys!);
    }

    [Fact]
    public async Task KeyedQuote_Non2xx_DegradesToEstimate_NeverThrows()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.InternalServerError, "{}");
        using var p = new PcbWayProvider("test-key", handler);

        var q = await p.QuoteAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(FabQuoteSource.Estimate, q.Source);
        Assert.Contains(q.Notes, n => n.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task KeyedQuote_GarbageJson_DegradesToEstimate_NeverThrows()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "this is not json");
        using var p = new PcbWayProvider("test-key", handler);

        var q = await p.QuoteAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(FabQuoteSource.Estimate, q.Source);
    }

    [Fact]
    public async Task PrepareOrder_IsAssistedHandoff_NeverSubmits_EvenWhenKeyed()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        using var p = new PcbWayProvider("test-key", handler);

        var h = await p.PrepareOrderAsync(new FabOrderSpec("C:/out/b-fab.zip", 50, 50));

        Assert.Equal(0, handler.Calls); // preparing an order must NOT make a network call
        Assert.Equal(PcbWayProvider.PortalUrl, h.PortalUrl);
        Assert.Equal("C:/out/b-fab.zip", h.ZipPath);
        Assert.Contains(h.Notes, n => n.Contains("never submits", StringComparison.OrdinalIgnoreCase));
    }
}

// ---- NullFabProvider — always-available fallback -------------------------------------------------

public class NullFabProviderTests
{
    [Fact]
    public async Task IsOfflineEstimate_WithJlcpcbHandoff()
    {
        var p = new NullFabProvider();
        Assert.False(p.NeedsApiKey);
        Assert.False(p.IsLive);

        var q = await p.QuoteAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(FabQuoteSource.Estimate, q.Source);

        var h = await p.PrepareOrderAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(JlcpcbProvider.PortalUrl, h.PortalUrl);
    }
}

// ---- FabService — spec building + provider selection + orchestration -----------------------------

public class FabServiceTests
{
    [Fact]
    public void Select_PrefersKeyedPcbWay_ThenJlcpcb_ThenNull()
    {
        Assert.IsType<PcbWayProvider>(FabService.Select(pcbWayKey: "k", jlcpcbConfigured: true));
        Assert.IsType<JlcpcbProvider>(FabService.Select(pcbWayKey: null, jlcpcbConfigured: true));
        Assert.IsType<NullFabProvider>(FabService.Select(pcbWayKey: " ", jlcpcbConfigured: false));
    }

    [Fact]
    public void BuildSpec_DerivesSizeFromKicadPcb_AndDefaultsBoardNameFromZip()
    {
        var pcb = Path.Combine(Path.GetTempPath(), "foundry_fabspec_" + Guid.NewGuid().ToString("N")[..8] + ".kicad_pcb");
        File.WriteAllText(pcb, """
            (gr_line (start 0 0) (end 80 0) (layer "Edge.Cuts"))
            (gr_line (start 0 0) (end 0 30) (layer "Edge.Cuts"))
            """);
        try
        {
            var spec = FabService.BuildSpec("C:/out/gadget-fab.zip", pcb, quantity: 10);
            Assert.Equal(80.0, spec.WidthMm);
            Assert.Equal(30.0, spec.HeightMm);
            Assert.Equal(FabService.FabLayers, spec.Layers);
            Assert.Equal(10, spec.Quantity);
            Assert.Equal("gadget-fab", spec.BoardName);
        }
        finally
        {
            File.Delete(pcb);
        }
    }

    [Fact]
    public void BuildSpec_NoBoardFile_FallsBackToDefaultSize()
    {
        var spec = FabService.BuildSpec("C:/out/x-fab.zip");
        Assert.Equal(BoardDimensions.Default.WidthMm, spec.WidthMm);
        Assert.Equal(BoardDimensions.Default.HeightMm, spec.HeightMm);
        Assert.True(spec.Quantity >= 1);
    }

    [Fact]
    public async Task Service_ForwardsToProvider_AndReportsLiveness()
    {
        var svc = new FabService(new JlcpcbProvider());
        Assert.False(svc.IsLive);
        Assert.True(svc.NeedsApiKey);
        Assert.Equal("JLCPCB", svc.ProviderName);

        var q = await svc.QuoteAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(FabQuoteSource.Estimate, q.Source);

        var h = await svc.PrepareOrderAsync(new FabOrderSpec("z.zip", 50, 50));
        Assert.Equal(JlcpcbProvider.PortalUrl, h.PortalUrl);
    }
}
