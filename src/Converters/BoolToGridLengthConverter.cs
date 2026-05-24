using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SVNFileBox.Converters;

/// <summary>
/// Converts bool to GridLength: true → 40px (visible), false → 0 (hidden).
/// </summary>
public class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return new GridLength(40);
        return new GridLength(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}