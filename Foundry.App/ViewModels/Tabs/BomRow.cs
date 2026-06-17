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

// ---------------- BOM ----------------
/// <summary>Observable wrapper over a BOM line so live sourcing updates reflect in the table.</summary>
public sealed partial class BomRow : ObservableObject
{
    private readonly BomLine _line;
    public BomRow(BomLine line)
    {
        _line = line;
        _price = line.Price; _stock = line.Stock; _lead = line.Lead; _dist = line.Dist;
    }

    public int Qty => _line.Qty;
    public string Name => _line.Name;
    public string Note => _line.Note;
    public string Mpn => _line.Mpn;

    [ObservableProperty] private double _price;
    [ObservableProperty] private int _stock;
    [ObservableProperty] private string _lead;
    [ObservableProperty] private string _dist;

    public double Extended => Qty * Price;
    partial void OnPriceChanged(double value) => OnPropertyChanged(nameof(Extended));

    /// <summary>True once a live sourcing quote has been applied to this row; until then Price is a generated estimate.</summary>
    [ObservableProperty] private bool _isLivePrice;
    public string PriceSourceTag => IsLivePrice ? "LIVE" : "EST";
    partial void OnIsLivePriceChanged(bool value) => OnPropertyChanged(nameof(PriceSourceTag));

    public void Apply(SourcingQuote q) { Dist = q.Distributor; Price = q.UnitPrice; Stock = q.Stock; Lead = q.Lead; IsLivePrice = true; }

    // v2 G10: substitutes
    [ObservableProperty] private bool _showAlternates;
    [ObservableProperty] private bool _altBusy;
    [ObservableProperty] private bool _altLoaded;
    public ObservableCollection<Foundry.Core.Sourcing.Alternate> Alternates { get; } = new();
}
