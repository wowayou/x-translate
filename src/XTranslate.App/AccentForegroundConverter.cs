using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace XTranslate.App;

/// <summary>
/// User-selected Windows accents can be very bright. Keep the native accent, but choose black or
/// white text by WCAG contrast instead of assuming white is readable. High contrast keeps the user's
/// system highlight text. This only customizes the primary action and the selected pin icon.
/// </summary>
public sealed class AccentForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (SystemParameters.HighContrast) return SystemColors.HighlightTextBrush;
        if (value is not Color color) return SystemColors.ControlTextBrush;
        return BestForeground(color) == Colors.Black ? Brushes.Black : Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    internal static Color BestForeground(Color background) =>
        Contrast(Colors.Black, background) >= Contrast(Colors.White, background) ? Colors.Black : Colors.White;

    internal static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    internal static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return Linear(color.R) * 0.2126 + Linear(color.G) * 0.7152 + Linear(color.B) * 0.0722;
    }
}
