using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Foundry.App.ViewModels;
using Foundry.Core.Project;

namespace Foundry.App.Views.Tabs;

public partial class FirmwareView : UserControl
{
    public FirmwareView()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private FirmwareViewModel? Vm => DataContext as FirmwareViewModel;

    private static Brush B(string key) =>
        (Brush)(Application.Current.TryFindResource(key) ?? Brushes.Gainsboro);

    private void Render()
    {
        if (Vm is null || CodeHost is null) return;
        CodeHost.Children.Clear();
        var content = Vm.ActiveFile?.Content ?? "";
        var lines = content.Replace("\r\n", "\n").Split('\n');

        var lnBrush = B("Brush.InkFaint");
        for (int i = 0; i < lines.Length; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var ln = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontFamily = (FontFamily)FindResource("Font.Mono"),
                FontSize = 12.5,
                Foreground = lnBrush,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 14, 0),
            };
            Grid.SetColumn(ln, 0);

            var code = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("Font.Mono"),
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 18, 0),
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = 19,
            };
            Highlight(code, lines[i]);
            Grid.SetColumn(code, 1);

            row.Children.Add(ln);
            row.Children.Add(code);
            CodeHost.Children.Add(row);
        }
    }

    // Lightweight highlighter: comments, preprocessor, strings, keywords, numbers.
    private static readonly Regex Token = new(
        @"(?<comment>//.*$)|(?<pp>^\s*#\w+)|(?<str>""[^""]*"")|(?<kw>\b(?:void|int|float|bool|return|if|else|for|while|constexpr|uint64_t|uint32_t|const|true|false|INPUT|OUTPUT)\b)|(?<num>\b\d[\w.]*\b)",
        RegexOptions.Compiled);

    private void Highlight(TextBlock tb, string line)
    {
        if (line.TrimStart().StartsWith("//"))
        {
            tb.Inlines.Add(new Run(line) { Foreground = B("Brush.InkFaint"), FontStyle = FontStyles.Italic });
            return;
        }

        int last = 0;
        foreach (Match m in Token.Matches(line))
        {
            if (m.Index > last)
                tb.Inlines.Add(new Run(line[last..m.Index]) { Foreground = B("Brush.InkSoft") });

            Brush brush =
                m.Groups["comment"].Success ? B("Brush.InkFaint") :
                m.Groups["pp"].Success ? B("Brush.I2c") :
                m.Groups["str"].Success ? B("Brush.Ok") :
                m.Groups["kw"].Success ? B("Brush.Accent") :
                m.Groups["num"].Success ? B("Brush.Warn") :
                B("Brush.InkSoft");

            var run = new Run(m.Value) { Foreground = brush };
            if (m.Groups["comment"].Success) run.FontStyle = FontStyles.Italic;
            tb.Inlines.Add(run);
            last = m.Index + m.Length;
        }
        if (last < line.Length)
            tb.Inlines.Add(new Run(line[last..]) { Foreground = B("Brush.InkSoft") });
    }
}
