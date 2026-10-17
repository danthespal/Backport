using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Ps5Backport.App.ViewModels;

namespace Ps5Backport.App;

/// <summary>Status tone ("ok", "patched", "warn", "error", "muted") or log level -> the theme's status brush.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public static readonly ToneBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            LogLevel.Error or "error" => "StatusError",
            LogLevel.Warning or "warn" => "StatusWarn",
            "ok" => "StatusOk",
            "patched" => "StatusPatched",
            LogLevel.Info or "muted" => "StatusMuted",
            _ => "StatusMuted",
        };
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) ? brush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Code graph chip ("patched", "stub", "plt", "changed", "missing", "unknown", "fakelib", "entry") -> its theme brush.</summary>
public sealed class ChipBrushConverter : IValueConverter
{
    public static readonly ChipBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            "patched" or "modified" => "MagentaBrush",
            "new" => "StatusOk",
            "stub" or "unknown" or "pattern" => "StatusWarn",
            "plt" or "changed" or "missing" or "crash" => "StatusError",
            "fakelib" => "CyanBrush",
            _ => "StatusMuted",
        };
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) ? brush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Navigation rail: true when the bound <see cref="AppPage"/> equals the parameter; checking a rail button
/// selects its page (unchecking does nothing, the newly checked button sets the page).
/// </summary>
public sealed class PageIsConverter : IValueConverter
{
    public static readonly PageIsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>True when the string starts with the converter parameter (the Backport page's "⚠" console-evidence notes).</summary>
public sealed class StartsWithConverter : IValueConverter
{
    public static readonly StartsWithConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && parameter is string prefix && text.StartsWith(prefix, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
