using Foundry.Core.Sourcing;
using Foundry.Core.Project;

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

// Price provenance existed only as a view-model flag that no XAML bound, so a generated "1,442 in stock ·
// DigiKey · Stock" rendered identically to a real distributor lookup -- and the Overview tab turned it into
// the headline "All in stock", a sourcing claim about parts nobody had looked up.
public class BomProvenanceTests
{
    private static BomLine Est(int stock = 1442, string lead = "Stock") =>
        new() { Qty = 1, Name = "ESP32 DevKit v1", Mpn = "ESP32-DEVKITC-32E", Price = 8.5,
                Stock = stock, Lead = lead, Dist = "DigiKey" };

    private static BomLine Live(int stock, string lead = "Stock")
    {
        var l = Est(stock, lead);
        l.PriceSource = "DigiKey";
        l.PricedAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        return l;
    }

    [Fact]
    public void AGeneratedLine_IsNotLive()
    {
        Assert.False(Est().IsLive);
        Assert.Equal("EST", BomPricing.SourceTag(Est()));
    }

    [Fact]
    public void AQuotedLine_IsLive()
    {
        Assert.True(Live(1442).IsLive);
        Assert.Equal("LIVE", BomPricing.SourceTag(Live(1442)));
    }

    // The invented integer is the most dangerous field: it reads as inventory.
    [Fact]
    public void AnEstimateHasNoStockOrLeadToShow()
    {
        Assert.Equal(BomPricing.Unknown, BomPricing.StockText(Est()));
        Assert.Equal(BomPricing.Unknown, BomPricing.LeadText(Est()));
    }

    [Fact]
    public void AQuotedLineShowsItsRealFigures()
    {
        Assert.Equal("1,442", BomPricing.StockText(Live(1442)));
        Assert.Equal("Stock", BomPricing.LeadText(Live(1442)));
    }

    // LowStock drove a red/green dot. Against an estimate it was decorating a guess.
    [Fact]
    public void LowStock_IsFalseForAnEstimate_WhateverTheNumberSays()
    {
        Assert.False(Est(stock: 3).LowStock);
        Assert.True(Live(3).LowStock);
        Assert.False(Live(3000).LowStock);
    }

    // ---- the Overview headline ----

    [Fact]
    public void WithNoLiveQuotes_StockIsReportedUnknown_NotHealthy()
    {
        var bom = new List<BomLine> { Est(1442), Est(980) };
        Assert.Equal("stock not checked", BomPricing.StockSummary(bom));
        Assert.DoesNotContain("in stock", BomPricing.StockSummary(bom));
    }

    [Fact]
    public void WithEveryLineQuotedAndHealthy_ItSaysAllInStock() =>
        Assert.Equal("All in stock", BomPricing.StockSummary(new List<BomLine> { Live(1442), Live(980) }));

    [Fact]
    public void WithEveryLineQuoted_LowOnesAreCounted() =>
        Assert.Equal("1 low-stock", BomPricing.StockSummary(new List<BomLine> { Live(1442), Live(12) }));

    // Partial coverage must say so: "All in stock" over 2 of 5 checked lines is still a claim about 5.
    [Fact]
    public void PartialCoverage_StatesHowManyWereChecked() =>
        Assert.Equal("All in stock (2/3 checked)",
            BomPricing.StockSummary(new List<BomLine> { Live(1442), Live(980), Est(500) }));

    [Fact]
    public void AnEmptyBom_SaysSo() =>
        Assert.Equal("no parts", BomPricing.StockSummary(new List<BomLine>()));

    // A distributor group nobody checked must not render with the same "ok" as a checked one.
    [Fact]
    public void AnUncheckedDistributorGroup_IsUnknownNotOk()
    {
        Assert.Equal("unknown", BomPricing.GroupStatus(new[] { Est(1442), Est(980) }));
        Assert.Equal("ok", BomPricing.GroupStatus(new[] { Live(1442) }));
        Assert.Equal("warn", BomPricing.GroupStatus(new[] { Live(12) }));
    }

    // ---- the CSV is what someone actually orders from ----

    [Fact]
    public void TheCsv_LabelsEachLineAndOmitsInventedStock()
    {
        var p = new Project { Title = "T", Bom = new List<BomLine> { Est(1442), Live(980, "2 wks") } };
        var csv = Foundry.Core.Export.Exporters.BomCsv(p);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("Source", lines[0]);
        Assert.Contains(",EST,,", lines[1]);          // estimate: tagged, stock column empty
        Assert.DoesNotContain("1442", lines[1]);
        Assert.Contains(",LIVE,980,", lines[2]);      // quote: tagged and reported
        Assert.Contains("2 wks", lines[2]);
    }

    // Every data row must have the same column count as the header, subtotal included.
    [Fact]
    public void TheCsvStaysRectangular()
    {
        var p = new Project { Title = "T", Bom = new List<BomLine> { Est(), Live(980) } };
        var lines = Foundry.Core.Export.Exporters.BomCsv(p)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cols = lines[0].Split(',').Length;
        Assert.All(lines, l => Assert.Equal(cols, l.Split(',').Length));
    }
}
