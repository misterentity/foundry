using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Foundry.App.Converters;

internal static class Brushes
{
    public static Brush Res(string key) =>
        (Brush)(Application.Current.TryFindResource(key) ?? System.Windows.Media.Brushes.Magenta);
}

/// <summary>net name (power/ground/signal/i2c) → wire brush.</summary>
public sealed class NetBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value as string)?.ToLowerInvariant() switch
        {
            "power"  => Brushes.Res("Brush.Power"),
            "ground" => Brushes.Res("Brush.Ground"),
            "i2c"    => Brushes.Res("Brush.I2c"),
            _        => Brushes.Res("Brush.Signal"),
        };

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>severity (info/warn/fail/pass/ok) → brush.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value as string)?.ToLowerInvariant() switch
        {
            "warn" => Brushes.Res("Brush.Warn"),
            "fail" => Brushes.Res("Brush.Fail"),
            "info" => Brushes.Res("Brush.Info"),
            "pass" or "ok" => Brushes.Res("Brush.Ok"),
            _ => Brushes.Res("Brush.InkSoft"),
        };

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>severity → tag background tint brush.</summary>
public sealed class SeverityTagBgConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value as string)?.ToLowerInvariant() switch
        {
            "warn" => Brushes.Res("Brush.TagWarnBg"),
            "fail" => Brushes.Res("Brush.TagFailBg"),
            "info" => Brushes.Res("Brush.TagInfoBg"),
            "pass" or "ok" => Brushes.Res("Brush.TagOkBg"),
            "accent" => Brushes.Res("Brush.TagAccentBg"),
            _ => Brushes.Res("Brush.Surface1"),
        };

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>severity → tag border tint brush.</summary>
public sealed class SeverityTagBorderConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value as string)?.ToLowerInvariant() switch
        {
            "warn" => Brushes.Res("Brush.TagWarnBorder"),
            "fail" => Brushes.Res("Brush.TagFailBorder"),
            "info" => Brushes.Res("Brush.TagInfoBorder"),
            "pass" or "ok" => Brushes.Res("Brush.TagOkBorder"),
            "accent" => Brushes.Res("Brush.TagAccentBorder"),
            _ => Brushes.Res("Brush.Hairline3"),
        };

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>severity → finding strip background wash.</summary>
public sealed class SeverityFindingBgConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value as string)?.ToLowerInvariant() switch
        {
            "warn" => Brushes.Res("Brush.FindWarnBg"),
            "fail" => Brushes.Res("Brush.FindFailBg"),
            "info" => Brushes.Res("Brush.FindInfoBg"),
            "pass" or "ok" => Brushes.Res("Brush.FindPassBg"),
            _ => Brushes.Res("Brush.Surface0"),
        };

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>bool → Visibility (true=Visible). Pass "invert" param to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = value is bool v && v;
        if ((p as string) == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>null/empty string → Collapsed, else Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        if ((p as string) == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>bool equality against a string param → for tab/screen highlighting.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>value == param → accent brush (active), else transparent. For tab underlines.</summary>
public sealed class ActiveTabBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Brushes.Res("Brush.Accent")
            : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>value == param → ink (active) else ink-mute. For tab labels.</summary>
public sealed class ActiveTabInkConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Brushes.Res("Brush.Ink")
            : Brushes.Res("Brush.InkMute");

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>bool → GridLength (true=value or Auto, false=0). Param is the visible length, default 360.</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var on = value is bool b && b;
        if (!on) return new GridLength(0);
        if (p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var px))
            return new GridLength(px);
        return new GridLength(360);
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>low-stock (int &lt; 100) → warn/ok brush.</summary>
public sealed class StockBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var n = value is int i ? i : 0;
        return n < 100 ? Brushes.Res("Brush.Warn") : Brushes.Res("Brush.Ok");
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
