using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Foundry.App.ViewModels;

namespace Foundry.App.Views.Tabs;

public partial class ValidationView : UserControl
{
    public ValidationView()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderPowerBar();
    }

    private void RenderPowerBar()
    {
        if (DataContext is not ValidationViewModel vm || PowerHost is null) return;
        PowerHost.ColumnDefinitions.Clear();
        PowerHost.Children.Clear();

        int col = 0;
        foreach (var s in vm.PowerBudget)
        {
            PowerHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.Ma, GridUnitType.Star) });
            var brush = (Brush)(Application.Current.TryFindResource(s.BrushKey) ?? Brushes.Gray);
            var cell = new Border { Background = brush, Opacity = 0.85 };
            var label = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("Font.Mono"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x06, 0x06, 0x0A)),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 4, 4, 0),
                Text = $"{s.Label.ToUpperInvariant()}\n{s.Ma} mA",
            };
            cell.Child = label;
            Grid.SetColumn(cell, col++);
            PowerHost.Children.Add(cell);
        }
    }
}
