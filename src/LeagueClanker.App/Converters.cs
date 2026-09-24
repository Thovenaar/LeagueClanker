using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LeagueClanker.App;

/// <summary>Visible for a non-empty string or collection; pass "invert" to show only when empty.</summary>
public sealed class HasContentToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasContent = value switch
        {
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            null => false,
            _ => true,
        };
        if (parameter is "invert")
            hasContent = !hasContent;
        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
