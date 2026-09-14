using System.Globalization;
using Avalonia.Data.Converters;

namespace BlurLink.Shell.Converters;

/// <summary>Non-empty string → true. For IsVisible bindings that replace the
/// WPF NonEmptyToVis converter (none exists on the Shell side yet).</summary>
public sealed class NonEmptyToVisibleConverter : IValueConverter
{
    public static readonly NonEmptyToVisibleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
