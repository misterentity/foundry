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

// ---------------- Enclosure ----------------
/// <summary>One row in the Enclosure tab's parametric-slider panel (PRD v2 Phase D).</summary>
public sealed partial class ScadParamRow : ObservableObject
{
    private readonly Action<ScadParamRow> _onChanged;
    public ScadParamRow(Foundry.Core.Cad.ScadParam p, Action<ScadParamRow> onChanged)
    {
        Name = p.Name; DisplayName = p.DisplayName;
        Min = p.Min; Max = p.Max; Step = p.Step;
        _value = p.Value;
        _onChanged = onChanged;
    }
    public string Name { get; }
    public string DisplayName { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    [ObservableProperty] private double _value;
    public string ValueText => Step >= 1 ? Value.ToString("0") : Value.ToString("0.##");
    partial void OnValueChanged(double value) { OnPropertyChanged(nameof(ValueText)); _onChanged(this); }
}
