using System.Net;
using System.Net.Http;
using System.Text;
using Foundry.Core.Sourcing;

namespace Foundry.Tests;

public class NexarSourcingTests
{
    // Routes the OAuth token POST and the GraphQL POST to canned bodies — exercises the whole live path
    // (token -> query -> Parse) against a captured Nexar response, so a schema rename can't silently pass.
    private sealed class NexarFake : HttpMessageHandler
    {
        private readonly string _graphql;
        public NexarFake(string graphql) => _graphql = graphql;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            var body = url.Contains("identity.nexar.com")
                ? """{"access_token":"tok","expires_in":3600}"""
                : _graphql;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static NexarSourcingProvider Provider(string graphql) =>
        new("id:secret", new HttpClient(new NexarFake(graphql)));

    [Fact]
    public async Task GetQuote_RealResponse_MapsDistributorPriceStock()
    {
        const string body = """
        {"data":{"supSearchMpn":{"results":[{"part":{"mpn":"ESP32-WROOM-32","bestDatasheet":{"url":"http://ds"},
        "sellers":[{"company":{"name":"DigiKey"},"offers":[{"inventoryLevel":1442,"clickUrl":"http://buy",
        "prices":[{"quantity":1,"price":8.50},{"quantity":10,"price":7.20}]}]}]}}]}}}
        """;
        var q = await Provider(body).GetQuoteAsync("ESP32-WROOM-32");
        Assert.NotNull(q);
        Assert.Equal("DigiKey", q!.Distributor);
        Assert.Equal(7.20, q.UnitPrice, 3);   // lowest price break for the offer
        Assert.Equal(1442, q.Stock);
    }

    [Fact]
    public async Task GetQuote_PrefersInStockOverCheaperOutOfStock()
    {
        const string body = """
        {"data":{"supSearchMpn":{"results":[{"part":{"mpn":"X","sellers":[
          {"company":{"name":"Cheap"},"offers":[{"inventoryLevel":0,"prices":[{"price":1.00}]}]},
          {"company":{"name":"InStock"},"offers":[{"inventoryLevel":500,"prices":[{"price":2.00}]}]}
        ]}}]}}}
        """;
        var q = await Provider(body).GetQuoteAsync("X");
        Assert.NotNull(q);
        Assert.Equal("InStock", q!.Distributor);   // in-stock wins over a cheaper out-of-stock offer
    }

    [Fact]
    public async Task GetQuote_NoResults_ReturnsNull()
    {
        var q = await Provider("""{"data":{"supSearchMpn":{"results":[]}}}""").GetQuoteAsync("nope");
        Assert.Null(q);
    }

    [Fact]
    public async Task GetQuote_SchemaDrift_ReturnsNullNotThrow()
    {
        // A renamed/dropped field (e.g. supSearchMpn -> supSearch) must degrade to null, never throw.
        var q = await Provider("""{"data":{"supSearch":{"hits":[]}}}""").GetQuoteAsync("x");
        Assert.Null(q);
    }

    [Fact]
    public void IsLive_RequiresBothClientIdAndSecret()
    {
        Assert.True(new NexarSourcingProvider("id:secret").IsLive);
        Assert.False(new NexarSourcingProvider("id-only").IsLive);
        Assert.False(new NexarSourcingProvider("").IsLive);
    }
}
