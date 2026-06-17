using Foundry.App.ViewModels;
using Foundry.Core.Project;
using Foundry.Core.Sourcing;

namespace Foundry.App.Tests;

// First automated coverage of the Foundry.App ViewModel layer — exercises the Phase 2 BOM honesty fix at the
// VM level (the layer that historically shipped regressions green because nothing referenced Foundry.App).
public class BomViewModelTests
{
    private sealed class FakeProvider : ISourcingProvider
    {
        private readonly SourcingQuote? _quote;
        public FakeProvider(SourcingQuote? quote) => _quote = quote;
        public string Name => "Nexar";
        public bool IsLive => true;
        public Task<SourcingQuote?> GetQuoteAsync(string mpn, CancellationToken ct = default) => Task.FromResult(_quote);
    }

    [Fact]
    public async Task RefreshPrices_AllQuotesFail_DoesNotClaimLivePricing()
    {
        SourcingService.Shared = new SourcingService(new FakeProvider(null));   // live key, but every quote null
        var vm = new BomViewModel(DemoData.CreateSoilMoistureProject());

        await vm.RefreshPricesCommand.ExecuteAsync(null);

        Assert.DoesNotContain("live pricing", vm.SourcingStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estimate", vm.SourcingStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vm.Rows, r => r.IsLivePrice);   // no row falsely tagged live
    }

    [Fact]
    public async Task RefreshPrices_QuoteApplied_TagsRowLiveAndReportsLive()
    {
        var quote = new SourcingQuote("X", "DigiKey", 1.23, 500, "Stock");
        SourcingService.Shared = new SourcingService(new FakeProvider(quote));
        var vm = new BomViewModel(DemoData.CreateSoilMoistureProject());

        await vm.RefreshPricesCommand.ExecuteAsync(null);

        Assert.Contains("live pricing", vm.SourcingStatus, StringComparison.OrdinalIgnoreCase);
        Assert.All(vm.Rows, r => Assert.True(r.IsLivePrice));
        Assert.All(vm.Rows, r => Assert.Equal("LIVE", r.PriceSourceTag));
    }

    [Fact]
    public void BomRow_DefaultsToEstimate_BeforeAnyQuote()
    {
        var vm = new BomViewModel(DemoData.CreateSoilMoistureProject());
        Assert.All(vm.Rows, r => Assert.False(r.IsLivePrice));
        Assert.All(vm.Rows, r => Assert.Equal("EST", r.PriceSourceTag));
    }
}
