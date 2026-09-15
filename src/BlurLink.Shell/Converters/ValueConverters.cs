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

/// <summary>Task 12: a Refusals row — a <c>(string Text, long Count)</c> tuple —
/// renders as "count × explanation". Tuple elements are fields (Item1/Item2),
/// not properties, so a row template cannot bind Text/Count directly; it binds
/// the whole row through this converter instead.</summary>
public sealed class RefusalRowConverter : IValueConverter
{
    public static readonly RefusalRowConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ValueTuple<string, long> row ? $"{row.Item2} × {row.Item1}" : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
