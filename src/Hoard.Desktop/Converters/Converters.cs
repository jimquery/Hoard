using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Hoard.Desktop.Converters;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → SolidColorBrush; null/invalid → a neutral gray.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Fallback = new(Color.FromRgb(0x8a, 0x89, 0x86));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch (FormatException) { }
        }
        return Fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Negative amounts in the theme's "critical" color, positive in the primary text color.</summary>
public sealed class AmountToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var negative = value is decimal d ? d < 0 : value is double x && x < 0;
        var key = negative ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush";
        return Application.Current.TryFindResource(key) ?? Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Two-way: true when the bound string equals the converter parameter (for radio buttons over a string property).</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter! : Binding.DoNothing;
}
