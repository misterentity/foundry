using Foundry.Core.Project;
using Foundry.Core.Sourcing;

namespace Foundry.Tests;

public class CartLinksTests
{
    [Fact]
    public void Search_RoutesPerDistributor_AndEscapes()
    {
        Assert.StartsWith("https://www.digikey.com/", CartLinks.Search("DigiKey", "ESP32-DEVKITC-32E"));
        Assert.StartsWith("https://www.mouser.com/", CartLinks.Search("Mouser", "MCP1700-3302E/TO"));
        Assert.StartsWith("https://www.amazon.com/", CartLinks.Search("Amazon", "SEN-CAP-01"));
        // '/' in the MPN must be URL-encoded
        Assert.Contains("MCP1700-3302E%2FTO", CartLinks.Search("Mouser", "MCP1700-3302E/TO"));
    }

    [Fact]
    public void DigiKeyBomCsv_HasHeaderAndQtyMpnRows()
    {
        var bom = DemoData.CreateSoilMoistureProject().Bom;
        var csv = CartLinks.DigiKeyBomCsv(bom);
        var lines = csv.Replace("\r\n", "\n").TrimEnd().Split('\n');

        Assert.Equal("Quantity,Part Number", lines[0]);
        Assert.Equal(bom.Count + 1, lines.Length);
        Assert.Contains(lines, l => l == "1,ESP32-DEVKITC-32E");
        Assert.Contains(lines, l => l == "2,TL3301AF260QG"); // qty 2 tact switches
    }
}

public class SourcingServiceTests
{
    private sealed class FakeProvider : ISourcingProvider
    {
        public int Calls;
        public string Name => "fake";
        public bool IsLive => true;
        public Task<SourcingQuote?> GetQuoteAsync(string mpn, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<SourcingQuote?>(new SourcingQuote(mpn, "DigiKey", 1.23, 500, "Stock"));
        }
    }

    [Fact]
    public async Task Caches_PerMpn()
    {
        var fake = new FakeProvider();
        var svc = new SourcingService(fake);

        var a = await svc.GetQuoteAsync("ABC");
        var b = await svc.GetQuoteAsync("ABC"); // cached — no second provider call
        Assert.Equal(1, fake.Calls);
        Assert.Equal(1.23, a!.UnitPrice);
        Assert.Same(a, b);
    }

    [Fact]
    public async Task Offline_ReturnsNoQuotes_AndIsNotLive()
    {
        var svc = new SourcingService(new NullSourcingProvider());
        Assert.False(svc.IsLive);
        Assert.Null(await svc.GetQuoteAsync("ABC"));
        var quotes = await svc.GetQuotesAsync(new[] { "ABC", "DEF" });
        Assert.Empty(quotes);
    }
}
