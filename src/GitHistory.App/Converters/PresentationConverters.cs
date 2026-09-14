using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GitHistory.Core.Models;

namespace GitHistory.App.Converters;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EnumSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true && Enum.TryParse<FileViewMode>(parameter?.ToString(), out var mode) ? mode : Binding.DoNothing;
}

public sealed class NoModalOpenConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => !values.Any(value => value is true);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PaletteShortcutConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "refresh" => "F5",
        "theme" => "Ctrl D",
        _ => ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Shows patch content and hunk positions; commit metadata is available in the adjacent details tab.</summary>
public sealed class DiffBodyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is IEnumerable<DiffLine> lines
        ? lines.Where(line => line.Kind != DiffLineKind.Header || line.Text.StartsWith("@@", StringComparison.Ordinal)).ToArray()
        : Array.Empty<DiffLine>();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ActivityExtentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<ActivityPoint> { Count: > 0 } points) return "No activity";
        return $"{points.Min(point => point.Date):MMM d} – {points.Max(point => point.Date):MMM d}  ·  Drag to select";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
