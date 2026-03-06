using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HandyPlaylistPlayer.App.Converters;

/// <summary>
/// Converts a <see cref="Color"/> or hex string to a <see cref="SolidColorBrush"/>.
/// Used in AXAML to show a color swatch when the binding source is a Color property.
/// </summary>
public class ColorToBrushConverter : IValueConverter
{
    public static readonly ColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c) return new SolidColorBrush(c);
        if (value is string s && Color.TryParse(s, out var pc)) return new SolidColorBrush(pc);
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
