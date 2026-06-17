using Foundry.Core.Sourcing;

namespace Foundry.Tests;

public class BomPricingTests
{
    // The fix: zero applied quotes (bad key / outage / schema drift) must NOT be reported as live pricing.
    [Fact]
    public void ZeroQuotesApplied_DoesNotClaimLivePricing()
    {
        var s = BomPricing.RefreshStatus("Nexar", applied: 0, total: 8, timeHhmm: "14:32");
        Assert.DoesNotContain("live pricing", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estimate", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllQuotesApplied_ReportsLiveAndTime()
    {
        var s = BomPricing.RefreshStatus("Nexar", applied: 8, total: 8, timeHhmm: "14:32");
        Assert.Contains("live pricing", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14:32", s);
    }

    [Fact]
    public void PartialQuotesApplied_ReportsCoverageHonestly()
    {
        var s = BomPricing.RefreshStatus("Nexar", applied: 3, total: 8, timeHhmm: "14:32");
        Assert.Contains("3/8", s);
        Assert.Contains("estimate", s, StringComparison.OrdinalIgnoreCase);   // says the rest are estimates
    }
}
