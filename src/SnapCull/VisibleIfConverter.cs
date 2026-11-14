using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SnapCull;

/// <summary>Visible when the value is true, a non-empty string, or any other non-null object.</summary>
public sealed class VisibleIfConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            _ => true,
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
