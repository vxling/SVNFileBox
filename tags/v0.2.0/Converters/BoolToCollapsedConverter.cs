using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SVNFileBox.Converters;

public class BoolToCollapsedConverter : IValueConverter
{
    // true → Collapsed, false → Visible (inverted visibility)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
