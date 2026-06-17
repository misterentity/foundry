using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Config;
using Foundry.Core.Export;
using Foundry.Core.Firmware;
using Foundry.Core.Project;
using Foundry.Core.Simulation;
using Foundry.Core.Sourcing;
using Foundry.Core.Validation;
using Microsoft.Win32;

namespace Foundry.App.ViewModels;

public sealed partial class BomViewModel : TabViewModelBase
{
    private readonly Foundry.Core.Generation.ProjectGenerator? _reviser;
    [ObservableProperty] private string _sourcingStatus;
    [ObservableProperty] private bool _isRefreshing;

    public ObservableCollection<BomRow> Rows { get; }

    /// <summary>Raised to ask the shell to swap a part (revise + re-run downstream).</summary>
    public event Action<string>? SwapRequested;

    public BomViewModel(Project project, Foundry.Core.Generation.ProjectGenerator? reviser = null) : base(project)
    {
        _reviser = reviser;
        Rows = new ObservableCollection<BomRow>(project.Bom.Select(l => new BomRow(l)));
        var svc = SourcingService.Shared;
        _sourcingStatus = svc.IsLive
            ? $"live pricing via {svc.ProviderName}"
            : "offline — cached estimates · add a sourcing key in Settings for live pricing";
    }

    public double Total => Rows.Sum(r => r.Extended);
    public string TotalText => $"${Total:0.00}";
    public int Units => Rows.Sum(r => r.Qty);
    public string SubtotalLabel => $"Subtotal · {Rows.Count} lines · {Units} units";
    public string LinesLabel => $"BILL OF MATERIALS · {Rows.Count} LINES";
    public int LowStockCount => Rows.Count(r => r.Stock < 100);

    /// <summary>Real cost/line breakdown by distributor (replaces the old hardcoded substitutions).</summary>
    public IReadOnlyList<SourcingRow> ByDistributor => Rows
        .GroupBy(r => string.IsNullOrWhiteSpace(r.Dist) ? "—" : r.Dist)
        .Select(g => new SourcingRow
        {
            Distributor = g.Key, Lines = g.Count(), Cost = g.Sum(r => r.Extended),
            Status = g.Any(r => r.Stock < 100) ? "warn" : "ok",
        })
        .OrderByDescending(s => s.Cost).ToList();

    /// <summary>Expand a row and lazily fetch AI-suggested substitutes (PRD v2 G10).</summary>
    [RelayCommand]
    private async Task ToggleAlternates(BomRow? row)
    {
        if (row is null) return;
        row.ShowAlternates = !row.ShowAlternates;
        if (!row.ShowAlternates || row.AltLoaded || _reviser is null) return;
        row.AltBusy = true;
        try
        {
            var alts = await _reviser.SuggestAlternatesAsync(row.Name, row.Mpn);
            row.Alternates.Clear();
            foreach (var a in alts) row.Alternates.Add(a);
            row.AltLoaded = true;
            if (alts.Count == 0) row.Alternates.Add(new Foundry.Core.Sourcing.Alternate { Name = "No substitutes suggested.", Note = "" });
        }
        catch { }
        finally { row.AltBusy = false; }
    }

    // v2 G12: budget mode
    [ObservableProperty] private string _targetBudget = "";

    /// <summary>Ask the AI to bring the BOM under a target budget by substituting cheaper parts (PRD v2 G12).</summary>
    [RelayCommand]
    private void OptimizeForBudget()
    {
        var t = TargetBudget.Trim().TrimStart('$');
        if (!double.TryParse(t, out var budget) || budget <= 0) { SourcingStatus = "Enter a target budget (e.g. 25) first."; return; }
        SwapRequested?.Invoke(
            $"Rework the design to bring the total BOM cost under ${budget:0.00} by substituting cheaper but " +
            $"suitable, pin-compatible parts where possible. Keep the device fully functional; if a tradeoff is " +
            $"unavoidable, make the most reasonable choice. Current total is about {TotalText}.");
    }

    /// <summary>Swap a BOM part for a suggested alternate — revises the whole project.</summary>
    [RelayCommand]
    private void Swap(Foundry.Core.Sourcing.Alternate? alt)
    {
        if (alt is null || string.IsNullOrWhiteSpace(alt.Mpn)) return;
        SwapRequested?.Invoke(
            $"Replace the BOM part \"{alt.Replaces}\" with \"{alt.Name}\" (MPN {alt.Mpn}) and update the design, " +
            $"netlist and firmware to match the substitute. Keep everything else the same.");
    }

    [RelayCommand]
    private async Task RefreshPrices()
    {
        var svc = SourcingService.Shared;
        if (!svc.IsLive)
        {
            SourcingStatus = "offline — add a Nexar/Octopart key in Settings for live pricing";
            return;
        }
        IsRefreshing = true;
        try
        {
            int applied = 0;
            foreach (var row in Rows)
            {
                var q = await svc.GetQuoteAsync(row.Mpn);
                if (q is not null) { row.Apply(q); applied++; }
            }
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(TotalText));
            OnPropertyChanged(nameof(ByDistributor));
            OnPropertyChanged(nameof(LowStockCount));
            // Honest status: only claim live pricing for rows that actually got a live quote (see BomPricing).
            SourcingStatus = BomPricing.RefreshStatus(svc.ProviderName, applied, Rows.Count, DateTime.Now.ToString("HH:mm"));
        }
        finally { IsRefreshing = false; }
    }

    /// <summary>Write a DigiKey-format BOM CSV and open the BOM manager for one-click upload (PRD §8.7).</summary>
    [RelayCommand]
    private void Cart()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Foundry");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "digikey-bom.csv");
            File.WriteAllText(path, CartLinks.DigiKeyBomCsv(Project.Bom));
            OpenUrl(CartLinks.DigiKeyBomManager);
            SourcingStatus = $"DigiKey BOM CSV written to {path} — upload it in the BOM manager";
        }
        catch (Exception ex) { SourcingStatus = $"cart export failed: {ex.Message}"; }
    }

    /// <summary>Write a Mouser-format BOM CSV and open Mouser's BOM tool (PRD v2 G11).</summary>
    [RelayCommand]
    private void CartMouser()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Foundry");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mouser-bom.csv");
            File.WriteAllText(path, CartLinks.MouserBomCsv(Project.Bom));
            OpenUrl(CartLinks.MouserBom);
            SourcingStatus = $"Mouser BOM CSV written to {path} — import it in Mouser's BOM tool";
        }
        catch (Exception ex) { SourcingStatus = $"cart export failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void Buy(BomRow? row)
    {
        if (row is not null) OpenUrl(CartLinks.Search(row.Dist, row.Mpn));
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* best effort */ }
    }
}
